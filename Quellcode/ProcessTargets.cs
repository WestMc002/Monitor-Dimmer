// Monitor Dimmer – Prozesse für „Programm beenden“ eindeutig bestimmen
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace MonitorDimmer
{
    /// <summary>
    /// Ein eindeutig bestimmter Prozess: offenes Handle (die Prozess-ID kann solange nicht neu vergeben werden),
    /// vollständiger Pfad und Startzeit.
    /// </summary>
    internal sealed class TargetProcess : IDisposable
    {
        public int Id;
        public string Path;
        public long StartTicks;
        public IntPtr Handle;
        public bool CanTerminate;

        /// <summary>Beendet (oder Handle nicht mehr lesbar).</summary>
        public bool HasExited
        {
            get { return Handle == IntPtr.Zero || ProcessTargets.WaitForSingleObject(Handle, 0) != 258; } // 258 = WAIT_TIMEOUT = läuft
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { ProcessTargets.CloseHandle(Handle); Handle = IntPtr.Zero; }
        }
    }

    /// <summary>
    /// Auswahl der Prozesse für „Programm beenden“ und „läuft bereits“. Nur Prozesse der eigenen Sitzung und des eigenen
    /// Benutzerkontos; mit vollständigem Pfad in der Aktion nur genau dieser Pfad, mit Namen alle gleichnamigen. Prozesse,
    /// deren Pfad, Sitzung oder Besitzer sich nicht lesen lassen, zählen nicht (lieber nicht beenden als das falsche).
    /// </summary>
    internal static class ProcessTargets
    {
        private const uint SYNCHRONIZE = 0x00100000, PROCESS_TERMINATE = 0x0001, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenUser = 1;
        private const uint WM_CLOSE = 0x0010;

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr h, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(int pid, out int session);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>Alle Programme der eigenen Sitzung und des eigenen Kontos (für die Auswahlliste).</summary>
        public static List<TargetProcess> All()
        {
            return Collect(null, null);
        }

        /// <summary>
        /// Prozesse zur Angabe in der Aktion: vollständiger Pfad = nur genau dieser Pfad, sonst Name (mit oder ohne .exe).
        /// Der Aufrufer muss die Einträge freigeben (Dispose).
        /// </summary>
        public static List<TargetProcess> Find(string value)
        {
            string v = (value ?? "").Trim().Trim('"');
            if (v.Length == 0) return new List<TargetProcess>();
            string fullPath = null;
            if (Path.IsPathRooted(v))
            {
                try { fullPath = Path.GetFullPath(v); }
                catch { return new List<TargetProcess>(); }
            }
            string name = Path.GetFileName(v);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            if (name.Length == 0) return new List<TargetProcess>();
            return Collect(name, fullPath);
        }

        /// <summary>Steht in der Aktion ein vollständiger Pfad (dann zählt nur genau dieser)?</summary>
        public static bool IsFullPath(string value)
        {
            string v = (value ?? "").Trim().Trim('"');
            try { return v.Length > 0 && Path.IsPathRooted(v); }
            catch { return false; }
        }

        /// <summary>Läuft das Programm (gleicher Pfad bzw. Name) schon in der eigenen Sitzung?</summary>
        public static bool IsRunning(string value)
        {
            var list = Find(value);
            foreach (var t in list) t.Dispose();
            return list.Count > 0;
        }

        private static List<TargetProcess> Collect(string name, string fullPath)
        {
            var result = new List<TargetProcess>();
            int ownSession, ownId;
            SecurityIdentifier ownUser;
            using (var self = Process.GetCurrentProcess()) { ownSession = self.SessionId; ownId = self.Id; }
            using (var id = WindowsIdentity.GetCurrent()) ownUser = id.User;

            Process[] procs = name != null ? Process.GetProcessesByName(name) : Process.GetProcesses();
            foreach (var p in procs)
            {
                int pid;
                try { pid = p.Id; }
                catch { continue; }
                finally { p.Dispose(); }
                if (pid <= 4 || pid == ownId) continue;
                var t = Open(pid);
                if (t == null) continue;
                bool keep = false;
                try
                {
                    // Sitzung erst nach dem Öffnen prüfen: ab jetzt gehört die Prozess-ID sicher zu diesem Prozess
                    int session;
                    if (!ProcessIdToSessionId(pid, out session) || session != ownSession) continue;
                    if (name != null && !string.Equals(Path.GetFileNameWithoutExtension(t.Path), name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (fullPath != null && !string.Equals(t.Path, fullPath, StringComparison.OrdinalIgnoreCase)) continue;
                    var owner = Owner(t.Handle);
                    if (owner == null || ownUser == null || !owner.Equals(ownUser)) continue;
                    keep = true;
                }
                finally
                {
                    if (keep) result.Add(t); else t.Dispose();
                }
            }
            return result;
        }

        /// <summary>Handle öffnen und Pfad sowie Startzeit lesen; null, wenn der Prozess nicht eindeutig lesbar ist.</summary>
        private static TargetProcess Open(int pid)
        {
            bool canTerminate = true;
            IntPtr h = OpenProcess(SYNCHRONIZE | PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                canTerminate = false;
                h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return null;
            }
            var t = new TargetProcess { Id = pid, Handle = h, CanTerminate = canTerminate };
            try
            {
                var sb = new StringBuilder(32768);
                int size = sb.Capacity;
                if (!QueryFullProcessImageName(h, 0, sb, ref size) || size == 0) { t.Dispose(); return null; }
                t.Path = sb.ToString(0, size);
                long creation, exit, kernel, user;
                if (!GetProcessTimes(h, out creation, out exit, out kernel, out user)) { t.Dispose(); return null; }
                t.StartTicks = creation;
                if (t.HasExited) { t.Dispose(); return null; }
                return t;
            }
            catch { t.Dispose(); return null; }
        }

        private static SecurityIdentifier Owner(IntPtr process)
        {
            IntPtr token = IntPtr.Zero, buffer = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(process, TOKEN_QUERY, out token)) return null;
                int length;
                GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out length);
                if (length <= 0 || length > 4096) return null;
                buffer = Marshal.AllocHGlobal(length);
                if (!GetTokenInformation(token, TokenUser, buffer, length, out length)) return null;
                return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)); // TOKEN_USER.User.Sid
            }
            catch { return null; }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        /// <summary>Normal beenden wie taskkill ohne /F: WM_CLOSE an die Hauptfenster genau dieses Prozesses.</summary>
        public static void CloseWindows(TargetProcess t)
        {
            if (t.HasExited) return;
            var windows = new List<IntPtr>();
            EnumWindows((hWnd, l) =>
            {
                int pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid == t.Id) windows.Add(hWnd);
                return true;
            }, IntPtr.Zero);
            // die Prozess-ID ist über das offene Handle gebunden – die Fenster gehören also zu genau diesem Prozess
            if (t.HasExited) return;
            foreach (var w in windows) PostMessage(w, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Hart beenden über das gebundene Handle; null = erledigt, sonst der Fehler.</summary>
        public static string Terminate(TargetProcess t)
        {
            if (t.HasExited) return null;
            if (!t.CanTerminate) return "keine Berechtigung";
            if (!TerminateProcess(t.Handle, 1))
            {
                int err = Marshal.GetLastWin32Error();
                if (t.HasExited) return null;
                return new System.ComponentModel.Win32Exception(err).Message;
            }
            WaitForSingleObject(t.Handle, 5000);
            return null;
        }
    }
}
