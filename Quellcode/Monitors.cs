// Monitor Dimmer – Helligkeit und Kontrast der Monitore über DDC/CI
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        public MonitorManager()
        {
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
                foreach (var pm in arr)
                {
                    list.Add(new PhysicalMonitor
                    {
                        Index = list.Count,
                        Handle = pm.hPhysicalMonitor,
                        Description = pm.szPhysicalMonitorDescription,
                        DeviceName = info.szDevice,
                        Bounds = info.rcMonitor
                    });
                }
                return true;
            };
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            GC.KeepAlive(proc);

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
            Enqueue(() => ApplyInternal(name, values, reason));
        }

        /// <summary>Setzt die Werte eines Profils und wartet darauf (z. B. beim Beenden).</summary>
        public void ApplySync(DimmingProfile profile, string reason, int timeoutMs)
        {
            var values = new Dictionary<int, MonitorValues>(profile.Monitors);
            string name = profile.Name;
            Invoke(() => ApplyInternal(name, values, reason), timeoutMs);
        }

        private void ApplyInternal(string name, Dictionary<int, MonitorValues> values, string reason)
        {
            Log.Info("Profil \"{0}\" wird gesetzt ({1})", name, reason);
            foreach (var kv in values)
            {
                if (kv.Key >= _monitors.Count)
                {
                    Log.Warn("  Monitor #{0} existiert nicht (nur {1} gefunden)", kv.Key, _monitors.Count);
                    continue;
                }
                var m = _monitors[kv.Key];
                if (kv.Value.Brightness.HasValue) Write(m, VcpBrightness, "Helligkeit", kv.Value.Brightness.Value, m.MaxBrightness);
                if (kv.Value.Contrast.HasValue) Write(m, VcpContrast, "Kontrast", kv.Value.Contrast.Value, m.MaxContrast);
            }
        }

        private static void Write(PhysicalMonitor m, byte code, string label, int value, uint max)
        {
            uint v = (uint)Math.Max(0, Math.Min(value, (int)max));
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (NativeMethods.SetVCPFeature(m.Handle, code, v))
                {
                    Log.Info("  Monitor #{0}: {1} = {2}", m.Index, label, v);
                    Thread.Sleep(50); // DDC/CI braucht eine kleine Pause zwischen Befehlen
                    return;
                }
                Thread.Sleep(100 * attempt);
            }
            Log.Error("  Monitor #{0}: {1} = {2} konnte nicht gesetzt werden (DDC/CI am Monitor aktiv?)", m.Index, label, v);
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
