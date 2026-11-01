// Monitor Dimmer – Unsichtbares Fenster für Windows-Nachrichten (Monitore, Tastenkürzel)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MonitorDimmer
{
    /// <summary>Unsichtbares Fenster für Hotkeys, Bildschirmänderungen und Energie-Ereignisse.</summary>
    internal sealed class MessageWindow : NativeWindow, IDisposable
    {
        private readonly Dictionary<int, Action> _hotkeys = new Dictionary<int, Action>();
        private int _nextId = 1;
        private IntPtr _powerNotify;

        public event Action DisplayChanged;
        public event Action DisplayTurnedOn;
        public event Action Resumed;

        public MessageWindow()
        {
            CreateHandle(new CreateParams { Caption = "MonitorDimmerMessageWindow" });
            Guid g = NativeMethods.GUID_CONSOLE_DISPLAY_STATE;
            _powerNotify = NativeMethods.RegisterPowerSettingNotification(Handle, ref g, 0);
        }

        /// <summary>Registriert z. B. "Ctrl+Alt+F10". Liefert eine Fehlermeldung oder null.</summary>
        public string RegisterHotkey(string keys, Action action)
        {
            if (string.IsNullOrWhiteSpace(keys)) return "keine Tasten angegeben";
            uint mods = 0;
            Keys key = Keys.None;
            foreach (string raw in keys.Split('+'))
            {
                string t = raw.Trim().ToLowerInvariant();
                if (t == "ctrl" || t == "strg" || t == "control") mods |= NativeMethods.MOD_CONTROL;
                else if (t == "alt") mods |= NativeMethods.MOD_ALT;
                else if (t == "shift" || t == "umschalt") mods |= NativeMethods.MOD_SHIFT;
                else if (t == "win") mods |= NativeMethods.MOD_WIN;
                else
                {
                    Keys k;
                    if (t.Length == 1 && char.IsDigit(t[0])) t = "D" + t;
                    if (!Enum.TryParse(t, true, out k)) return "unbekannte Taste \"" + raw.Trim() + "\"";
                    key = k;
                }
            }
            if (key == Keys.None) return "keine Haupttaste angegeben";
            int id = _nextId++;
            if (!NativeMethods.RegisterHotKey(Handle, id, mods | NativeMethods.MOD_NOREPEAT, (uint)key))
                return "Tastenkombination ist bereits von einem anderen Programm belegt";
            _hotkeys[id] = action;
            return null;
        }

        public void UnregisterAllHotkeys()
        {
            foreach (int id in _hotkeys.Keys) NativeMethods.UnregisterHotKey(Handle, id);
            _hotkeys.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case NativeMethods.WM_HOTKEY:
                    Action a;
                    if (_hotkeys.TryGetValue(m.WParam.ToInt32(), out a)) a();
                    break;
                case NativeMethods.WM_DISPLAYCHANGE:
                    Raise(DisplayChanged);
                    break;
                case NativeMethods.WM_POWERBROADCAST:
                    int kind = m.WParam.ToInt32();
                    if (kind == NativeMethods.PBT_APMRESUMEAUTOMATIC)
                    {
                        Raise(Resumed);
                    }
                    else if (kind == NativeMethods.PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
                    {
                        var s = (NativeMethods.POWERBROADCAST_SETTING)Marshal.PtrToStructure(m.LParam, typeof(NativeMethods.POWERBROADCAST_SETTING));
                        if (s.PowerSetting == NativeMethods.GUID_CONSOLE_DISPLAY_STATE && s.Data == 1)
                            Raise(DisplayTurnedOn);
                    }
                    m.Result = new IntPtr(1);
                    break;
            }
            base.WndProc(ref m);
        }

        private static void Raise(Action a)
        {
            if (a != null) a();
        }

        public void Dispose()
        {
            UnregisterAllHotkeys();
            if (_powerNotify != IntPtr.Zero) NativeMethods.UnregisterPowerSettingNotification(_powerNotify);
            DestroyHandle();
        }
    }
}
