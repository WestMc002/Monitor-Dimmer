// Monitor Dimmer – Profil-Aktionen (Programme, Dienste, Energiesparplan, Audiogerät, Aufgaben)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MonitorDimmer
{
    /// <summary>Eine Aktion beim Aktivieren/Deaktivieren eines Profils (ersetzt die .bat-Skripte).</summary>
    internal sealed class ActionDef
    {
        public string Type = "";
        public string Value = "";
        public string Arguments = "";

        public ActionDef Clone()
        {
            return new ActionDef { Type = Type, Value = Value, Arguments = Arguments };
        }
    }

    /// <summary>Die verfügbaren Aktionsarten mit Namen und Symbol.</summary>
    internal static class ActionTypes
    {
        internal sealed class Info
        {
            public string Id, Title, ValueLabel, Hint;
            public char Glyph;
            public bool HasArguments;
            public bool NoValue;
            public override string ToString() { return Title; }
        }

        public static readonly Info[] All =
        {
            new Info { Id = "KillProcess", Title = "Programm beenden", ValueLabel = "Programm (exe)", Glyph = '\uE711', Hint = "Beendet alle laufenden Prozesse mit diesem Namen, z. B. gsyncit.exe." },
            new Info { Id = "StartProgram", Title = "Programm starten", ValueLabel = "Programm", Glyph = '\uE768', HasArguments = true, Hint = "Startet das Programm, sofern es nicht schon läuft." },
            new Info { Id = "StopService", Title = "Dienst beenden", ValueLabel = "Dienst", Glyph = '\uE71A', Hint = "Beendet den Windows-Dienst und wartet höchstens 60 Sekunden. Braucht Adminrechte." },
            new Info { Id = "StartService", Title = "Dienst starten", ValueLabel = "Dienst", Glyph = '\uE768', Hint = "Startet den Windows-Dienst. Braucht Adminrechte." },
            new Info { Id = "PowerPlan", Title = "Energiesparplan wählen", ValueLabel = "Energiesparplan", Glyph = '\uE83F', Hint = "Schaltet den Energiesparplan um (wie powercfg /setactive)." },
            new Info { Id = "AudioDevice", Title = "Audiogerät als Standard", ValueLabel = "Wiedergabegerät", Glyph = '\uE767', Hint = "Macht das Gerät zum Standard-Wiedergabegerät (ohne NirCmd)." },
            new Info { Id = "TaskRun", Title = "Aufgabe ausführen", ValueLabel = "Aufgabe", Glyph = '\uE768', Hint = "Startet eine Aufgabe der Aufgabenplanung." },
            new Info { Id = "TaskEnable", Title = "Aufgabe aktivieren", ValueLabel = "Aufgabe", Glyph = '\uE73E', Hint = "Aktiviert eine Aufgabe der Aufgabenplanung." },
            new Info { Id = "TaskDisable", Title = "Aufgabe deaktivieren", ValueLabel = "Aufgabe", Glyph = '\uE711', Hint = "Deaktiviert eine Aufgabe der Aufgabenplanung." },
            new Info { Id = "Wait", Title = "Warten", ValueLabel = "Sekunden", Glyph = '\uE916', Hint = "Wartet die angegebene Zeit (höchstens 60 Sekunden)." },
            new Info { Id = "RestoreStopped", Title = "Beendete wieder starten", ValueLabel = "", Glyph = '\uE72C', NoValue = true, Hint = "Startet die Programme und Dienste wieder, die \"Beim Aktivieren\" dieses Profils beendet wurden - nur die, die vorher wirklich liefen, mit ihren ursprünglichen Startparametern." },
            new Info { Id = "Command", Title = "Befehl ausführen", ValueLabel = "Befehl", Glyph = '\uE756', Hint = "Führt eine Befehlszeile unsichtbar über cmd.exe aus (wartet höchstens 2 Minuten)." },
        };

        public static Info Find(string id)
        {
            return All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Lesbarer Text für die Liste, z. B. "Energiesparplan: Höchstleistung".</summary>
        public static string Describe(ActionDef a)
        {
            var info = Find(a.Type);
            string title = info != null ? info.Title : a.Type;
            string value = a.Value ?? "";
            if (a.Type == "PowerPlan") value = SystemLists.PowerPlanName(value);
            else if (a.Type == "Wait") value = value + " s";
            else if (a.Type == "StartProgram") value = Path.GetFileName(value) + (string.IsNullOrEmpty(a.Arguments) ? "" : " " + a.Arguments);
            else if (a.Type == "RestoreStopped") value = "Programme und Dienste aus \"Beim Aktivieren\"";
            return title + ":  " + value;
        }
    }

    /// <summary>Führt Aktionen aus. Wird im Hintergrund-Thread des EventRunner aufgerufen.</summary>
    internal static class ActionExecutor
    {
        /// <summary>Führt eine Aktion aus und liefert eine kurze Meldung (für Protokoll und Test).</summary>
        public static string Execute(ActionDef a)
        {
            return Execute(a, null);
        }

        /// <summary>profile: Name des Profils, für das sich "Beendete wieder starten" merkt, was beendet wurde.</summary>
        public static string Execute(ActionDef a, string profile)
        {
            string v = Environment.ExpandEnvironmentVariables((a.Value ?? "").Trim());
            try
            {
                switch (a.Type)
                {
                    case "KillProcess": return KillProcess(v, profile);
                    case "StartProgram": return StartProgram(v, Environment.ExpandEnvironmentVariables(a.Arguments ?? ""));
                    case "StopService": return StopService(v, profile);
                    case "RestoreStopped": return RestoreStopped(profile);
                    case "StartService": return StartService(v);
                    case "PowerPlan": return PowerPlan(v);
                    case "AudioDevice": return AudioSwitcher.SetDefaultWaiting(v, 60);
                    case "TaskRun": return SchTasks("/Run /TN \"" + v + "\"", "gestartet");
                    case "TaskEnable": return SchTasks("/Change /TN \"" + v + "\" /ENABLE", "aktiviert");
                    case "TaskDisable": return SchTasks("/Change /TN \"" + v + "\" /DISABLE", "deaktiviert");
                    case "Wait":
                        int s;
                        if (!int.TryParse(v, out s)) s = 1;
                        s = Math.Max(0, Math.Min(60, s));
                        Thread.Sleep(s * 1000);
                        return s + " s gewartet";
                    case "Command": return Command(v);
                    default: return "unbekannte Aktion \"" + a.Type + "\"";
                }
            }
            catch (Exception ex)
            {
                return "Fehler: " + ex.Message;
            }
        }

        private static string ExeName(string v)
        {
            string n = Path.GetFileName(v.Trim('"'));
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            return n;
        }

        /// <summary>Wie die Batch: erst normal beenden (Fenster schließen), nach 5 s hart beenden.</summary>
        private static string KillProcess(string v, string profile)
        {
            string name = ExeName(v);
            if (!Running(name)) return "läuft nicht";
            if (profile != null) StoppedRegistry.RememberProgram(profile, name);
            RunHidden(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/IM \"" + name + ".exe\"", 10000);
            for (int i = 0; i < 10; i++)
            {
                if (!Running(name)) return "beendet";
                Thread.Sleep(500);
            }
            string error = null;
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); p.WaitForExit(5000); }
                catch (Exception ex) { error = ex.Message; }
                finally { p.Dispose(); }
            }
            if (!Running(name)) return "reagierte nicht - hart beendet";
            return "Fehler: läuft noch" + (error != null ? " (" + error + ")" : "");
        }

        private static bool Running(string name)
        {
            var list = Process.GetProcessesByName(name);
            foreach (var p in list) p.Dispose();
            return list.Length > 0;
        }

        internal static string StartProgram(string path, string args)
        {
            path = path.Trim('"');
            if (!File.Exists(path)) return "Fehler: Datei nicht gefunden";
            if (Running(ExeName(path))) return "läuft bereits";
            // Der Monitor Dimmer läuft mit Adminrechten - Programme aber normal (wie aus dem Startmenü) starten
            if (UserStarter.Start(path, args, Path.GetDirectoryName(path))) return "gestartet";
            var psi = new ProcessStartInfo(path, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) };
            using (Process.Start(psi)) { }
            return "gestartet (mit Adminrechten)";
        }

        /// <summary>Startet die Dienste und Programme wieder, die "Beim Aktivieren" dieses Profils beendet wurden.</summary>
        private static string RestoreStopped(string profile)
        {
            if (profile == null) return "kein Profil";
            var list = StoppedRegistry.Take(profile);
            if (list.Count == 0) return "nichts zu starten";
            int ok = 0, failed = 0;
            var errors = new List<string>();
            foreach (var e in list.Where(x => x.Kind == "S"))
            {
                string r = StartService(e.Path);
                Log.Info("  Dienst {0} -> {1}", e.Path, r);
                if (r.StartsWith("Fehler")) { failed++; errors.Add(e.Path); } else ok++;
            }
            foreach (var e in list.Where(x => x.Kind == "P"))
            {
                string r;
                try { r = StartProgram(e.Path, e.Args); }
                catch (Exception ex) { r = "Fehler: " + ex.Message; }
                Log.Info("  Programm {0} {1} -> {2}", e.Path, e.Args, r);
                if (r.StartsWith("Fehler")) { failed++; errors.Add(Path.GetFileName(e.Path)); } else ok++;
            }
            return ok + " gestartet" + (failed > 0 ? ", " + failed + " Fehler (" + string.Join(", ", errors) + ")" : "");
        }

        private static string StopService(string name, string profile)
        {
            if (!ServiceExists(name)) return "nicht vorhanden - übersprungen";
            using (var sc = new ServiceController(name))
            {
                if (sc.Status == ServiceControllerStatus.Stopped) return "war schon beendet";
                if (profile != null) StoppedRegistry.RememberService(profile, name);
                if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                return "beendet";
            }
        }

        internal static string StartService(string name)
        {
            if (!ServiceExists(name)) return "nicht vorhanden - übersprungen";
            using (var sc = new ServiceController(name))
            {
                if (sc.Status == ServiceControllerStatus.Running) return "läuft bereits";
                if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                return "gestartet";
            }
        }

        private static bool ServiceExists(string name)
        {
            try
            {
                using (var sc = new ServiceController(name)) { var st = sc.Status; return true; }
            }
            catch (InvalidOperationException) { return false; }
        }

        /// <summary>Kurznamen wie bei powercfg: SCHEME_MIN = Höchstleistung, SCHEME_MAX = Energiesparmodus, SCHEME_BALANCED = Ausbalanciert.</summary>
        internal static string PlanAlias(string v)
        {
            switch ((v ?? "").Trim().ToUpperInvariant())
            {
                case "SCHEME_MIN": return "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
                case "SCHEME_MAX": return "a1841308-3541-4fab-bc81-f71556f20b4a";
                case "SCHEME_BALANCED": return "381b4222-f694-41f0-9685-ff5bb260df2e";
                default: return v;
            }
        }

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);

        private static string PowerPlan(string v)
        {
            v = PlanAlias(v);
            Guid g;
            if (!Guid.TryParse(v, out g)) return "Fehler: keine gültige GUID";
            uint r = PowerSetActiveScheme(IntPtr.Zero, ref g);
            return r == 0 ? "aktiv: " + SystemLists.PowerPlanName(v) : "Fehler " + r;
        }

        private static string SchTasks(string args, string ok)
        {
            int code = RunHidden(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args, 15000);
            return code == 0 ? ok : "Fehler (Exitcode " + code + ")";
        }

        private static string Command(string cmd)
        {
            int code = RunHidden(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c " + cmd, 120000);
            return code == int.MinValue ? "läuft nach 2 Minuten noch" : "beendet (Exitcode " + code + ")";
        }

        /// <summary>Startet unsichtbar und wartet; int.MinValue = Zeit abgelaufen.</summary>
        internal static int RunHidden(string exe, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { };
                p.ErrorDataReceived += (s, e) => { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs)) return int.MinValue;
                return p.ExitCode;
            }
        }
    }

    /// <summary>Auswahllisten für das Bearbeiten-Fenster.</summary>
    internal static class SystemLists
    {
        internal sealed class Item
        {
            public string Value, Text;
            public override string ToString() { return Text; }
        }

        [DllImport("powrprof.dll")]
        private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subGroup, uint accessFlags, uint index, byte[] buffer, ref uint size);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subGroup, IntPtr setting, byte[] buffer, ref uint size);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        private static Dictionary<string, string> _planNames;

        public static List<Item> PowerPlans()
        {
            var list = new List<Item>();
            try
            {
                for (uint i = 0; ; i++)
                {
                    var buf = new byte[16];
                    uint size = 16;
                    if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16 /* ACCESS_SCHEME */, i, buf, ref size) != 0) break;
                    var g = new Guid(buf);
                    list.Add(new Item { Value = g.ToString(), Text = ReadPlanName(g) });
                }
            }
            catch { }
            _planNames = list.ToDictionary(x => x.Value, x => x.Text, StringComparer.OrdinalIgnoreCase);
            return list;
        }

        private static string ReadPlanName(Guid g)
        {
            uint size = 0;
            PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref size);
            if (size == 0) return g.ToString();
            var buf = new byte[size];
            if (PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return g.ToString();
            return Encoding.Unicode.GetString(buf).TrimEnd('\0');
        }

        /// <summary>Name eines Energiesparplans (oder die GUID, wenn unbekannt).</summary>
        public static string PowerPlanName(string guid)
        {
            if (_planNames == null) PowerPlans();
            string n;
            return _planNames != null && guid != null && _planNames.TryGetValue(guid.Trim(), out n) ? n : guid;
        }

        public static List<Item> Processes()
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try { if (p.Id > 4) names.Add(p.ProcessName + ".exe"); }
                catch { }
                finally { p.Dispose(); }
            }
            return names.Select(n => new Item { Value = n, Text = n }).ToList();
        }

        public static List<Item> Services()
        {
            var list = new List<Item>();
            try
            {
                foreach (var s in ServiceController.GetServices().OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                {
                    list.Add(new Item { Value = s.ServiceName, Text = s.ServiceName + "  -  " + s.DisplayName });
                    s.Dispose();
                }
            }
            catch { }
            return list;
        }

        /// <summary>Aufgaben im obersten Ordner der Aufgabenplanung (eigene Aufgaben, ohne \Microsoft\...).</summary>
        public static List<Item> Tasks()
        {
            var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/Query /FO CSV /NH")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.GetEncoding(850)
                };
                using (var p = Process.Start(psi))
                {
                    string all = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    foreach (var line in all.Split('\n'))
                    {
                        var m = Regex.Match(line, "^\"\\\\([^\"\\\\]+)\"");
                        if (m.Success) names.Add(m.Groups[1].Value);
                    }
                }
            }
            catch { }
            return names.Select(n => new Item { Value = n, Text = n }).ToList();
        }

        public static List<Item> AudioDevices()
        {
            return AudioSwitcher.List().Select(n => new Item { Value = n, Text = n }).ToList();
        }
    }

    /// <summary>Standard-Wiedergabegerät setzen (Core Audio + IPolicyConfig, wie das Sound-Fenster von Windows).</summary>
    internal static class AudioSwitcher
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid Fmt;
            public int Pid;
            public PropertyKey(string g, int pid) { Fmt = new Guid(g); Pid = pid; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort Vt;
            public ushort R1, R2, R3;
            public IntPtr P;
            public IntPtr P2;
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
            [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
            [PreserveSig] int ResetDeviceFormat(IntPtr a);
            [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
            [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
            [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
            [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
            [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
            [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorCom { }

        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class PolicyConfigCom { }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pv);

        private static readonly PropertyKey KeyFriendlyName = new PropertyKey("a45c254e-df1c-4efd-8020-67d146a850e0", 14); // "Headset (Logitech ...)"
        private static readonly PropertyKey KeyDeviceDesc = new PropertyKey("a45c254e-df1c-4efd-8020-67d146a850e0", 2);    // "Headset" bzw. eigener Name
        private static readonly PropertyKey KeyInterfaceName = new PropertyKey("026e516e-b814-414b-83cd-856d6fef4822", 2); // "Logitech PRO X 2 LIGHTSPEED"

        private sealed class Device
        {
            public string Id, FriendlyName, Desc, Interface;
        }

        private static List<Device> Devices()
        {
            var list = new List<Device>();
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDeviceCollection col;
            if (en.EnumAudioEndpoints(0 /* eRender */, 1 /* ACTIVE */, out col) != 0) return list;
            int count;
            col.GetCount(out count);
            for (int i = 0; i < count; i++)
            {
                IMMDevice dev;
                if (col.Item(i, out dev) != 0) continue;
                var d = new Device();
                dev.GetId(out d.Id);
                IPropertyStore store;
                if (dev.OpenPropertyStore(0, out store) == 0)
                {
                    d.FriendlyName = ReadString(store, KeyFriendlyName);
                    d.Desc = ReadString(store, KeyDeviceDesc);
                    d.Interface = ReadString(store, KeyInterfaceName);
                }
                list.Add(d);
            }
            return list;
        }

        private static string ReadString(IPropertyStore store, PropertyKey key)
        {
            PropVariant pv;
            if (store.GetValue(ref key, out pv) != 0) return null;
            try { return pv.Vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.P) : null; }
            finally { PropVariantClear(ref pv); }
        }

        /// <summary>Namen der aktiven Wiedergabegeräte (wie im Sound-Fenster).</summary>
        /// <summary>Ist das Gerät gerade vorhanden (gleiche Namenssuche wie beim Umschalten)?</summary>
        public static bool Exists(string name)
        {
            try
            {
                return Devices().Any(d => Eq(d.FriendlyName, name) || Eq(d.Desc, name) || Eq(d.Interface, name)
                    || Has(d.FriendlyName, name) || Has(d.Interface, name));
            }
            catch { return true; }
        }

        public static List<string> List()
        {
            try { return Devices().Select(d => d.FriendlyName ?? d.Desc ?? d.Id).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList(); }
            catch { return new List<string>(); }
        }

        /// <summary>Wartet höchstens maxSeconds, bis das Gerät da ist (z. B. VR-Headset wird gerade eingeschaltet).</summary>
        public static string SetDefaultWaiting(string name, int maxSeconds)
        {
            var sw = Stopwatch.StartNew();
            string r = SetDefault(name);
            while (r.StartsWith("Fehler: Gerät") && sw.Elapsed.TotalSeconds < maxSeconds)
            {
                Thread.Sleep(2000);
                r = SetDefault(name);
            }
            if (sw.Elapsed.TotalSeconds >= 2 && !r.StartsWith("Fehler")) r += " (nach " + (int)sw.Elapsed.TotalSeconds + " s)";
            return r;
        }

        /// <summary>Gerät per Name suchen (voller Name, Gerätename oder Schnittstellenname wie bei NirCmd) und als Standard setzen.</summary>
        public static string SetDefault(string name)
        {
            var devices = Devices();
            Func<Device, bool> exact = d => Eq(d.FriendlyName, name) || Eq(d.Desc, name) || Eq(d.Interface, name);
            var dev = devices.FirstOrDefault(exact)
                ?? devices.FirstOrDefault(d => Has(d.FriendlyName, name) || Has(d.Interface, name));
            if (dev == null) return "Fehler: Gerät \"" + name + "\" nicht gefunden (nicht angeschlossen?)";
            var pc = (IPolicyConfig)new PolicyConfigCom();
            int r1 = pc.SetDefaultEndpoint(dev.Id, 0); // Konsole
            int r2 = pc.SetDefaultEndpoint(dev.Id, 1); // Multimedia
            return r1 == 0 && r2 == 0 ? "Standard: " + (dev.FriendlyName ?? name) : "Fehler 0x" + (r1 != 0 ? r1 : r2).ToString("X8");
        }

        private static bool Eq(string a, string b) { return a != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase); }
        private static bool Has(string a, string b) { return a != null && a.IndexOf(b.Trim(), StringComparison.OrdinalIgnoreCase) >= 0; }
    }

    /// <summary>Übersetzt die alten .bat-Skripte in Aktionen.</summary>
    internal static class ScriptImporter
    {
        /// <summary>Liefert die Aktionen aus allen Skripten dieses Typs; unbekannte Zeilen werden "Befehl ausführen".</summary>
        public static List<ActionDef> Import(DimmingProfile p, string type, string resourcesFolder, List<string> notes)
        {
            var result = new List<ActionDef>();
            foreach (var h in p.Handlers.Where(x => string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase)))
            {
                string args = (h.Arguments ?? "").Replace("{ResourcesFolderPath}", resourcesFolder);
                var m = Regex.Match(args, "\"([^\"]+\\.(bat|cmd))\"|(\\S+\\.(bat|cmd))", RegexOptions.IgnoreCase);
                string bat = m.Success ? Environment.ExpandEnvironmentVariables(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value) : null;
                if (bat == null || !File.Exists(bat))
                {
                    // Kein Skript: Aufruf als Befehl übernehmen
                    result.Add(new ActionDef { Type = "Command", Value = "\"" + Environment.ExpandEnvironmentVariables(h.Executable) + "\" " + args });
                    continue;
                }
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in File.ReadAllLines(bat, Encoding.GetEncoding(850)))
                {
                    string line = raw.Trim();
                    // SET "NAME=Wert" merken und %NAME% in den folgenden Zeilen ersetzen
                    var sv = Regex.Match(line, "^set\\s+\"?([A-Za-z0-9_]+)=([^\"]*)\"?$", RegexOptions.IgnoreCase);
                    if (sv.Success) { vars[sv.Groups[1].Value] = sv.Groups[2].Value; continue; }
                    foreach (var kv in vars) line = Regex.Replace(line, "%" + Regex.Escape(kv.Key) + "%", kv.Value.Replace("$", "$$"), RegexOptions.IgnoreCase);
                    foreach (var a in ParseLines(line)) result.Add(a);
                }
                notes.Add(Path.GetFileName(bat));
            }
            return result;
        }

        /// <summary>Eine Zeile kann mehrere Aktionen ergeben (FOR %%D IN (a b c) DO CALL :DienstStoppen %%D).</summary>
        private static IEnumerable<ActionDef> ParseLines(string l)
        {
            var f = Regex.Match(l, "^for\\s+%%\\w\\s+in\\s+\\(([^)]*)\\)\\s+do\\s+call\\s+:(\\w+)", RegexOptions.IgnoreCase);
            if (f.Success)
            {
                string sub = f.Groups[2].Value.ToLowerInvariant();
                string type = sub.Contains("dienst") && sub.Contains("stopp") ? "StopService"
                    : sub.Contains("dienst") && sub.Contains("start") ? "StartService"
                    : sub.Contains("beenden") || sub.Contains("kill") ? "KillProcess" : null;
                if (type != null)
                {
                    foreach (var item in f.Groups[1].Value.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                        yield return new ActionDef { Type = type, Value = item.Trim('"') };
                    yield break;
                }
            }
            var a = ParseLine(l);
            if (a != null && (a.Value ?? "").IndexOf('%') < 0) yield return a;
        }

        private static ActionDef ParseLine(string l)
        {
            if (l.Length == 0) return null;
            string low = l.ToLowerInvariant();
            Match m;

            // Unterprogramm-Aufrufe der Batch: CALL :Beenden x.exe "Name"
            m = Regex.Match(l, "^call\\s+:(\\w+)\\s+\"?([^\"\\s]+)\"?", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string sub = m.Groups[1].Value.ToLowerInvariant();
                if (sub.Contains("dienst") && sub.Contains("stopp")) return new ActionDef { Type = "StopService", Value = m.Groups[2].Value };
                if (sub.Contains("dienst") && sub.Contains("start")) return new ActionDef { Type = "StartService", Value = m.Groups[2].Value };
                if (sub.Contains("beenden") || sub.Contains("kill")) return new ActionDef { Type = "KillProcess", Value = m.Groups[2].Value };
                return null;
            }
            if (low.StartsWith("call ")) return null;

            // NirCmd direkt aufgerufen: "...\nircmd.exe" setdefaultsounddevice "Gerät"
            m = Regex.Match(l, "nircmd\\.exe\"?\\s+setdefaultsounddevice\\s+\"?([^\"]+?)\"?(\\s+\\d)?\\s*$", RegexOptions.IgnoreCase);
            if (m.Success && !low.StartsWith("start ")) return new ActionDef { Type = "AudioDevice", Value = m.Groups[1].Value.Trim() };

            // Prozess-Schleife: for /f ... tasklist /FI "IMAGENAME eq x.exe"
            m = Regex.Match(l, "IMAGENAME\\s+eq\\s+([^\"']+?)\\s*[\"']", RegexOptions.IgnoreCase);
            if (m.Success) return new ActionDef { Type = "KillProcess", Value = m.Groups[1].Value.Trim() };
            m = Regex.Match(l, "^taskkill\\b.*?/IM\\s+\"?([^\"\\s]+)", RegexOptions.IgnoreCase);
            if (m.Success) return new ActionDef { Type = "KillProcess", Value = m.Groups[1].Value };

            string[] ignore = { "@echo", "echo", "rem", "::", "mode ", "set ", "goto", ":", "ping", "sc query", "if ", ")", "taskkill", "for ", "timeout", "cls", "exit", "pause", "setlocal", "endlocal", "color", "title", "net session", "chcp" };
            if (ignore.Any(x => low.StartsWith(x))) return null;

            m = Regex.Match(l, "^powercfg(\\.exe)?\\s+/setactive\\s+([0-9a-f-]{36}|scheme_\\w+)", RegexOptions.IgnoreCase);
            if (m.Success) return new ActionDef { Type = "PowerPlan", Value = ActionExecutor.PlanAlias(m.Groups[2].Value).ToLowerInvariant() };

            m = Regex.Match(l, "^schtasks(\\.exe)?\\s+/run\\s+/tn\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (m.Success) return new ActionDef { Type = "TaskRun", Value = m.Groups[2].Value };
            m = Regex.Match(l, "^schtasks(\\.exe)?\\s+/change\\s+/tn\\s+\"([^\"]+)\"\\s+/(enable|disable)", RegexOptions.IgnoreCase);
            if (m.Success) return new ActionDef { Type = m.Groups[3].Value.ToLowerInvariant() == "enable" ? "TaskEnable" : "TaskDisable", Value = m.Groups[2].Value };

            m = Regex.Match(l, "^net\\s+(stop|start)\\s+\"?([^\">]+?)\"?\\s*(/y)?\\s*(>.*)?$", RegexOptions.IgnoreCase);
            if (m.Success) return new ActionDef { Type = m.Groups[1].Value.ToLowerInvariant() == "stop" ? "StopService" : "StartService", Value = m.Groups[2].Value.Trim() };

            m = Regex.Match(l, "^start\\s+\"[^\"]*\"\\s+\"([^\"]+)\"\\s*(.*)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string exe = m.Groups[1].Value, rest = m.Groups[2].Value.Trim();
                var sd = Regex.Match(rest, "^setdefaultsounddevice\\s+\"?([^\"]+?)\"?(\\s+\\d)?$", RegexOptions.IgnoreCase);
                if (Path.GetFileName(exe).Equals("nircmd.exe", StringComparison.OrdinalIgnoreCase) && sd.Success)
                    return new ActionDef { Type = "AudioDevice", Value = sd.Groups[1].Value.Trim() };
                return new ActionDef { Type = "StartProgram", Value = exe, Arguments = rest };
            }
            return new ActionDef { Type = "Command", Value = l };
        }
    }

    /// <summary>
    /// Merkt sich je Profil, welche Programme (Pfad + Startparameter) und Dienste beim Aktivieren beendet wurden.
    /// Wird in Configurations\Beendete Programme.txt gespeichert, damit es auch einen Neustart des Monitor Dimmers übersteht.
    /// </summary>
    internal static class StoppedRegistry
    {
        internal sealed class Entry
        {
            public string Profile, Kind, Path, Args;
        }

        private static readonly object Sync = new object();
        private static List<Entry> _list;

        private static string FilePath
        {
            get { return System.IO.Path.Combine(AppData.Configurations, "Beendete Programme.txt"); }
        }

        private static List<Entry> List
        {
            get
            {
                if (_list != null) return _list;
                _list = new List<Entry>();
                try
                {
                    if (File.Exists(FilePath))
                        foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                        {
                            var f = line.Split('\t');
                            if (f.Length >= 3) _list.Add(new Entry { Profile = f[0], Kind = f[1], Path = f[2], Args = f.Length > 3 ? f[3] : "" });
                        }
                }
                catch { }
                return _list;
            }
        }

        private static void Save()
        {
            try
            {
                if (_list.Count == 0) { if (File.Exists(FilePath)) File.Delete(FilePath); return; }
                File.WriteAllLines(FilePath, _list.Select(e => e.Profile + "\t" + e.Kind + "\t" + e.Path + "\t" + (e.Args ?? "").Replace("\t", " ")), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("Beendete Programme speichern: {0}", ex.Message); }
        }

        private static void Add(Entry e)
        {
            lock (Sync)
            {
                if (List.Any(x => x.Profile == e.Profile && x.Kind == e.Kind && string.Equals(x.Path, e.Path, StringComparison.OrdinalIgnoreCase))) return;
                List.Add(e);
                Save();
            }
        }

        public static void RememberService(string profile, string name)
        {
            Add(new Entry { Profile = profile, Kind = "S", Path = name, Args = "" });
        }

        /// <summary>Liest Pfad und Startparameter des Hauptprozesses (ohne Unterprozesse wie bei Firefox/Discord).</summary>
        public static void RememberProgram(string profile, string exeName)
        {
            try
            {
                var procs = new List<Tuple<uint, uint, string, string>>();
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = '" + exeName.Replace("'", "") + ".exe'"))
                using (var results = searcher.Get())
                {
                    foreach (System.Management.ManagementObject o in results)
                    {
                        procs.Add(Tuple.Create(Convert.ToUInt32(o["ProcessId"]), Convert.ToUInt32(o["ParentProcessId"]),
                            o["ExecutablePath"] as string, o["CommandLine"] as string));
                        o.Dispose();
                    }
                }
                var pids = new HashSet<uint>(procs.Select(p => p.Item1));
                foreach (var p in procs.Where(x => !pids.Contains(x.Item2)))
                {
                    string path = p.Item3;
                    if (string.IsNullOrEmpty(path)) continue;
                    // Store-Apps (WindowsApps) startet Windows selbst wieder, direkt starten geht nicht
                    if (path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    Add(new Entry { Profile = profile, Kind = "P", Path = path, Args = ArgsOf(p.Item4, path) });
                }
            }
            catch (Exception ex) { Log.Warn("Startparameter von {0}.exe nicht lesbar: {1}", exeName, ex.Message); }
        }

        /// <summary>Kommandozeile ohne den Programmpfad am Anfang.</summary>
        private static string ArgsOf(string cmd, string path)
        {
            if (string.IsNullOrEmpty(cmd)) return "";
            cmd = cmd.Trim();
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                return end < 0 ? "" : cmd.Substring(end + 1).Trim();
            }
            if (cmd.StartsWith(path, StringComparison.OrdinalIgnoreCase)) return cmd.Substring(path.Length).Trim();
            int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exe < 0 ? "" : cmd.Substring(exe + 4).Trim();
        }

        /// <summary>Holt die gemerkten Einträge des Profils (und löscht sie aus der Liste).</summary>
        public static List<Entry> Take(string profile)
        {
            lock (Sync)
            {
                var result = List.Where(e => e.Profile == profile).ToList();
                _list.RemoveAll(e => e.Profile == profile);
                Save();
                return result;
            }
        }
    }

    /// <summary>Startet Programme mit den Rechten des angemeldeten Benutzers (ohne Adminrechte), auch wenn der Monitor Dimmer erhöht läuft.</summary>
    internal static class UserStarter
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessWithTokenW(IntPtr token, int logonFlags, string application, StringBuilder commandLine,
            int creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInfo);

        /// <summary>false = nicht möglich (z. B. ohne Adminrechte) - dann normal starten.</summary>
        public static bool Start(string path, string args, string workDir)
        {
            if (!Autostart.IsElevated) return false;
            IntPtr process = IntPtr.Zero, token = IntPtr.Zero, primary = IntPtr.Zero;
            try
            {
                IntPtr shell = GetShellWindow();
                if (shell == IntPtr.Zero) return false;
                uint pid;
                GetWindowThreadProcessId(shell, out pid);
                process = OpenProcess(0x0400 /* QUERY_INFORMATION */, false, pid);
                if (process == IntPtr.Zero) return false;
                if (!OpenProcessToken(process, 0x0002 /* DUPLICATE */, out token)) return false;
                if (!DuplicateTokenEx(token, 0x018B, IntPtr.Zero, 2 /* Impersonation */, 1 /* Primary */, out primary)) return false;
                var si = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)) };
                ProcessInformation pi;
                var cmd = new StringBuilder("\"" + path + "\"" + (string.IsNullOrEmpty(args) ? "" : " " + args));
                if (!CreateProcessWithTokenW(primary, 0, null, cmd, 0x00000400 /* UNICODE_ENVIRONMENT */, IntPtr.Zero, workDir, ref si, out pi))
                {
                    Log.Warn("Start ohne Adminrechte nicht möglich (Fehler {0}) - {1}", Marshal.GetLastWin32Error(), path);
                    return false;
                }
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
                return true;
            }
            catch { return false; }
            finally
            {
                if (primary != IntPtr.Zero) CloseHandle(primary);
                if (token != IntPtr.Zero) CloseHandle(token);
                if (process != IntPtr.Zero) CloseHandle(process);
            }
        }
    }

    /// <summary>Prüft Aktionen für die Anzeige: fehlt die exe, der Dienst, die Aufgabe ...?</summary>
    internal sealed class ActionChecker
    {
        /// <summary>0 = in Ordnung, 1 = Hinweis (gelb), 2 = Fehler (rot).</summary>
        internal sealed class Result
        {
            public int Level;
            public string Text;
        }

        private HashSet<string> _services, _tasks, _plans;

        private HashSet<string> Services
        {
            get
            {
                if (_services == null)
                    _services = new HashSet<string>(SystemLists.Services().Select(i => i.Value), StringComparer.OrdinalIgnoreCase);
                return _services;
            }
        }

        private HashSet<string> Tasks
        {
            get
            {
                if (_tasks == null)
                    _tasks = new HashSet<string>(SystemLists.Tasks().Select(i => i.Value), StringComparer.OrdinalIgnoreCase);
                return _tasks;
            }
        }

        private HashSet<string> Plans
        {
            get
            {
                if (_plans == null)
                    _plans = new HashSet<string>(SystemLists.PowerPlans().Select(i => i.Value), StringComparer.OrdinalIgnoreCase);
                return _plans;
            }
        }

        public Result Check(ActionDef a)
        {
            if (Demo.Active) return Ok(); // Beispieldaten für die Screenshots

            string v = Environment.ExpandEnvironmentVariables((a.Value ?? "").Trim()).Trim('"');
            try
            {
                switch (a.Type)
                {
                    case "StartProgram":
                        return File.Exists(v) ? Ok() : Err(2, "Datei fehlt");
                    case "StopService":
                    case "StartService":
                        return Services.Contains(v) ? Ok() : Err(1, "Dienst nicht vorhanden - wird übersprungen");
                    case "TaskRun":
                    case "TaskEnable":
                    case "TaskDisable":
                        if (Tasks.Count == 0) return Ok(); // Liste nicht lesbar - nicht als Fehler zeigen
                        return Tasks.Contains(v) ? Ok() : Err(2, "Aufgabe nicht vorhanden");
                    case "PowerPlan":
                        string g = ActionExecutor.PlanAlias(v);
                        if (Plans.Count == 0) return Ok();
                        return Plans.Contains(g) ? Ok() : Err(2, "Energiesparplan nicht vorhanden");
                    case "AudioDevice":
                        return AudioSwitcher.Exists(v) ? Ok() : Err(1, "Gerät gerade nicht angeschlossen");
                    case "KillProcess":
                        return v.Length > 0 ? Ok() : Err(2, "kein Programm angegeben");
                    default:
                        return ActionTypes.Find(a.Type) == null ? Err(2, "unbekannte Aktion") : Ok();
                }
            }
            catch { return Ok(); }
        }

        private static Result Ok() { return new Result { Level = 0, Text = "" }; }
        private static Result Err(int level, string text) { return new Result { Level = level, Text = text }; }
    }
}
