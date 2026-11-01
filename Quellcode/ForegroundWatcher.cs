// Monitor Dimmer – Erkennt das Programm im Vordergrund
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace MonitorDimmer
{
    internal sealed class ForegroundInfo
    {
        public uint ProcessId;
        public string FullPath;   // null, wenn nicht lesbar
        public string FileName;

        public string Display { get { return FullPath ?? FileName ?? "?"; } }
    }

    /// <summary>
    /// Meldet Wechsel des Vordergrundprogramms. Nutzt ein Windows-Ereignis und zusätzlich
    /// eine Abfrage pro Sekunde als Sicherheitsnetz (z. B. wenn ein Spiel beendet wird).
    /// </summary>
    internal sealed class ForegroundWatcher : IDisposable
    {
        private readonly NativeMethods.WinEventDelegate _callback;
        private readonly IntPtr _hookForeground;
        private readonly IntPtr _hookMinimize;
        private readonly Timer _timer;
        private uint _lastPid;
        private IntPtr _lastHwnd;

        public event Action<ForegroundInfo> Changed;

        public ForegroundWatcher()
        {
            _callback = OnWinEvent;
            _hookForeground = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _callback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
            _hookMinimize = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_MINIMIZEEND, NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
                IntPtr.Zero, _callback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
            if (_hookForeground == IntPtr.Zero) Log.Warn("Vordergrund-Ereignis konnte nicht registriert werden - nur Abfrage aktiv");

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => Check(false);
            _timer.Start();
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            Check(false);
        }

        /// <summary>Prüft das aktuelle Vordergrundfenster; force meldet auch ohne Wechsel.</summary>
        public void Check(bool force)
        {
            try
            {
                IntPtr hwnd = NativeMethods.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return; // z. B. während eines Fensterwechsels
                uint pid;
                NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                if (!force && pid == _lastPid && hwnd == _lastHwnd) return;
                bool pidChanged = pid != _lastPid;
                _lastPid = pid;
                _lastHwnd = hwnd;
                if (!force && !pidChanged) return;

                var info = Query(pid);
                var handler = Changed;
                if (handler != null) handler(info);
            }
            catch (Exception ex)
            {
                Log.Error("Vordergrund-Prüfung: {0}", ex.Message);
            }
        }

        public static ForegroundInfo Query(uint pid)
        {
            var info = new ForegroundInfo { ProcessId = pid };
            IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    uint size = (uint)sb.Capacity;
                    if (NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size))
                        info.FullPath = sb.ToString(0, (int)size);
                }
                finally
                {
                    NativeMethods.CloseHandle(h);
                }
            }
            if (info.FullPath != null)
            {
                info.FileName = Path.GetFileName(info.FullPath);
            }
            else
            {
                try { info.FileName = Process.GetProcessById((int)pid).ProcessName + ".exe"; }
                catch { info.FileName = null; }
            }
            return info;
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
            if (_hookForeground != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hookForeground);
            if (_hookMinimize != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hookMinimize);
        }
    }
}
