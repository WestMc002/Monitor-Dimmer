// Monitor Dimmer – Bausteine der Oberfläche im Windows-11-Stil (Kopf, Karten, Footer, Info-Fenster)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace MonitorDimmer
{
    /// <summary>Gemeinsame Farben und Hilfen für das moderne Fensterdesign (Stil wie "Fensterpositionen").</summary>
    internal static class Ui
    {
        public static Color Back { get { return Theme.Map(Color.FromArgb(243, 243, 243)); } }
        public static Color Card { get { return Theme.Map(Color.White); } }
        public static Color Border { get { return Theme.Map(Color.FromArgb(229, 229, 229)); } }
        public static Color Text { get { return Theme.Map(Color.FromArgb(27, 27, 27)); } }
        public static Color Muted { get { return Theme.Map(Color.FromArgb(97, 97, 97)); } }
        // Akzentfarbe: Windows-Akzentfarbe (Einstellungen > Personalisierung > Farben) oder festes Blau
        public static Color Primary { get { var p = Accent.Palette; return p != null ? (Theme.Dark ? p[1] : p[4]) : Color.FromArgb(37, 99, 235); } }
        public static Color PrimaryHover { get { var p = Accent.Palette; return p != null ? (Theme.Dark ? p[2] : p[5]) : Color.FromArgb(29, 78, 216); } }
        public static Color PrimaryDown { get { var p = Accent.Palette; return p != null ? (Theme.Dark ? p[3] : p[6]) : Color.FromArgb(30, 64, 175); } }
        /// <summary>Schrift auf dem Akzent-Knopf: weiß, bei hellen Akzentfarben dunkel.</summary>
        public static Color PrimaryText { get { return Accent.Luminance(Primary) > 0.5 ? Color.FromArgb(20, 20, 20) : Color.White; } }
        /// <summary>Akzentfarbe für Schrift und Symbole (Versionsnummer, Link, "i", "?").</summary>
        public static Color AccentText
        {
            get
            {
                var p = Accent.Palette;
                if (p == null) return Theme.Map(Color.FromArgb(29, 78, 216));
                return Theme.Dark ? p[1] : p[5];
            }
        }
        public static Color Hover { get { return Theme.Map(Color.FromArgb(246, 246, 246)); } }
        public static Color Down { get { return Theme.Map(Color.FromArgb(236, 236, 236)); } }
        public static Color Disabled { get { return Theme.Map(Color.FromArgb(157, 157, 157)); } }
        public static Color InfoBack
        {
            get
            {
                var p = Accent.Palette;
                return p != null ? Accent.Mix(p[3], Back, Theme.Dark ? 0.20 : 0.10) : Theme.Map(Color.FromArgb(231, 240, 252));
            }
        }
        public static Color InfoBorder
        {
            get
            {
                var p = Accent.Palette;
                return p != null ? Accent.Mix(p[3], Back, Theme.Dark ? 0.42 : 0.30) : Theme.Map(Color.FromArgb(190, 210, 240));
            }
        }
        public static Color PillText { get { return AccentText; } }

        public static float Scale(Control c)
        {
            using (var g = c.CreateGraphics()) return g.DpiX / 96f;
        }

        public static string Version
        {
            get { return AppInfo.Version; }
        }

        /// <summary>Großes Programm-Icon (aus Resources\MonitorDimmer.ico, sonst aus der exe).</summary>
        public static Image AppIcon(int size)
        {
            try
            {
                string p = Path.Combine(Application.StartupPath, "Resources", "MonitorDimmer.ico");
                if (File.Exists(p))
                    using (var ico = new Icon(p, size, size)) return ico.ToBitmap();
                using (var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                    return new Bitmap(ico.ToBitmap(), size, size);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Kopfbereich: Programm-Icon, Titel, Versions-Plakette, Unterzeile; rechts optional ein Knopf.</summary>
    internal sealed class HeaderPanel : Panel
    {
        private readonly float k;
        private readonly Image _icon;
        public string Title;
        public string Subtitle;
        private RectangleF _pill;
        private bool _pillHover;

        public HeaderPanel(string title, string subtitle, float scale)
        {
            Title = title;
            Subtitle = subtitle;
            k = scale;
            _icon = Ui.AppIcon(64);
            Dock = DockStyle.Top;
            Height = (int)(108 * k);
            BackColor = Ui.Back;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        /// <summary>Knopf rechts oben (z. B. "Ordner öffnen").</summary>
        public void SetRight(Control c)
        {
            c.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(c);
            Layout += (s, e) => c.Location = new Point(Width - c.Width - (int)(28 * k), (int)(32 * k));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float x = 28 * k, y = 24 * k, icon = 52 * k;
            if (_icon != null) g.DrawImage(_icon, x, y + 4 * k, icon, icon);
            float tx = x + icon + 18 * k;
            using (var fTitle = new Font("Segoe UI Semibold", 17f))
            using (var bText = new SolidBrush(Ui.Text))
            {
                g.DrawString(Title, fTitle, bText, tx - 3 * k, y - 2 * k);
                float tw = g.MeasureString(Title, fTitle).Width;
                // Versions-Plakette
                using (var fPill = new Font("Segoe UI", 9f))
                {
                    string v = "Version " + Ui.Version;
                    var sz = g.MeasureString(v, fPill);
                    var r = new RectangleF(tx + tw + 6 * k, y + 6 * k, sz.Width + 16 * k, sz.Height + 6 * k);
                    _pill = r;
                    using (var p = MonitorForm.Rounded(r, r.Height / 2))
                    {
                        using (var b = new SolidBrush(_pillHover ? Ui.InfoBorder : Ui.InfoBack)) g.FillPath(b, p);
                        using (var pen = new Pen(Ui.InfoBorder)) g.DrawPath(pen, p);
                    }
                    using (var b = new SolidBrush(Ui.PillText))
                        g.DrawString(v, fPill, b, r.X + 8 * k, r.Y + 3 * k);
                }
            }
            if (!string.IsNullOrEmpty(Subtitle))
                using (var f = new Font("Segoe UI", 10f))
                using (var b = new SolidBrush(Ui.Muted))
                    g.DrawString(Subtitle, f, b, tx - 1 * k, y + 38 * k);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = _pill.Contains(e.Location);
            if (h != _pillHover) { _pillHover = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_pillHover) { _pillHover = false; Cursor = Cursors.Default; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _pill.Contains(e.Location)) AboutDialog.ShowFor(FindForm());
            base.OnMouseClick(e);
        }
    }

    /// <summary>Weiße Karte mit abgerundeten Ecken und feinem Rahmen.</summary>
    internal sealed class CardPanel : Panel
    {
        private readonly float k;

        public CardPanel(float scale)
        {
            k = scale;
            BackColor = Ui.Card;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Padding = new Padding((int)(24 * k), (int)(18 * k), (int)(24 * k), (int)(18 * k));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var p = MonitorForm.Rounded(r, 8 * k))
            {
                using (var b = new SolidBrush(Ui.Card)) g.FillPath(b, p);
                using (var pen = new Pen(Ui.Border)) g.DrawPath(pen, p);
            }
        }
    }

    /// <summary>Hellblauer Hinweisbalken mit "i"-Symbol.</summary>
    internal sealed class InfoBanner : Panel
    {
        private readonly float k;
        private readonly Label _label;

        public InfoBanner(string text, float scale)
        {
            k = scale;
            BackColor = Ui.InfoBack;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Padding = new Padding((int)(56 * k), (int)(12 * k), (int)(20 * k), (int)(12 * k));
            _label = new Label { Dock = DockStyle.Fill, Text = text, ForeColor = Ui.Text, BackColor = Color.Transparent, Font = new Font("Segoe UI", 9.5f) };
            Controls.Add(_label);
            int lines = text.Split('\n').Length;
            Height = (int)((24 + lines * 22) * k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var p = MonitorForm.Rounded(r, 8 * k))
            {
                using (var b = new SolidBrush(Ui.InfoBack)) g.FillPath(b, p);
                using (var pen = new Pen(Ui.InfoBorder)) g.DrawPath(pen, p);
            }
            float d = 18 * k, cx = 24 * k, cy = Height / 2f;
            var blue = Ui.AccentText;
            using (var pen = new Pen(blue, 1.4f * k)) g.DrawEllipse(pen, cx - d / 2, cy - d / 2, d, d);
            using (var f = new Font("Segoe UI Semibold", 8f))
                TextRenderer.DrawText(g, "i", f, new Rectangle((int)(cx - d / 2), (int)(cy - d / 2), (int)d, (int)d), blue,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Runder Knopf mit Symbol. Primary = blau ausgefüllt, sonst weiß mit Rahmen.</summary>
    internal sealed class ModernButton : Button
    {
        private readonly float k;
        private bool _hover, _down;
        public bool Primary;
        public char Glyph;

        public ModernButton(string text, char glyph, bool primary, float scale)
        {
            k = scale;
            Text = text;
            Glyph = glyph;
            Primary = primary;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Segoe UI Semibold", 9.5f);
            Cursor = Cursors.Hand;
            Margin = new Padding(0, 0, (int)(10 * k), 0);
            AutoSize = false;
            UpdateSize();
        }

        public void UpdateSize()
        {
            int tw = TextRenderer.MeasureText(Text, Font).Width;
            int icon = Glyph == '\0' ? 0 : (int)((string.IsNullOrEmpty(Text) ? 16 : 26) * k);
            Size = new Size(tw + icon + (int)(36 * k), (int)(40 * k));
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (k > 0) UpdateSize();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill, text, border;
            if (Primary && Enabled)
            {
                fill = _down ? Ui.PrimaryDown : _hover ? Ui.PrimaryHover : Ui.Primary;
                text = Ui.PrimaryText;
                border = fill;
            }
            else
            {
                fill = !Enabled ? Ui.Card : _down ? Ui.Down : _hover ? Ui.Hover : Ui.Card;
                text = Enabled ? Ui.Text : Ui.Disabled;
                border = Ui.Border;
            }
            using (var p = MonitorForm.Rounded(r, 4 * k))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                using (var pen = new Pen(border)) g.DrawPath(pen, p);
            }

            int tw = TextRenderer.MeasureText(Text, Font).Width;
            int icon = Glyph == '\0' ? 0 : (int)((string.IsNullOrEmpty(Text) ? 16 : 26) * k);
            int x = (Width - tw - icon) / 2;
            if (Glyph != '\0')
            {
                int gs = (int)(16 * k);
                var img = MenuHelper.Glyph(Glyph, text, gs);
                if (img != null)
                {
                    g.DrawImage(img, x, (Height - gs) / 2, gs, gs);
                    img.Dispose();
                }
                x += icon;
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, tw + 2, Height), text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Fußzeile unten im Fenster: links Copyright, rechts GS-Logo mit Name und Website (anklickbar).</summary>
    internal sealed class SignatureFooter : Panel
    {
        public const string Url = "https://www.westmc002.de";
        private const string Name1 = "Günther Schoder";
        private const string Name2 = "www.westmc002.de";
        private static Image _logo;
        private static bool _logoLoaded;
        private readonly float k;
        private Rectangle _link;
        private bool _hover;

        public SignatureFooter(float scale)
        {
            k = scale;
            Dock = DockStyle.Bottom;
            Height = (int)(64 * k);
            BackColor = Ui.Back;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        /// <summary>Resources\GS-Logo.png (fehlt die Datei, steht nur der Text da).</summary>
        public static Image Logo
        {
            get
            {
                if (_logoLoaded) return _logo;
                _logoLoaded = true;
                try
                {
                    string p = Path.Combine(Application.StartupPath, "Resources", "GS-Logo.png");
                    if (File.Exists(p))
                        using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var img = Image.FromStream(fs))
                            _logo = new Bitmap(img);
                    else
                        // Fallback: von Erstellen.bat in die EXE eingebettetes Logo
                        using (var rs = typeof(Ui).Assembly.GetManifestResourceStream("GS-Logo.png"))
                            if (rs != null) using (var img = Image.FromStream(rs)) _logo = new Bitmap(img);
                }
                catch { _logo = null; }
                return _logo;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int pad = (int)(28 * k);
            int right = Width - pad;
            using (var f1 = new Font("Segoe UI Semibold", 9.5f))
            using (var f2 = new Font("Segoe UI", 9f, _hover ? FontStyle.Underline : FontStyle.Regular))
            using (var f3 = new Font("Segoe UI", 9f))
            {
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                var s1 = TextRenderer.MeasureText(g, Name1, f1, Size.Empty, flags);
                var s2 = TextRenderer.MeasureText(g, Name2, f2, Size.Empty, flags);
                int tw = Math.Max(s1.Width, s2.Width);
                int th = s1.Height + (int)(2 * k) + s2.Height;
                int top = (Height - th) / 2;
                int tx = right - tw;
                TextRenderer.DrawText(g, Name1, f1, new Point(tx, top), Ui.Text, flags);
                int y2 = top + s1.Height + (int)(2 * k);
                TextRenderer.DrawText(g, Name2, f2, new Point(tx, y2), _hover ? Ui.AccentText : Ui.Muted, flags);
                _link = new Rectangle(tx, top, tw, th);

                var logo = Logo;
                if (logo != null)
                {
                    int lh = (int)(26 * k);
                    int lw = logo.Width * lh / Math.Max(1, logo.Height);
                    var r = new Rectangle(tx - (int)(10 * k) - lw, (Height - lh) / 2, lw, lh);
                    g.DrawImage(logo, r);
                    _link = Rectangle.Union(_link, r);
                }

                string left = "© " + DateTime.Now.Year + " · Monitor Dimmer";
                var s3 = TextRenderer.MeasureText(g, left, f3, Size.Empty, flags);
                TextRenderer.DrawText(g, left, f3, new Point(pad, (Height - s3.Height) / 2), Ui.Muted, flags);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = _link.Contains(e.Location);
            if (h != _hover) { _hover = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover) { _hover = false; Cursor = Cursors.Default; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _link.Contains(e.Location))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Url) { UseShellExecute = true }); }
                catch { }
            }
            base.OnMouseClick(e);
        }
    }

    /// <summary>"Info über Monitor Dimmer": großes GS-Logo, Version, Eckdaten, Anleitung/Sicherungen.</summary>
    internal sealed class AboutDialog : Form
    {
        private static AboutDialog _open;
        private readonly float k;

        public static void ShowFor(Form owner)
        {
            if (_open != null) { _open.Activate(); return; }
            using (var d = new AboutDialog(owner))
            {
                _open = d;
                try { if (owner != null && owner.Visible) d.ShowDialog(owner); else d.ShowDialog(); }
                finally { _open = null; }
            }
        }

        internal AboutDialog(Form owner)
        {
            Theme.Refresh();
            Text = "Info über Monitor Dimmer";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            // Normaler Rahmen wie bei den Hauptfenstern (Icon mit gleichem Abstand), Größe bleibt fest
            FormBorderStyle = FormBorderStyle.Sizable;
            SizeGripStyle = SizeGripStyle.Hide;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = owner == null;
            StartPosition = owner != null && owner.Visible ? FormStartPosition.CenterParent : FormStartPosition.Manual;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            int W = S(480);

            int y = S(24);
            var logo = new PictureBox
            {
                Image = SignatureFooter.Logo,
                SizeMode = PictureBoxSizeMode.Zoom,
                Location = new Point(0, y),
                Size = new Size(W, S(130)),
                BackColor = Color.Transparent
            };
            if (logo.Image == null) logo.Image = Ui.AppIcon(128);
            Controls.Add(logo);
            y += S(146);

            Controls.Add(new Label
            {
                Text = "Monitor Dimmer",
                Font = new Font("Segoe UI Semibold", 15f),
                ForeColor = Ui.Text,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(0, y),
                Size = new Size(W, S(36))
            });
            y += S(38);
            Controls.Add(new Label
            {
                Text = "Version " + Ui.Version,
                Font = new Font("Segoe UI", 10.5f),
                ForeColor = Ui.PillText,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(0, y),
                Size = new Size(W, S(26))
            });
            y += S(40);

            // ---------- Karte mit Eckdaten ----------
            var card = new CardPanel(k) { Location = new Point(S(24), y), Size = new Size(W - S(48), S(140)) };
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Ui.Card };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(118)));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var tip = new ToolTip();
            string built;
            try { built = File.GetLastWriteTime(Application.ExecutablePath).ToString("dd.MM.yyyy"); }
            catch { built = "-"; }
            AddRow(table, tip, "Entwickler", "Günther Schoder");
            AddRow(table, tip, "Erstellt", built);
            AddRow(table, tip, "Farbschema", (Theme.Dark ? "dunkel" : "hell") + " (folgt Windows)");
            AddRow(table, tip, "Programm", Demo.Active ? @"C:\Tools\Monitor Dimmer" : Application.StartupPath); // Screenshots: kein privater Pfad
            card.Controls.Add(table);
            Controls.Add(card);
            y += card.Height + S(24);

            // ---------- Knöpfe ----------
            var manual = new ModernButton("Anleitung", MenuHelper.Document, false, k);
            manual.Click += (s, e) => Open(Path.Combine(Application.StartupPath, "Liesmich.txt"), true);
            var backups = new ModernButton("Sicherungen", MenuHelper.Folder, false, k);
            backups.Click += (s, e) =>
            {
                string dir = Path.Combine(Application.StartupPath, "Configurations", "Sicherungen");
                try { Directory.CreateDirectory(dir); } catch { }
                Open(dir, false);
            };
            var close = new ModernButton("Schließen", '\0', true, k) { DialogResult = DialogResult.Cancel };
            int gap = S(14);
            int total = manual.Width + backups.Width + close.Width + 2 * gap;
            int bx = (W - total) / 2;
            manual.Location = new Point(bx, y);
            backups.Location = new Point(manual.Right + gap, y);
            close.Location = new Point(backups.Right + gap, y);
            Controls.Add(manual);
            Controls.Add(backups);
            Controls.Add(close);
            CancelButton = close;
            AcceptButton = close;
            y += close.Height + S(22);

            Controls.Add(new Label
            {
                Text = "© " + DateTime.Now.Year + " Günther Schoder",
                ForeColor = Ui.Muted,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(0, y),
                Size = new Size(W, S(24))
            });
            y += S(24) + S(20);

            ClientSize = new Size(W, y);
            if (StartPosition == FormStartPosition.Manual) Load += (s, e) => WindowPlacement.Center(this);
            Shown += (s, e) => close.Focus();
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // WM_NCHITTEST: Rand und Ecken wie festen Rahmen behandeln -> Größe bleibt fest
            if (m.Msg == 0x0084)
            {
                int hit = m.Result.ToInt32();
                if (hit >= 10 && hit <= 17) m.Result = (IntPtr)1; // HTCLIENT: keine Größenänderung (wie in allen Programmen)
            }
        }

        private void AddRow(TableLayoutPanel t, ToolTip tip, string name, string value)
        {
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(26)));
            t.Controls.Add(new Label { Text = name, ForeColor = Ui.Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Ui.Card }, 0, row);
            var v = new Label { Text = value, ForeColor = Ui.Text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, BackColor = Ui.Card };
            tip.SetToolTip(v, value);
            t.Controls.Add(v, 1, row);
        }

        private static void Open(string path, bool notepadFallback)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch
            {
                if (notepadFallback)
                    try { System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\""); } catch { }
            }
        }
    }

    /// <summary>
    /// Windows-Akzentfarbe. Palette wie in Windows 11 (7 Abstufungen):
    /// 0 Hell3, 1 Hell2, 2 Hell1, 3 Basis, 4 Dunkel1, 5 Dunkel2, 6 Dunkel3.
    /// Helles Design: Knöpfe in Dunkel1, dunkles Design: Knöpfe in Hell2 (wie Windows 11).
    /// null = Akzentfarbe aus (Konfiguration) oder nicht lesbar -> festes Blau.
    /// </summary>
    internal static class Accent
    {
        public static bool Enabled = true;
        private static Color[] _palette;
        private static bool _loaded;

        public static void Refresh()
        {
            _loaded = false;
        }

        public static Color[] Palette
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _palette = Enabled ? Read() : null;
                }
                return _palette;
            }
        }

        private static Color[] Read()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var b = key == null ? null : key.GetValue("AccentPalette") as byte[];
                    if (b != null && b.Length >= 28)
                    {
                        var p = new Color[7];
                        for (int i = 0; i < 7; i++) p[i] = Color.FromArgb(b[i * 4], b[i * 4 + 1], b[i * 4 + 2]);
                        return p;
                    }
                }
            }
            catch { }
            try
            {
                // Ältere Windows-Versionen: nur die Grundfarbe (DWORD als ABGR)
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = key == null ? null : key.GetValue("AccentColor");
                    if (v is int)
                    {
                        int c = (int)v;
                        var baseColor = Color.FromArgb(c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF);
                        return new[]
                        {
                            Mix(Color.White, baseColor, 0.75), Mix(Color.White, baseColor, 0.50), Mix(Color.White, baseColor, 0.25),
                            baseColor,
                            Mix(Color.Black, baseColor, 0.20), Mix(Color.Black, baseColor, 0.40), Mix(Color.Black, baseColor, 0.60)
                        };
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Mischt a in b: t = 0 ergibt b, t = 1 ergibt a.</summary>
        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)Math.Round(b.R + (a.R - b.R) * t),
                (int)Math.Round(b.G + (a.G - b.G) * t),
                (int)Math.Round(b.B + (a.B - b.B) * t));
        }

        /// <summary>Relative Helligkeit 0..1 (sRGB).</summary>
        public static double Luminance(Color c)
        {
            Func<double, double> lin = v => { v /= 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); };
            return 0.2126 * lin(c.R) + 0.7152 * lin(c.G) + 0.0722 * lin(c.B);
        }
    }
}
