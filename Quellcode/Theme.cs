// Monitor Dimmer – Hell- und Dunkelmodus
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MonitorDimmer
{
    /// <summary>
    /// Helles/dunkles Design für die Fenster. Folgt der Windows-Einstellung "App-Modus".
    /// Die Fenster sind hell gestaltet; im dunklen Modus werden die hellen Farben über Map() übersetzt.
    /// </summary>
    internal static class Theme
    {
        private static bool? _dark;

        /// <summary>Feste Vorgabe statt der Windows-Einstellung (nur für die Screenshots, sonst null).</summary>
        public static bool? Override;
        private static readonly Dictionary<int, Color> DarkMap = new Dictionary<int, Color>();

        static Theme()
        {
            Add(Color.FromArgb(243, 243, 243), Color.FromArgb(32, 32, 32));    // Fensterhintergrund
            Add(Color.White, Color.FromArgb(43, 43, 43));                       // Karten, Listen
            Add(Color.FromArgb(229, 229, 229), Color.FromArgb(61, 61, 61));    // Rahmen
            Add(Color.FromArgb(27, 27, 27), Color.FromArgb(250, 250, 250));    // Text
            Add(Color.FromArgb(97, 97, 97), Color.FromArgb(206, 206, 206)); // Grauer Text
            Add(Color.FromArgb(238, 238, 238), Color.FromArgb(60, 60, 60));    // Balken-Hintergrund
            Add(Color.FromArgb(234, 234, 234), Color.FromArgb(58, 58, 58));    // Tabellenlinien
            Add(Color.FromArgb(232, 240, 254), Color.FromArgb(48, 66, 104));   // Markierung
            Add(Color.FromArgb(255, 247, 230), Color.FromArgb(72, 54, 22));    // aktives Profil
            Add(Color.FromArgb(15, 123, 15), Color.FromArgb(108, 203, 95));    // Grün
            Add(Color.FromArgb(196, 43, 28), Color.FromArgb(255, 153, 164));   // Rot
            Add(Color.FromArgb(157, 93, 0), Color.FromArgb(252, 225, 0));      // Gelb/Hinweis (Windows 11)
            Add(Color.FromArgb(237, 237, 237), Color.FromArgb(55, 55, 55));    // Markierung in Tabellen (Windows 11)
            Add(Color.FromArgb(91, 123, 213), Color.FromArgb(129, 160, 240));  // Blau
            Add(Color.FromArgb(157, 157, 157), Color.FromArgb(110, 110, 110)); // deaktiviert
            Add(Color.FromArgb(219, 219, 219), Color.FromArgb(80, 80, 80));    // Platzhalter-Icon
            Add(Color.FromArgb(43, 50, 64), Color.FromArgb(78, 86, 104));      // Monitorrahmen
            Add(Color.FromArgb(246, 246, 246), Color.FromArgb(52, 52, 52));    // Knopf: Maus darüber
            Add(Color.FromArgb(236, 236, 236), Color.FromArgb(62, 62, 62));    // Knopf: gedrückt
            Add(Color.FromArgb(231, 240, 252), Color.FromArgb(34, 46, 68));    // Hinweisbalken
            Add(Color.FromArgb(190, 210, 240), Color.FromArgb(58, 78, 118));   // Hinweisbalken-Rahmen
            Add(Color.FromArgb(29, 78, 216), Color.FromArgb(147, 180, 255));   // Versions-Plakette
            Add(SystemColors.Control, Color.FromArgb(32, 32, 32));
            Add(SystemColors.Window, Color.FromArgb(43, 43, 43));
            Add(SystemColors.WindowText, Color.FromArgb(250, 250, 250));
            Add(SystemColors.ControlText, Color.FromArgb(250, 250, 250));
        }

        private static void Add(Color light, Color dark)
        {
            DarkMap[Key(light)] = dark;
        }

        private static int Key(Color c)
        {
            return Color.FromArgb(c.A, c.R, c.G, c.B).ToArgb();
        }

        /// <summary>Windows-Einstellung "App-Modus: Dunkel".</summary>
        public static bool Dark
        {
            get
            {
                if (Override.HasValue) return Override.Value;
                if (_dark.HasValue) return _dark.Value;
                bool dark = false;
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    {
                        object v = key == null ? null : key.GetValue("AppsUseLightTheme");
                        if (v is int) dark = (int)v == 0;
                    }
                }
                catch { }
                _dark = dark;
                return dark;
            }
        }

        /// <summary>Beim Öffnen eines Fensters neu lesen (Einstellung kann sich geändert haben).</summary>
        public static void Refresh()
        {
            _dark = null;
            Accent.Refresh();
        }

        /// <summary>Helle Farbe -> passende dunkle Farbe (im hellen Modus unverändert).</summary>
        public static Color Map(Color c)
        {
            if (!Dark || c == Color.Transparent || c.A == 0) return c;
            Color d;
            return DarkMap.TryGetValue(Key(c), out d) ? d : c;
        }

        // ------------------------------------------------------------------

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        private static readonly Color ButtonBack = Color.FromArgb(55, 55, 55);
        private static readonly Color ButtonHover = Color.FromArgb(66, 66, 66);
        private static readonly Color ButtonDown = Color.FromArgb(78, 78, 78);
        private static readonly Color ButtonBorder = Color.FromArgb(85, 85, 85);
        private static readonly Color InputBack = Color.FromArgb(50, 50, 50);

        /// <summary>Färbt ein Fenster samt aller Steuerelemente dunkel (nur im dunklen Modus).</summary>
        public static void Apply(Form form)
        {
            ComboStyle.ApplyAll(form); // in hell und dunkel: Text in Auswahllisten mit Abstand zum Rand
            if (!Dark) return;
            WhenHandle(form, h =>
            {
                int on = 1;
                if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(h, 19, ref on, sizeof(int)); // ältere Windows-10-Versionen
            });
            ApplyTo(form);
        }

        private static void ApplyTo(Control c)
        {
            c.BackColor = Map(c.BackColor);
            c.ForeColor = Map(c.ForeColor);

            var button = c as Button;
            if (button != null && !(c is ModernButton))
            {
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = ButtonBack;
                button.ForeColor = Map(Color.FromArgb(27, 27, 27));
                button.FlatAppearance.BorderColor = ButtonBorder;
                button.FlatAppearance.MouseOverBackColor = ButtonHover;
                button.FlatAppearance.MouseDownBackColor = ButtonDown;
                button.UseVisualStyleBackColor = false;
            }

            var combo = c as ComboBox;
            if (combo != null)
            {
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = InputBack;
                WhenHandle(combo, h => SetWindowTheme(h, "DarkMode_CFD", null));
            }

            var list = c as ListView;
            if (list != null)
            {
                list.BackColor = Map(Color.White);
                list.ForeColor = Map(Color.FromArgb(27, 27, 27));
                WhenHandle(list, h =>
                {
                    SetWindowTheme(h, "DarkMode_Explorer", null);
                    IntPtr header = SendMessage(h, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
                    if (header != IntPtr.Zero) SetWindowTheme(header, "DarkMode_ItemsView", null);
                });
            }

            var grid = c as DataGridView;
            if (grid != null) ApplyToGrid(grid);

            var scroll = c as ScrollBar;
            if (scroll != null) WhenHandle(scroll, h => SetWindowTheme(h, "DarkMode_Explorer", null));

            var scrollable = c as ScrollableControl;
            if (scrollable != null && scrollable.AutoScroll) WhenHandle(c, h => SetWindowTheme(h, "DarkMode_Explorer", null));

            foreach (Control child in c.Controls) ApplyTo(child);
        }

        private static void ApplyToGrid(DataGridView grid)
        {
            grid.BackgroundColor = Map(grid.BackgroundColor);
            grid.GridColor = Map(grid.GridColor);
            MapStyle(grid.DefaultCellStyle);
            MapStyle(grid.ColumnHeadersDefaultCellStyle);
            MapStyle(grid.RowsDefaultCellStyle);
            if (grid.DefaultCellStyle.BackColor.IsEmpty || grid.DefaultCellStyle.BackColor == SystemColors.Window)
                grid.DefaultCellStyle.BackColor = Map(Color.White);
            if (grid.DefaultCellStyle.ForeColor.IsEmpty || grid.DefaultCellStyle.ForeColor == SystemColors.ControlText)
                grid.DefaultCellStyle.ForeColor = Map(Color.FromArgb(27, 27, 27));
            foreach (DataGridViewColumn col in grid.Columns) MapStyle(col.DefaultCellStyle);
            foreach (DataGridViewRow row in grid.Rows)
            {
                MapStyle(row.DefaultCellStyle);
                foreach (DataGridViewCell cell in row.Cells)
                    if (cell.HasStyle) MapStyle(cell.Style);
            }
            WhenHandle(grid, h => SetWindowTheme(h, "DarkMode_Explorer", null));
        }

        private static void MapStyle(DataGridViewCellStyle s)
        {
            if (s == null) return;
            if (!s.BackColor.IsEmpty) s.BackColor = Map(s.BackColor);
            if (!s.ForeColor.IsEmpty) s.ForeColor = Map(s.ForeColor);
            if (!s.SelectionBackColor.IsEmpty) s.SelectionBackColor = Map(s.SelectionBackColor);
            if (!s.SelectionForeColor.IsEmpty) s.SelectionForeColor = Map(s.SelectionForeColor);
        }

        private static void WhenHandle(Control c, Action<IntPtr> action)
        {
            if (c.IsHandleCreated) action(c.Handle);
            else c.HandleCreated += (s, e) => action(c.Handle);
        }
    }
}
