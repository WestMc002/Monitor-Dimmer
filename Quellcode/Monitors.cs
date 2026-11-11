// Monitor Dimmer – Helligkeit und Kontrast der Monitore über DDC/CI
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace MonitorDimmer
{
    internal sealed class PhysicalMonitor
    {
        public int Index;
        public IntPtr Handle;
        public string Description;
        public string DeviceName;
        /// <summary>Feste Kennung (Typ und Anschluss), z. B. "GSM5B09#5&amp;2a1c3b7f&amp;0&amp;UID4353"; leer, wenn Windows keine liefert.</summary>
        public string Id = "";
        public NativeMethods.RECT Bounds;
        public uint MaxBrightness = 100;
        public uint MaxContrast = 100;
    }

    internal sealed class MonitorSnapshot
    {
        public int Index;
        public string Description;
        public string DeviceName;
        public System.Drawing.Rectangle Bounds;
        public int Brightness = -1;
        public int BrightnessMax = 100;
        public int Contrast = -1;
        public int ContrastMax = 100;
        public string PositionName = "";

        public bool IsPrimary { get { return Bounds.X == 0 && Bounds.Y == 0; } }
    }

    /// <summary>
    /// Steuert Helligkeit/Kontrast per DDC/CI. Alle Zugriffe laufen nacheinander in einem eigenen Thread,
    /// weil DDC/CI langsam ist und die Oberfläche nicht blockieren soll.
    /// </summary>
    internal sealed class MonitorManager : IDisposable
    {
        private const byte VcpBrightness = 0x10;
        private const byte VcpContrast = 0x12;

        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private List<PhysicalMonitor> _monitors = new List<PhysicalMonitor>();
        private readonly string _mapFile;
        private readonly HashSet<int> _failed = new HashSet<int>();

        /// <summary>Helligkeit in mehreren Schritten überblenden statt springen.</summary>
        public volatile bool Smooth = true;

        /// <summary>Ein Monitor nimmt die Werte nicht an (kommt aus dem DDC-Thread, je Monitor nur einmal bis er wieder reagiert).</summary>
        public event Action<string> Problem;

        /// <summary>mapFile: Datei mit der festen Zuordnung Handle-Nummer = Monitor (null: Nummern in der Reihenfolge von Windows).</summary>
        public MonitorManager(string mapFile = null)
        {
            _mapFile = mapFile;
            _thread = new Thread(Worker) { IsBackground = true, Name = "DDC/CI" };
            _thread.Start();
        }

        public int Count { get { return _monitors.Count; } }

        private void Worker()
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception ex) { Log.Error("DDC/CI-Auftrag fehlgeschlagen: {0}", ex); }
            }
        }

        public void Enqueue(Action action)
        {
            if (!_queue.IsAddingCompleted) _queue.Add(action);
        }

        /// <summary>Führt eine Aktion im DDC-Thread aus und wartet höchstens timeoutMs darauf.</summary>
        public bool Invoke(Action action, int timeoutMs)
        {
            using (var done = new ManualResetEvent(false))
            {
                Exception error = null;
                Enqueue(() =>
                {
                    try { action(); }
                    catch (Exception ex) { error = ex; }
                    finally { try { done.Set(); } catch (ObjectDisposedException) { } }
                });
                bool ok = done.WaitOne(timeoutMs);
                if (error != null) Log.Error("DDC/CI: {0}", error);
                return ok;
            }
        }

        // ------------------------------------------------------------------

        public void Refresh()
        {
            Enqueue(RefreshInternal);
        }

        private void RefreshInternal()
        {
            ReleaseAll();
            var list = new List<PhysicalMonitor>();
            NativeMethods.MonitorEnumProc proc = (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT r, IntPtr data) =>
            {
                var info = new NativeMethods.MONITORINFOEX();
                info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.MONITORINFOEX));
                NativeMethods.GetMonitorInfo(hMon, ref info);

                uint count;
                if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) || count == 0)
                    return true;
                var arr = new NativeMethods.PHYSICAL_MONITOR[count];
                if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
                    return true;
                var ids = MonitorIds(info.szDevice);
                for (int j = 0; j < arr.Length; j++)
                {
                    list.Add(new PhysicalMonitor
                    {
                        Index = list.Count,
                        Handle = arr[j].hPhysicalMonitor,
                        Description = arr[j].szPhysicalMonitorDescription,
                        DeviceName = info.szDevice,
                        Id = j < ids.Count ? ids[j] : "",
                        Bounds = info.rcMonitor
                    });
                }
                return true;
            };
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            GC.KeepAlive(proc);
            AssignHandles(list);

            foreach (var m in list)
            {
                uint cur, max;
                if (TryRead(m.Handle, VcpBrightness, out cur, out max) && max > 0) m.MaxBrightness = max;
                if (TryRead(m.Handle, VcpContrast, out cur, out max) && max > 0) m.MaxContrast = max;
                Log.Info("Monitor #{0}: \"{1}\" {2} Position X={3} Y={4} Breite={5} Hoehe={6} (max. Helligkeit {7}, max. Kontrast {8})",
                    m.Index, m.Description, m.DeviceName, m.Bounds.Left, m.Bounds.Top,
                    m.Bounds.Right - m.Bounds.Left, m.Bounds.Bottom - m.Bounds.Top, m.MaxBrightness, m.MaxContrast);
            }
            _monitors = list;
            Log.Info("{0} physische Monitore gefunden", list.Count);
        }

        // ------------------------------------------------------------------ feste Zuordnung Handle = Monitor

        /// <summary>Kennungen der aktiven Monitore an einer Grafikausgabe (\\.\DISPLAY1), in der Reihenfolge von Windows.</summary>
        private static List<string> MonitorIds(string device)
        {
            var ids = new List<string>();
            try
            {
                for (uint i = 0; i < 16; i++)
                {
                    var dd = new NativeMethods.DISPLAY_DEVICE();
                    dd.cb = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.DISPLAY_DEVICE));
                    if (!NativeMethods.EnumDisplayDevices(device, i, ref dd, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME)) break;
                    if ((dd.StateFlags & NativeMethods.DISPLAY_DEVICE_ACTIVE) == 0) continue;
                    ids.Add(ShortId(dd.DeviceID));
                }
            }
            catch (Exception ex) { Log.Warn("Monitor-Kennung nicht lesbar: {0}", ex.Message); }
            return ids;
        }

        /// <summary>"\\?\DISPLAY#GSM5B09#5&amp;2a1c3b7f&amp;0&amp;UID4353#{...}" -> "GSM5B09#5&amp;2a1c3b7f&amp;0&amp;UID4353" (Typ#Anschluss).</summary>
        private static string ShortId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return "";
            var parts = deviceId.Split('#');
            return parts.Length >= 3 ? parts[1] + "#" + parts[2] : deviceId.Trim();
        }

        private static string Model(string id)
        {
            int i = id.IndexOf('#');
            return i > 0 ? id.Substring(0, i) : id;
        }

        /// <summary>
        /// Gibt jedem Monitor seine feste Handle-Nummer. Die Nummer hängt am Monitor (Typ und Anschluss), nicht an der
        /// Reihenfolge, in der Windows die Monitore gerade aufzählt: Fehlt ein Monitor oder zählt Windows nach einem
        /// Treiber-Update anders, behalten die übrigen ihre Nummer und bekommen weiter die richtigen Werte.
        /// </summary>
        private void AssignHandles(List<PhysicalMonitor> list)
        {
            if (_mapFile == null) return; // Nummern in der Reihenfolge von Windows
            var map = LoadMap();
            bool changed = false;
            var used = new HashSet<int>();
            var open = new List<PhysicalMonitor>();

            // 1. bekannte Monitore: gleiche Nummer wie bisher
            foreach (var m in list)
            {
                m.Index = -1;
                if (m.Id.Length > 0)
                {
                    foreach (var kv in map)
                    {
                        if (used.Contains(kv.Key) || !string.Equals(kv.Value, m.Id, StringComparison.OrdinalIgnoreCase)) continue;
                        m.Index = kv.Key;
                        break;
                    }
                }
                if (m.Index >= 0) used.Add(m.Index);
                else open.Add(m);
            }
            var absent = map.Keys.Where(h => !used.Contains(h)).OrderBy(h => h).ToList();

            // 2. gleicher Monitortyp an einem anderen Anschluss: übernimmt die Nummer des fehlenden
            foreach (var m in open.ToList())
            {
                if (m.Id.Length == 0) continue;
                string model = Model(m.Id);
                int found = -1;
                foreach (int h in absent)
                    if (string.Equals(Model(map[h]), model, StringComparison.OrdinalIgnoreCase)) { found = h; break; }
                if (found < 0) continue;
                Log.Info("Monitor \"{0}\" hängt an einem anderen Anschluss - behält Handle {1}", m.Description, found);
                m.Index = found;
                used.Add(found);
                absent.Remove(found);
                open.Remove(m);
                map[found] = m.Id;
                changed = true;
            }

            // 3. Monitor ersetzt (gleich viele neue wie fehlende): der neue übernimmt die Nummer
            if (open.Count > 0 && open.Count == absent.Count)
            {
                foreach (var m in open)
                {
                    int h = absent[0];
                    absent.RemoveAt(0);
                    Log.Info("Neuer Monitor \"{0}\" übernimmt Handle {1}", m.Description, h);
                    m.Index = h;
                    used.Add(h);
                    if (m.Id.Length > 0) { map[h] = m.Id; changed = true; }
                }
                open.Clear();
            }

            // 4. zusätzliche Monitore: nächste freie Nummer
            foreach (var m in open)
            {
                int h = 0;
                while (used.Contains(h) || map.ContainsKey(h)) h++;
                m.Index = h;
                used.Add(h);
                if (m.Id.Length > 0) { map[h] = m.Id; changed = true; }
            }

            if (changed) SaveMap(map);
            list.Sort((a, b) => a.Index.CompareTo(b.Index));
        }

        private Dictionary<int, string> LoadMap()
        {
            var map = new Dictionary<int, string>();
            try
            {
                if (!File.Exists(_mapFile)) return map;
                foreach (string line in File.ReadAllLines(_mapFile))
                {
                    int eq = line.IndexOf('=');
                    int handle;
                    if (eq <= 0 || !int.TryParse(line.Substring(0, eq).Trim(), out handle) || handle < 0) continue;
                    string id = line.Substring(eq + 1).Trim();
                    if (id.Length > 0) map[handle] = id;
                }
            }
            catch (Exception ex) { Log.Warn("Monitor-Zuordnung nicht lesbar: {0}", ex.Message); }
            return map;
        }

        private void SaveMap(Dictionary<int, string> map)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_mapFile));
                File.WriteAllLines(_mapFile, map.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value).ToArray());
                Log.Info("Monitor-Zuordnung gespeichert ({0})", Path.GetFileName(_mapFile));
            }
            catch (Exception ex) { Log.Warn("Monitor-Zuordnung nicht speicherbar: {0}", ex.Message); }
        }

        private PhysicalMonitor Find(int index)
        {
            foreach (var m in _monitors)
                if (m.Index == index) return m;
            return null;
        }

        private void ReleaseAll()
        {
            foreach (var m in _monitors)
            {
                NativeMethods.DestroyPhysicalMonitor(m.Handle);
            }
            _monitors = new List<PhysicalMonitor>();
        }

        // ------------------------------------------------------------------

        /// <summary>Setzt die Werte eines Profils (asynchron, in Reihenfolge).</summary>
        public void Apply(DimmingProfile profile, string reason)
        {
            var values = new Dictionary<int, MonitorValues>(profile.Monitors);
            string name = profile.Name;
            Enqueue(() => ApplyInternal(name, values, reason, true));
        }

        /// <summary>Setzt die Werte eines Profils und wartet darauf (z. B. beim Beenden).</summary>
        public void ApplySync(DimmingProfile profile, string reason, int timeoutMs)
        {
            var values = new Dictionary<int, MonitorValues>(profile.Monitors);
            string name = profile.Name;
            Invoke(() => ApplyInternal(name, values, reason, false), timeoutMs); // ohne Überblenden: soll schnell gehen
        }

        private void ApplyInternal(string name, Dictionary<int, MonitorValues> values, string reason, bool fade)
        {
            Log.Info("Profil \"{0}\" wird gesetzt ({1})", name, reason);
            var jobs = new List<KeyValuePair<PhysicalMonitor, MonitorValues>>();
            foreach (var kv in values)
            {
                var m = Find(kv.Key);
                if (m == null)
                {
                    Log.Warn("  Monitor #{0} ist nicht angeschlossen ({1} gefunden)", kv.Key, _monitors.Count);
                    continue;
                }
                jobs.Add(new KeyValuePair<PhysicalMonitor, MonitorValues>(m, kv.Value));
            }
            if (fade && Smooth) Fade(jobs);
            foreach (var job in jobs)
            {
                var m = job.Key;
                bool ok = true;
                if (job.Value.Brightness.HasValue) ok &= Write(m, VcpBrightness, "Helligkeit", job.Value.Brightness.Value, m.MaxBrightness);
                if (job.Value.Contrast.HasValue) ok &= Write(m, VcpContrast, "Kontrast", job.Value.Contrast.Value, m.MaxContrast);
                Report(m, ok);
            }
        }

        private const int FadeSteps = 6;

        /// <summary>
        /// Helligkeit in Zwischenschritten zum Zielwert führen (die Monitore abwechselnd, das gibt jedem seine Pause).
        /// Den Zielwert selbst setzt danach <see cref="Write"/>. Wartet schon ein neuer Auftrag, wird abgebrochen.
        /// </summary>
        private void Fade(List<KeyValuePair<PhysicalMonitor, MonitorValues>> jobs)
        {
            var starts = new Dictionary<PhysicalMonitor, int>();
            foreach (var job in jobs)
            {
                if (!job.Value.Brightness.HasValue) continue;
                uint cur, max;
                if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(job.Key.Handle, VcpBrightness, IntPtr.Zero, out cur, out max)) continue;
                if (Math.Abs(Target(job) - (int)cur) < 5) continue; // lohnt nicht
                starts[job.Key] = (int)cur;
            }
            if (starts.Count == 0) return;
            for (int step = 1; step < FadeSteps; step++)
            {
                foreach (var job in jobs)
                {
                    int start;
                    if (!starts.TryGetValue(job.Key, out start)) continue;
                    if (_queue.Count > 0) return;
                    int v = start + (Target(job) - start) * step / FadeSteps;
                    if (!NativeMethods.SetVCPFeature(job.Key.Handle, VcpBrightness, (uint)v)) starts.Remove(job.Key);
                }
                Thread.Sleep(20);
            }
        }

        private static int Target(KeyValuePair<PhysicalMonitor, MonitorValues> job)
        {
            return Math.Max(0, Math.Min(job.Value.Brightness.Value, (int)job.Key.MaxBrightness));
        }

        /// <summary>Meldet einen Monitor, der die Werte nicht annimmt - einmal, bis er wieder reagiert.</summary>
        private void Report(PhysicalMonitor m, bool ok)
        {
            if (ok) { _failed.Remove(m.Index); return; }
            if (!_failed.Add(m.Index)) return;
            var h = Problem;
            if (h != null)
                h("Monitor " + m.Index + " (" + m.Description + ") reagiert nicht.\r\nIst DDC/CI im Menü des Monitors eingeschaltet?");
        }

        private static bool Write(PhysicalMonitor m, byte code, string label, int value, uint max)
        {
            uint v = (uint)Math.Max(0, Math.Min(value, (int)max));
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (NativeMethods.SetVCPFeature(m.Handle, code, v))
                {
                    Log.Info("  Monitor #{0}: {1} = {2}", m.Index, label, v);
                    Thread.Sleep(50); // DDC/CI braucht eine kleine Pause zwischen Befehlen
                    return true;
                }
                Thread.Sleep(100 * attempt);
            }
            Log.Error("  Monitor #{0}: {1} = {2} konnte nicht gesetzt werden (DDC/CI am Monitor aktiv?)", m.Index, label, v);
            return false;
        }

        private static bool TryRead(IntPtr handle, byte code, out uint current, out uint max)
        {
            current = 0;
            max = 0;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, code, IntPtr.Zero, out current, out max))
                    return true;
                Thread.Sleep(100 * attempt);
            }
            return false;
        }

        /// <summary>Liest den aktuellen Zustand aller Monitore (für die grafische Anzeige).</summary>
        public List<MonitorSnapshot> Snapshot(int timeoutMs)
        {
            var result = new List<MonitorSnapshot>();
            if (Demo.Active) return Demo.Monitors();
            Invoke(() =>
            {
                foreach (var m in _monitors)
                {
                    var s = new MonitorSnapshot
                    {
                        Index = m.Index,
                        Description = m.Description,
                        DeviceName = m.DeviceName,
                        Bounds = System.Drawing.Rectangle.FromLTRB(m.Bounds.Left, m.Bounds.Top, m.Bounds.Right, m.Bounds.Bottom)
                    };
                    uint cur, max;
                    if (TryRead(m.Handle, VcpBrightness, out cur, out max)) { s.Brightness = (int)cur; s.BrightnessMax = (int)Math.Max(1, max); }
                    if (TryRead(m.Handle, VcpContrast, out cur, out max)) { s.Contrast = (int)cur; s.ContrastMax = (int)Math.Max(1, max); }
                    result.Add(s);
                }
            }, timeoutMs);
            return result;
        }

        /// <summary>Liest den aktuellen Zustand aller Monitore als Text (für die Anzeige).</summary>
        public string Describe(int timeoutMs)
        {
            var sb = new StringBuilder();
            bool ok = Invoke(() =>
            {
                if (_monitors.Count == 0)
                {
                    sb.AppendLine("Keine Monitore mit DDC/CI gefunden.");
                    return;
                }
                foreach (var m in _monitors)
                {
                    uint bc, bm, cc, cm;
                    bool b = TryRead(m.Handle, VcpBrightness, out bc, out bm);
                    bool c = TryRead(m.Handle, VcpContrast, out cc, out cm);
                    sb.AppendFormat("Handle {0}   {1}   ({2})\r\n", m.Index, m.Description, m.DeviceName);
                    sb.AppendFormat("    Position: X={0}, Y={1}, {2} x {3}{4}\r\n", m.Bounds.Left, m.Bounds.Top,
                        m.Bounds.Right - m.Bounds.Left, m.Bounds.Bottom - m.Bounds.Top,
                        m.Bounds.Left == 0 && m.Bounds.Top == 0 ? "  (Hauptmonitor)" : "");
                    sb.AppendFormat("    Helligkeit: {0}\r\n", b ? bc + " von " + bm : "nicht lesbar");
                    sb.AppendFormat("    Kontrast:   {0}\r\n\r\n", c ? cc + " von " + cm : "nicht lesbar");
                }
            }, timeoutMs);
            if (!ok) sb.AppendLine("Zeitüberschreitung beim Lesen der Monitore.");
            return sb.ToString();
        }

        public void Dispose()
        {
            Invoke(ReleaseAll, 3000);
            _queue.CompleteAdding();
        }
    }
}
