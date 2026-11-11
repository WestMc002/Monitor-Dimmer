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
                    string text = GetToolTip(e.AssociatedControl) ?? "";
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
    }
}
