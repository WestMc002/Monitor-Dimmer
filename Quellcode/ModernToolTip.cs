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
    /// Tooltip im Programmdesign (globale Vorgabe „Weitere Fenster“, Referenz Wake on LAN ab v1.51):
    /// Kartenfarbe, 1 px Rand in Rahmenfarbe, Text in Textfarbe, Segoe UI 9 pt, Innenabstand 10 × 6, Umbruch bei 400.
    /// Ein gesetzter ToolTipTitle wird fett über dem Text gezeichnet.
    /// </summary>
    internal sealed class ModernToolTip : ToolTip
    {
        private static readonly Font TipFont = new Font("Segoe UI", 9f);
        private static readonly Font TitleFont = new Font("Segoe UI Semibold", 9f);
        private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top;

        public ModernToolTip()
        {
            OwnerDraw = true;
            Popup += (s, e) =>
            {
                float k;
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) k = g.DpiX / 96f;
                int max = (int)(400 * k);
                string text = GetToolTip(e.AssociatedControl) ?? "";
                var size = TextRenderer.MeasureText(text, TipFont, new Size(max, 0), Flags);
                int w = size.Width, h = size.Height;
                if (!string.IsNullOrEmpty(ToolTipTitle))
                {
                    var t = TextRenderer.MeasureText(ToolTipTitle, TitleFont, new Size(max, 0), Flags);
                    w = Math.Max(w, t.Width);
                    h += t.Height + (int)(4 * k);
                }
                e.ToolTipSize = new Size(w + (int)(20 * k), h + (int)(12 * k));
            };
            Draw += (s, e) =>
            {
                float k = e.Graphics.DpiX / 96f;
                using (var back = new SolidBrush(Ui.Card)) e.Graphics.FillRectangle(back, e.Bounds);
                using (var pen = new Pen(Ui.Border)) e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                var r = new Rectangle((int)(10 * k), (int)(6 * k), e.Bounds.Width - (int)(20 * k), e.Bounds.Height - (int)(12 * k));
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
