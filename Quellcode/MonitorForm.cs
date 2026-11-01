// Monitor Dimmer – Fenster „Monitore“
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace MonitorDimmer
{
    /// <summary>Grafische Übersicht der Monitore mit Einblendung der Handle-Nummer auf jedem Monitor.</summary>
    internal sealed class MonitorForm : Form
    {
        // ---------- Farben ----------
        private static Color CBack { get { return Theme.Map(Color.FromArgb(243, 243, 243)); } }
        private static Color CCard { get { return Theme.Map(Color.White); } }
        private static Color CBorder { get { return Theme.Map(Color.FromArgb(229, 229, 229)); } }
        private static Color CText { get { return Theme.Map(Color.FromArgb(27, 27, 27)); } }
        private static Color CMuted { get { return Theme.Map(Color.FromArgb(97, 97, 97)); } }
        private static Color CAccent { get { return Theme.Map(Color.FromArgb(245, 158, 11)); } }
        private static Color CAccentSoft { get { return Theme.Map(Color.FromArgb(255, 247, 230)); } }
        private static Color CBlue { get { return Theme.Map(Color.FromArgb(91, 123, 213)); } }
        private static Color CTrack { get { return Theme.Map(Color.FromArgb(238, 238, 238)); } }
        internal static readonly Color CNavy = Color.FromArgb(20, 26, 38);
        internal static readonly Color CBright = Color.FromArgb(255, 200, 92);
        private static Color CBezel { get { return Theme.Map(Color.FromArgb(43, 50, 64)); } }

        private static MonitorForm _instance;

        private readonly MonitorManager _manager;
        private readonly Func<AppConfig> _config;
        private readonly Func<DimmingProfile> _current;
        private readonly Canvas _canvas;
        private readonly ModernButton _identify;
        private readonly HeaderPanel _header;
        private readonly Panel _footer;
        private readonly SignatureFooter _signature;
        private readonly List<IdentifyOverlay> _overlays = new List<IdentifyOverlay>();
        private List<MonitorSnapshot> _monitors = new List<MonitorSnapshot>();
        private float k = 1f;

        public static void ShowWindow(MonitorManager manager, Func<AppConfig> config, Func<DimmingProfile> current, Icon icon)
        {
            if (_instance != null && !_instance.IsDisposed)
            {
                _instance.RefreshData();
                _instance.Activate();
                return;
            }
            _instance = new MonitorForm(manager, config, current, icon);
            _instance.Show();
        }

        internal MonitorForm(MonitorManager manager, Func<AppConfig> config, Func<DimmingProfile> current, Icon icon)
        {
            _manager = manager;
            _config = config;
            _current = current;

            Text = "Monitor Dimmer - Monitore";
            if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.Manual;
            // Normaler Rahmen (Icon mit gleichem Abstand wie in den anderen Fenstern),
            // Größe bleibt trotzdem fest: Ziehen am Rand ist in WndProc abgeschaltet.
            FormBorderStyle = FormBorderStyle.Sizable;
            SizeGripStyle = SizeGripStyle.Hide;
            MaximizeBox = false;
            BackColor = CBack;
            Font = new Font("Segoe UI", 9f);

            using (var g = CreateGraphics()) k = g.DpiX / 96f;

            _canvas = new Canvas { Dock = DockStyle.Fill, BackColor = CBack, AutoScroll = true };
            _canvas.Paint += OnCanvasPaint;

            // ---------- Kopf ----------
            _header = new HeaderPanel("Monitore", "Anordnung, Helligkeit und Kontrast der Monitore und Werte je Profil", k);
            var refresh = new ModernButton("Aktualisieren", '\uE72C', false, k);
            refresh.Click += (s, e) => RefreshData();
            _header.SetRight(refresh);

            // ---------- Knöpfe und Hinweis ----------
            _footer = new Panel { Dock = DockStyle.Bottom, BackColor = CBack, Padding = new Padding((int)(28 * k), 0, (int)(28 * k), 0) };
            var actions = new Panel { Dock = DockStyle.Top, Height = (int)(72 * k), BackColor = CBack };
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = CBack, Padding = new Padding(0, (int)(16 * k), 0, 0) };
            _identify = new ModernButton("Nummern ausblenden", '\uE7B3', true, k);
            _identify.Click += (s, e) => { if (_overlays.Count > 0) HideOverlays(); else ShowOverlays(); };
            left.Controls.Add(_identify);
            var close = new ModernButton("Schließen", '\0', false, k) { Margin = new Padding(0) };
            close.Click += (s, e) => Close();
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = CBack, Padding = new Padding(0, (int)(16 * k), 0, 0) };
            right.Controls.Add(close);
            actions.Controls.Add(left);
            actions.Controls.Add(right);
            var info = new InfoBanner("Die große Nummer entspricht Handle=\"...\" in der Konfiguration (Configurations\\Default.xml).\n" +
                "Solange dieses Fenster offen ist, steht sie auch groß auf jedem Monitor. Ein Klick auf die Einblendung blendet sie aus.", k) { Dock = DockStyle.Top };
            _footer.Controls.Add(info);
            _footer.Controls.Add(actions);
            _footer.Height = actions.Height + info.Height;
            _signature = new SignatureFooter(k);

            Controls.Add(_canvas);
            Controls.Add(_footer);
            Controls.Add(_signature);
            Controls.Add(_header);
            CancelButton = close;

            RefreshData();
            Theme.Apply(this);
        }

        // ================================================================== Daten

        private void RefreshData()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                _monitors = _manager.Snapshot(15000);
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            var ordered = _monitors.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                string pos = "";
                if (ordered.Count == 2) pos = i == 0 ? "links" : "rechts";
                else if (ordered.Count == 3) pos = i == 0 ? "links" : i == 1 ? "Mitte" : "rechts";
                else if (ordered.Count > 3) pos = (i + 1) + ". von links";
                ordered[i].PositionName = pos;
            }

            int profiles = _config().Profiles.Count;
            int content = (int)((4 + 200 + 16 + 172 + 16 + 74 + profiles * 32 + 8) * k);
            int buttons = _header.Height + _footer.Height + _signature.Height;
            int width = (int)(980 * k);

            // Nie höher als der freie Bereich des Monitors (ohne Taskleiste); sonst scrollt der Inhalt.
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int frame = Height - ClientSize.Height;
            int maxClient = area.Height - frame - (int)(24 * k);
            int client = Math.Min(content + buttons, maxClient);
            if (client < content + buttons) width += SystemInformation.VerticalScrollBarWidth;
            ClientSize = new Size(Math.Min(width, area.Width - (int)(24 * k)), client);
            _canvas.AutoScrollMinSize = new Size(0, content);
            if (!_positioned)
            {
                Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
                _positioned = true;
            }
            _canvas.Invalidate();

            if (_overlays.Count > 0 || !_overlaysHiddenByUser) ShowOverlays();
        }

        private bool _overlaysHiddenByUser;
        private bool _positioned;

        private void ShowOverlays()
        {
            HideOverlays();
            _overlaysHiddenByUser = false;
            foreach (var m in _monitors)
            {
                var o = new IdentifyOverlay(m);
                o.Click += (s, e) => HideOverlays();
                _overlays.Add(o);
                o.Show();
            }
            _identify.Text = "Nummern ausblenden";
            _identify.Glyph = '\uE7B3';
        }

        private void HideOverlays()
        {
            foreach (var o in _overlays) { o.Close(); o.Dispose(); }
            if (_overlays.Count > 0) _overlaysHiddenByUser = true;
            _overlays.Clear();
            _identify.Text = "Nummern auf Monitoren zeigen";
            _identify.Glyph = '\uE7B3';
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            HideOverlays();
            _instance = null;
            base.OnFormClosed(e);
        }

        // ================================================================== Zeichnen

        private float S(float v) { return v * k; }

        private void OnCanvasPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.TranslateTransform(_canvas.AutoScrollPosition.X, _canvas.AutoScrollPosition.Y);
            float W = _canvas.ClientSize.Width;
            float pad = S(28);
            float y = S(4);

            if (_monitors.Count == 0)
            {
                using (var f = new Font("Segoe UI", 11f))
                using (var b = new SolidBrush(CMuted))
                    g.DrawString("Keine Monitore mit DDC/CI gefunden.", f, b, pad, y + S(20));
                return;
            }

            var layoutRect = new RectangleF(pad, y, W - 2 * pad, S(200));
            DrawCard(g, layoutRect);
            DrawLayout(g, layoutRect);
            y += layoutRect.Height + S(16);

            var ordered = _monitors.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
            float gap = S(14);
            float cw = (W - 2 * pad - gap * (ordered.Count - 1)) / ordered.Count;
            for (int i = 0; i < ordered.Count; i++)
            {
                var r = new RectangleF(pad + i * (cw + gap), y, cw, S(172));
                DrawCard(g, r);
                DrawMonitorCard(g, r, ordered[i]);
            }
            y += S(172) + S(16);

            var cfg = _config();
            var tableRect = new RectangleF(pad, y, W - 2 * pad, S(74) + cfg.Profiles.Count * S(32) - S(8));
            DrawCard(g, tableRect);
            DrawProfiles(g, tableRect, ordered, cfg);
        }

        private void DrawCard(Graphics g, RectangleF r)
        {
            using (var path = Rounded(r, S(10)))
            using (var b = new SolidBrush(CCard))
            using (var p = new Pen(CBorder, 1f))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
        }

        private void DrawLayout(Graphics g, RectangleF card)
        {
            var inner = RectangleF.Inflate(card, -S(24), -S(18));
            float labelSpace = S(40);
            int ux0 = _monitors.Min(m => m.Bounds.Left), uy0 = _monitors.Min(m => m.Bounds.Top);
            int ux1 = _monitors.Max(m => m.Bounds.Right), uy1 = _monitors.Max(m => m.Bounds.Bottom);
            float scale = Math.Min(inner.Width / (ux1 - ux0), (inner.Height - labelSpace) / (uy1 - uy0));
            float ox = inner.X + (inner.Width - (ux1 - ux0) * scale) / 2;
            float oy = inner.Y;

            foreach (var m in _monitors)
            {
                var r = new RectangleF(ox + (m.Bounds.X - ux0) * scale, oy + (m.Bounds.Y - uy0) * scale,
                    m.Bounds.Width * scale, m.Bounds.Height * scale);
                r = RectangleF.Inflate(r, -S(7), 0);

                // Standfuß
                using (var bz = new SolidBrush(CBezel))
                {
                    float nw = r.Width * 0.10f;
                    g.FillRectangle(bz, r.X + (r.Width - nw) / 2, r.Bottom - S(2), nw, S(10));
                    using (var basePath = Rounded(new RectangleF(r.X + r.Width * 0.32f, r.Bottom + S(7), r.Width * 0.36f, S(5)), S(2.5f)))
                        g.FillPath(bz, basePath);
                }
                // Gehäuse und Bild
                using (var path = Rounded(r, S(7)))
                using (var bz = new SolidBrush(CBezel))
                    g.FillPath(bz, path);
                var screen = RectangleF.Inflate(r, -S(5), -S(5));
                float level = Level(m.Brightness, m.BrightnessMax);
                Color top = Lerp(CNavy, CBright, level);
                Color bottom = Lerp(CNavy, Color.FromArgb(245, 150, 40), level);
                using (var path = Rounded(screen, S(4)))
                using (var lg = new LinearGradientBrush(screen, top, bottom, 90f))
                    g.FillPath(lg, path);

                // Große Nummer
                Color numColor = level > 0.5f ? CNavy : Color.White;
                using (var f = new Font("Segoe UI Semibold", Math.Max(8f, screen.Height * 0.62f), GraphicsUnit.Pixel))
                using (var b = new SolidBrush(numColor))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(m.Index.ToString(), f, b, screen, sf);

                if (m.IsPrimary)
                {
                    using (var f = new Font("Segoe UI", 7.5f))
                    {
                        string t = "Hauptmonitor";
                        var sz = g.MeasureString(t, f);
                        var pill = new RectangleF(screen.X + S(6), screen.Y + S(6), sz.Width + S(8), sz.Height + S(1));
                        using (var pp = Rounded(pill, pill.Height / 2))
                        using (var pb = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                            g.FillPath(pb, pp);
                        using (var tb = new SolidBrush(CNavy))
                            g.DrawString(t, f, tb, pill.X + S(4), pill.Y + S(0.5f));
                    }
                }

                using (var f = new Font("Segoe UI", 9f))
                using (var b = new SolidBrush(CMuted))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                {
                    string label = "Handle " + m.Index + (m.PositionName.Length > 0 ? "  ·  " + m.PositionName : "");
                    g.DrawString(label, f, b, new RectangleF(r.X - S(20), r.Bottom + S(15), r.Width + S(40), S(20)), sf);
                }
            }
        }

        private void DrawMonitorCard(Graphics g, RectangleF r, MonitorSnapshot m)
        {
            float x = r.X + S(16), y = r.Y + S(14), right = r.Right - S(16);

            // Nummer im Kreis
            var circle = new RectangleF(x, y, S(32), S(32));
            using (var b = new SolidBrush(CAccent)) g.FillEllipse(b, circle);
            using (var f = new Font("Segoe UI Semibold", 12f))
            using (var b = new SolidBrush(Color.White))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(m.Index.ToString(), f, b, circle, sf);

            using (var fTitle = new Font("Segoe UI Semibold", 11f))
            using (var fText = new Font("Segoe UI", 9f))
            using (var fSmall = new Font("Segoe UI", 8.5f))
            using (var bText = new SolidBrush(CText))
            using (var bMuted = new SolidBrush(CMuted))
            {
                string title = "Handle " + m.Index;
                g.DrawString(title, fTitle, bText, x + S(42), y - S(1));
                var tw = g.MeasureString(title, fTitle).Width;
                string sub = m.PositionName + (m.IsPrimary ? (m.PositionName.Length > 0 ? " · " : "") + "Hauptmonitor" : "");
                if (sub.Length > 0) g.DrawString(sub, fText, bMuted, x + S(42) + tw, y + S(2.5f));
                g.DrawString(m.DeviceName.Replace("\\\\.\\", "") + "  ·  " + m.Bounds.Width + " × " + m.Bounds.Height,
                    fSmall, bMuted, x + S(42), y + S(19));

                var descRect = new RectangleF(x, y + S(42), right - x, S(18));
                using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                    g.DrawString(m.Description, fText, bText, descRect, sf);

                DrawBar(g, "Helligkeit", m.Brightness, m.BrightnessMax, CAccent, x, y + S(72), right, fText, bText, bMuted);
                DrawBar(g, "Kontrast", m.Contrast, m.ContrastMax, CBlue, x, y + S(110), right, fText, bText, bMuted);
            }
        }

        private void DrawBar(Graphics g, string label, int value, int max, Color color, float x, float y, float right,
            Font f, Brush bText, Brush bMuted)
        {
            g.DrawString(label, f, bText, x - S(1), y);
            string v = value < 0 ? "nicht lesbar" : value + " / " + max;
            var vs = g.MeasureString(v, f);
            g.DrawString(v, f, bMuted, right - vs.Width + S(2), y);
            var track = new RectangleF(x, y + S(21), right - x, S(8));
            using (var tp = Rounded(track, track.Height / 2))
            using (var tb = new SolidBrush(CTrack))
                g.FillPath(tb, tp);
            if (value > 0)
            {
                var fill = new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * Level(value, max)), track.Height);
                using (var fp = Rounded(fill, fill.Height / 2))
                using (var fb = new SolidBrush(color))
                    g.FillPath(fb, fp);
            }
        }

        private void DrawProfiles(Graphics g, RectangleF r, List<MonitorSnapshot> ordered, AppConfig cfg)
        {
            float x = r.X + S(16), y = r.Y + S(14), right = r.Right - S(16);
            var current = _current();
            using (var fTitle = new Font("Segoe UI Semibold", 11f))
            using (var fText = new Font("Segoe UI", 9f))
            using (var fHead = new Font("Segoe UI Semibold", 8.5f))
            using (var bText = new SolidBrush(CText))
            using (var bMuted = new SolidBrush(CMuted))
            {
                g.DrawString("Profile", fTitle, bText, x - S(1), y - S(1));
                var tw = g.MeasureString("Profile", fTitle).Width;
                g.DrawString("Helligkeit / Kontrast je Monitor", fText, bMuted, x + tw, y + S(2.5f));
                y += S(34);

                float firstCol = (right - x) * 0.40f;
                float colW = (right - x - firstCol) / Math.Max(1, ordered.Count);
                g.DrawString("PROFIL", fHead, bMuted, x, y);
                for (int i = 0; i < ordered.Count; i++)
                {
                    string h = "HANDLE " + ordered[i].Index + (ordered[i].PositionName.Length > 0 ? " · " + ordered[i].PositionName.ToUpperInvariant() : "");
                    g.DrawString(h, fHead, bMuted, x + firstCol + i * colW, y);
                }
                y += S(22);

                foreach (var p in cfg.Profiles)
                {
                    var row = new RectangleF(r.X + S(6), y - S(4), r.Width - S(12), S(30));
                    bool active = current != null && string.Equals(current.Name, p.Name, StringComparison.OrdinalIgnoreCase);
                    if (active)
                    {
                        using (var rp = Rounded(row, S(6)))
                        using (var rb = new SolidBrush(CAccentSoft))
                            g.FillPath(rb, rp);
                        using (var ab = new SolidBrush(CAccent))
                            g.FillRectangle(ab, row.X, row.Y + S(6), S(3), row.Height - S(12));
                    }
                    else
                    {
                        using (var lp = new Pen(CTrack, 1f))
                            g.DrawLine(lp, x, y - S(4), right, y - S(4));
                    }

                    var nameRect = new RectangleF(x, y + S(1), firstCol - S(60), S(20));
                    using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                        g.DrawString(p.Title, fText, bText, nameRect, sf);
                    if (active)
                    {
                        var nw = Math.Min(g.MeasureString(p.Title, fText).Width, nameRect.Width);
                        DrawPill(g, "aktiv", new PointF(x + nw + S(6), y + S(2)), CAccent, Color.White);
                    }

                    for (int i = 0; i < ordered.Count; i++)
                    {
                        MonitorValues v;
                        float cx = x + firstCol + i * colW;
                        if (!p.Monitors.TryGetValue(ordered[i].Index, out v))
                        {
                            g.DrawString("-", fText, bMuted, cx, y + S(1));
                            continue;
                        }
                        string bs = v.Brightness.HasValue ? v.Brightness.Value.ToString() : "-";
                        string cs = v.Contrast.HasValue ? v.Contrast.Value.ToString() : "-";
                        float lvl = v.Brightness.HasValue ? Level(v.Brightness.Value, ordered[i].BrightnessMax) : 0f;
                        var sw = new RectangleF(cx, y + S(4), S(12), S(12));
                        using (var sp = Rounded(sw, S(3)))
                        using (var sb = new SolidBrush(Lerp(CNavy, CBright, lvl)))
                            g.FillPath(sb, sp);
                        g.DrawString(bs + " / " + cs, fText, bText, cx + S(18), y + S(1));
                    }
                    y += S(32);
                }
            }
        }

        private void DrawPill(Graphics g, string text, PointF at, Color back, Color fore)
        {
            using (var f = new Font("Segoe UI Semibold", 7.5f))
            {
                var sz = g.MeasureString(text, f);
                var r = new RectangleF(at.X, at.Y, sz.Width + S(6), sz.Height + S(1));
                using (var p = Rounded(r, r.Height / 2))
                using (var b = new SolidBrush(back))
                    g.FillPath(b, p);
                using (var b = new SolidBrush(fore))
                    g.DrawString(text, f, b, r.X + S(3), r.Y + S(0.5f));
            }
        }

        // ================================================================== Hilfen

        internal static float Level(int value, int max)
        {
            if (value < 0 || max <= 0) return 0f;
            return Math.Max(0f, Math.Min(1f, value / (float)max));
        }

        internal static Color Lerp(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        internal static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Max(0.5f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // WM_NCHITTEST: Rand und Ecken (HTLEFT..HTBOTTOMRIGHT) wie festen Rahmen behandeln -> keine Größenänderung
            if (m.Msg == 0x0084)
            {
                int hit = m.Result.ToInt32();
                if (hit >= 10 && hit <= 17) m.Result = (IntPtr)1; // HTCLIENT: keine Größenänderung (wie in allen Programmen)
            }
        }

        private sealed class Canvas : Panel
        {
            public Canvas()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnScroll(ScrollEventArgs se)
            {
                base.OnScroll(se);
                Invalidate();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                Invalidate();
            }
        }
    }

    /// <summary>Große Einblendung der Handle-Nummer mitten auf einem Monitor.</summary>
    internal sealed class IdentifyOverlay : Form
    {
        private readonly MonitorSnapshot _m;

        public IdentifyOverlay(MonitorSnapshot m)
        {
            _m = m;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = MonitorForm.CNavy;
            Opacity = 0.94;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            int w = Math.Max(320, (int)(m.Bounds.Width * 0.20));
            int h = (int)(w * 0.66);
            Bounds = new Rectangle(m.Bounds.X + (m.Bounds.Width - w) / 2, m.Bounds.Y + (m.Bounds.Height - h) / 2, w, h);
            using (var path = MonitorForm.Rounded(new RectangleF(0, 0, w, h), h * 0.07f))
                Region = new Region(path);
            Cursor = Cursors.Hand;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080 | 0x08000000 | 0x00000008; // TOOLWINDOW | NOACTIVATE | TOPMOST
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            // Position hart setzen, damit Windows das Fenster nicht verschiebt (Fenster liegen auf verschiedenen Monitoren)
            base.OnShown(e);
            int w = Width, h = Height;
            NativeMethods.SetWindowPos(Handle, new IntPtr(-1),
                _m.Bounds.X + (_m.Bounds.Width - w) / 2, _m.Bounds.Y + (_m.Bounds.Height - h) / 2, w, h, 0x0010);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float w = ClientSize.Width, h = ClientSize.Height;

            using (var bg = new LinearGradientBrush(new RectangleF(0, 0, w, h), Color.FromArgb(28, 36, 54), MonitorForm.CNavy, 90f))
                g.FillRectangle(bg, 0, 0, w, h);
            float bw = Math.Max(3f, h * 0.014f);
            using (var path = MonitorForm.Rounded(new RectangleF(bw / 2, bw / 2, w - bw, h - bw), h * 0.07f))
            using (var pen = new Pen(MonitorForm.CBright, bw))
                g.DrawPath(pen, path);

            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near })
            {
                using (var f = new Font("Segoe UI Semibold", h * 0.44f, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(MonitorForm.CBright))
                    g.DrawString(_m.Index.ToString(), f, b, new RectangleF(0, h * 0.02f, w, h * 0.56f), sf);

                using (var f = new Font("Segoe UI Semibold", h * 0.085f, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(Color.White))
                    g.DrawString("Handle " + _m.Index + (_m.PositionName.Length > 0 ? "  ·  " + _m.PositionName : ""), f, b,
                        new RectangleF(0, h * 0.58f, w, h * 0.12f), sf);

                using (var f = new Font("Segoe UI", h * 0.055f, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(Color.FromArgb(180, 190, 210)))
                {
                    string line1 = _m.Description + (_m.IsPrimary ? "  ·  Hauptmonitor" : "");
                    string line2 = "Helligkeit " + (_m.Brightness < 0 ? "?" : _m.Brightness.ToString()) +
                                   "   ·   Kontrast " + (_m.Contrast < 0 ? "?" : _m.Contrast.ToString());
                    g.DrawString(line1, f, b, new RectangleF(0, h * 0.73f, w, h * 0.09f), sf);
                    g.DrawString(line2, f, b, new RectangleF(0, h * 0.83f, w, h * 0.09f), sf);
                }
            }
        }
    }
}
