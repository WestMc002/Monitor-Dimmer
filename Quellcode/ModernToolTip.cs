// Monitor Dimmer – Tooltips im Programmdesign
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Drawing;
using System.Windows.Forms;

namespace MonitorDimmer
{
    /// <summary>
    /// Tooltip im Programmdesign (globale Vorgabe „Weitere Fenster“, Referenz Wake on LAN ab v1.61):
    /// Kartenfarbe, 1 px Rand in Rahmenfarbe, Text in Textfarbe, Segoe UI 9 pt, Innenabstand 8 × 5 an allen Seiten gleich,
    /// Umbruch bei 400, 15 s sichtbar. Ein gesetzter ToolTipTitle wird fett über dem Text gezeichnet.
    /// </summary>
    internal sealed class ModernToolTip : ToolTip
    {
        private static readonly Font TipFont = new Font("Segoe UI", 9f);
        private static readonly Font TitleFont = new Font("Segoe UI Semibold", 9f);
        // NoPadding: sonst rechnet Windows links und rechts einen Zuschlag ein und der Rand wird ungleich
        private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding
            | TextFormatFlags.Left | TextFormatFlags.Top;

        private string _shown; // Text bei Show() (Listeneinträge), sonst der per SetToolTip gesetzte

        public ModernToolTip()
        {
            OwnerDraw = true;
            AutoPopDelay = 15000; // 15 s sichtbar statt 5 s
            Popup += (s, e) =>
            {
                // Mit Graphics messen: ohne rechnet Windows trotz NoPadding einen Zuschlag ein (Rand rechts größer als links)
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float k = g.DpiX / 96f;
                    int max = (int)(400 * k);
                    string text = _shown ?? GetToolTip(e.AssociatedControl) ?? "";
                    var size = TextRenderer.MeasureText(g, text, TipFont, new Size(max, 0), Flags);
                    int w = size.Width, h = size.Height;
                    if (!string.IsNullOrEmpty(ToolTipTitle))
                    {
                        var t = TextRenderer.MeasureText(g, ToolTipTitle, TitleFont, new Size(max, 0), Flags);
                        w = Math.Max(w, t.Width);
                        h += t.Height + (int)(4 * k);
                    }
                    e.ToolTipSize = new Size(w + 2 * (int)(8 * k) + 1, h + 2 * (int)(5 * k)); // 1 px Breite als Reserve
                }
            };
            Draw += (s, e) =>
            {
                float k = e.Graphics.DpiX / 96f;
                int px = (int)(8 * k), py = (int)(5 * k);
                using (var back = new SolidBrush(Ui.Card)) e.Graphics.FillRectangle(back, e.Bounds);
                using (var pen = new Pen(Ui.Border)) e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                var r = new Rectangle(px, py, e.Bounds.Width - 2 * px, e.Bounds.Height - 2 * py);
                if (!string.IsNullOrEmpty(ToolTipTitle))
                {
                    var t = TextRenderer.MeasureText(e.Graphics, ToolTipTitle, TitleFont, new Size(r.Width, 0), Flags);
                    TextRenderer.DrawText(e.Graphics, ToolTipTitle, TitleFont, r, Ui.Text, Flags);
                    int dy = t.Height + (int)(4 * k);
                    r = new Rectangle(r.X, r.Y + dy, r.Width, r.Height - dy);
                }
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, TipFont, r, Ui.Text, Flags);
            };
        }

        /// <summary>
        /// Hinweise der Listeneinträge (ListViewItem.ToolTipText) im Programmdesign statt des Windows-Hinweises der Liste
        /// (der hat ungleiche Ränder und bleibt im Dunkelmodus hell). Schaltet ShowItemToolTips der Liste ab.
        /// </summary>
        public static void AttachItems(ListView list)
        {
            list.ShowItemToolTips = false;
            var tip = new ModernToolTip();
            ListViewItem current = null;
            var timer = new Timer { Interval = SystemInformation.MouseHoverTime + 100 };
            Point at = Point.Empty;
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (current == null || list.IsDisposed || string.IsNullOrEmpty(current.ToolTipText)) return;
                tip.ShowAt(current.ToolTipText, list, new Point(at.X, at.Y + Cursor.Current.Size.Height * 2 / 3));
            };
            list.MouseMove += (s, e) =>
            {
                var hit = list.GetItemAt(e.X, e.Y);
                if (hit == current) return;
                current = hit;
                tip.Hide(list);
                timer.Stop();
                at = e.Location;
                if (hit != null && !string.IsNullOrEmpty(hit.ToolTipText)) timer.Start();
            };
            list.MouseLeave += (s, e) => { current = null; timer.Stop(); tip.Hide(list); };
            list.MouseDown += (s, e) => { timer.Stop(); tip.Hide(list); };
            list.Disposed += (s, e) => { timer.Dispose(); tip.Dispose(); };
        }

        /// <summary>Hinweise von Zellen und Spaltenköpfen (ToolTipText) im Programmdesign statt des Windows-Hinweises.</summary>
        public static void AttachCells(DataGridView grid)
        {
            grid.ShowCellToolTips = false;
            var tip = new ModernToolTip();
            string text = null;
            var timer = new Timer { Interval = SystemInformation.MouseHoverTime + 100 };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (string.IsNullOrEmpty(text) || grid.IsDisposed) return;
                var p = grid.PointToClient(Cursor.Position);
                tip.ShowAt(text, grid, new Point(p.X, p.Y + Cursor.Current.Size.Height * 2 / 3));
            };
            grid.CellMouseEnter += (s, e) =>
            {
                tip.Hide(grid);
                timer.Stop();
                text = null;
                if (e.ColumnIndex < 0) return;
                if (e.RowIndex < 0) text = grid.Columns[e.ColumnIndex].ToolTipText;
                else text = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].ToolTipText;
                if (!string.IsNullOrEmpty(text)) timer.Start();
            };
            grid.CellMouseLeave += (s, e) => { text = null; timer.Stop(); tip.Hide(grid); };
            grid.MouseDown += (s, e) => { timer.Stop(); tip.Hide(grid); };
            grid.Disposed += (s, e) => { timer.Dispose(); tip.Dispose(); };
        }

        /// <summary>Text an einer Stelle zeigen (wie Show, aber mit richtiger Größe im Programmdesign).</summary>
        public void ShowAt(string text, IWin32Window window, Point point)
        {
            _shown = text;
            try { Show(text, window, point, AutoPopDelay); }
            finally { _shown = null; }
        }
    }
}
