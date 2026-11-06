// Monitor Dimmer – Listen und Auswahllisten im Windows-11-Stil
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MonitorDimmer
{
    /// <summary>
    /// ListView mit doppelter Pufferung und Markierung im Windows-11-Stil:
    /// dezente, abgerundete Fläche mit Akzent-Balken links statt des kräftig blauen Balkens mit weißer Schrift.
    /// Die Schriftfarben (z. B. rot/grün für den Status) bleiben auch in der markierten Zeile erhalten.
    /// </summary>
    internal sealed class BufferedListView : ListView
    {
        public BufferedListView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            OwnerDraw = true;
        }

        private float _k;

        private float K
        {
            get
            {
                if (_k <= 0)
                {
                    try { using (var g = CreateGraphics()) _k = g.DpiX / 96f; }
                    catch { _k = 1f; }
                }
                return _k;
            }
        }

        private static Color SelectedBack { get { return Theme.Dark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(240, 240, 240); } }

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>
        /// Das dunkle Windows-Thema (DarkMode_Explorer) zeichnet in der Detailansicht senkrechte Spaltenlinien in die
        /// leere Fläche unter den Einträgen. Nach dem Zeichnen wird diese Fläche in der Listenfarbe übermalt.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x000F /* WM_PAINT */ || !Theme.Dark || View != View.Details || !IsHandleCreated) return;
            int top = 0;
            if (Items.Count > 0)
            {
                try { top = Items[Items.Count - 1].Bounds.Bottom; } catch { return; }
            }
            else if (HeaderStyle != ColumnHeaderStyle.None)
            {
                IntPtr header = SendMessage(Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
                RECT r;
                if (header != IntPtr.Zero && GetWindowRect(header, out r)) top = r.Bottom - r.Top;
            }
            if (top < 0) top = 0;
            if (top >= ClientSize.Height) return;
            using (var g = Graphics.FromHwnd(Handle))
            using (var b = new SolidBrush(BackColor))
                g.FillRectangle(b, 0, top, ClientSize.Width, ClientSize.Height - top);
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Ui.Card)) g.FillRectangle(b, e.Bounds);
            var r = new Rectangle(e.Bounds.X + (int)(6 * K), e.Bounds.Y, e.Bounds.Width - (int)(10 * K), e.Bounds.Height);
            TextRenderer.DrawText(g, e.Header.Text, Font, r, Ui.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            using (var p = new Pen(Ui.Border))
            {
                g.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Y + (int)(6 * K), e.Bounds.Right - 1, e.Bounds.Bottom - (int)(6 * K));
                g.DrawLine(p, e.Bounds.X, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            }
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            // Nichts zeichnen: Windows ruft dies auch beim Überfahren mit der Maus auf, ohne danach die
            // Spalten neu zu zeichnen - ein Hintergrund hier würde Programm, Status und Pfad überdecken.
            // Hintergrund und Markierung zeichnet OnDrawSubItem je Spalte.
        }

        /// <summary>Markierung der ganzen Zeile (abgerundet), jeweils nur im Bereich der gerade gezeichneten Spalte.</summary>
        private void DrawRowBackground(Graphics g, ListViewItem item, Rectangle cell, bool firstColumn)
        {
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, cell);
            if (!item.Selected) return;
            var row = item.Bounds;
            float k = K;
            var r = new RectangleF(row.X + 2 * k, row.Y + 1 * k, Math.Max(row.Width, ClientSize.Width) - 4 * k - 1, row.Height - 2 * k);
            r.Width = Math.Min(r.Width, ClientSize.Width - r.X - 2 * k);
            var oldClip = g.Clip;
            g.SetClip(cell);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = MonitorForm.Rounded(r, 4 * k))
            using (var b = new SolidBrush(SelectedBack))
                g.FillPath(b, path);
            if (firstColumn)
            {
                // Akzent-Balken links wie in Windows 11
                float bh = Math.Min(16 * k, r.Height - 6 * k), bw = 3 * k;
                var bar = new RectangleF(r.X + 1 * k, r.Y + (r.Height - bh) / 2, bw, bh);
                using (var path = MonitorForm.Rounded(bar, bw / 2))
                using (var b = new SolidBrush(Ui.Primary))
                    g.FillPath(b, path);
            }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
            g.Clip = oldClip;
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            var g = e.Graphics;
            var item = e.Item;
            var sub = e.SubItem;
            var cell = e.Bounds;
            // letzte Spalte: Hintergrund bis zum rechten Rand der Liste
            if (e.ColumnIndex == Columns.Count - 1) cell.Width = Math.Max(cell.Width, ClientSize.Width - cell.X);
            DrawRowBackground(g, item, cell, e.ColumnIndex == 0);
            bool own = item.UseItemStyleForSubItems || e.ColumnIndex == 0;
            Color color = own ? item.ForeColor : sub.ForeColor;
            Font font = (own ? item.Font : sub.Font) ?? Font;
            int x = e.Bounds.X + (int)(6 * K);
            if (e.ColumnIndex == 0)
            {
                x += (int)(4 * K); // Platz für den Akzent-Balken
                Image img = null;
                var il = SmallImageList;
                if (il != null)
                {
                    if (!string.IsNullOrEmpty(item.ImageKey) && il.Images.ContainsKey(item.ImageKey)) img = il.Images[item.ImageKey];
                    else if (item.ImageIndex >= 0 && item.ImageIndex < il.Images.Count) img = il.Images[item.ImageIndex];
                    if (img != null)
                    {
                        g.DrawImage(img, x, e.Bounds.Y + (e.Bounds.Height - il.ImageSize.Height) / 2, il.ImageSize.Width, il.ImageSize.Height);
                    }
                    x += il.ImageSize.Width + (int)(4 * K);
                }
            }
            var r = new Rectangle(x, e.Bounds.Y, Math.Max(0, e.Bounds.Right - x - (int)(4 * K)), e.Bounds.Height);
            TextRenderer.DrawText(g, sub.Text, font, r, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Spaltenbreiten nach Inhalt berechnen, ohne die Spalten dafür umzustellen.</summary>
    internal static class ListViewHelper
    {
        private static readonly Dictionary<ListView, int[]> Measured = new Dictionary<ListView, int[]>();
        private static readonly Dictionary<ListView, int> FillColumn = new Dictionary<ListView, int>();

        /// <summary>Misst Überschriften und Inhalte; setzt die Breiten nur, wenn sie sich ändern. Liefert die gemessenen Breiten.</summary>
        /// <param name="fill">Spalte, die den restlichen Platz bekommt (-1 = letzte Spalte).</param>
        public static int[] Fit(ListView lv, int padding, int fill = -1)
        {
            if (fill >= 0) FillColumn[lv] = fill; else FillColumn.Remove(lv);
            int n = lv.Columns.Count;
            var widths = new int[n];
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            using (var bold = new Font(lv.Font, FontStyle.Bold))
            {
                for (int i = 0; i < n; i++)
                    widths[i] = TextRenderer.MeasureText(lv.Columns[i].Text, lv.Font, Size.Empty, flags).Width;
                foreach (ListViewItem item in lv.Items)
                {
                    for (int i = 0; i < n && i < item.SubItems.Count; i++)
                    {
                        var sub = item.SubItems[i];
                        Font f = i == 0 ? (item.Font ?? lv.Font) : (item.UseItemStyleForSubItems ? (item.Font ?? lv.Font) : (sub.Font ?? lv.Font));
                        int w = TextRenderer.MeasureText(sub.Text ?? "", f, Size.Empty, flags).Width;
                        if (i == 0 && lv.SmallImageList != null) w += lv.SmallImageList.ImageSize.Width + 4;
                        if (i == 0) w += padding / 2; // Platz für den Akzent-Balken der Markierung
                        if (w > widths[i]) widths[i] = w;
                    }
                }
            }
            for (int i = 0; i < n; i++) widths[i] += padding;
            Measured[lv] = widths;
            lv.Disposed -= OnDisposed;
            lv.Disposed += OnDisposed;

            lv.BeginUpdate();
            for (int i = 0; i < n - 1; i++)
                if (lv.Columns[i].Width != widths[i]) lv.Columns[i].Width = widths[i];
            lv.EndUpdate();
            Stretch(lv);
            return widths;
        }

        /// <summary>Letzte Spalte füllt den Rest; ist ihr Inhalt breiter, bekommt sie ihre volle Breite (dann scrollt die Liste).</summary>
        public static void Stretch(ListView lv)
        {
            int[] widths;
            if (!Measured.TryGetValue(lv, out widths) || widths.Length == 0) return;
            int fill;
            if (FillColumn.TryGetValue(lv, out fill) && fill >= 0 && fill < widths.Length)
            {
                // Alle anderen Spalten behalten ihre gemessene Breite, nur "fill" wächst mit dem Fenster
                int rest = 0;
                for (int i = 0; i < widths.Length; i++)
                {
                    if (i == fill) continue;
                    if (lv.Columns[i].Width != widths[i]) lv.Columns[i].Width = widths[i];
                    rest += widths[i];
                }
                int w = Math.Max(widths[fill], lv.ClientSize.Width - rest - 1);
                if (lv.Columns[fill].Width != w) lv.Columns[fill].Width = w;
                return;
            }
            int last = widths.Length - 1;
            int others = 0;
            for (int i = 0; i < last; i++) others += lv.Columns[i].Width;
            int avail = lv.ClientSize.Width - others;
            if (widths[last] > avail)
            {
                if (lv.Columns[last].Width != widths[last]) lv.Columns[last].Width = widths[last];
            }
            else
            {
                lv.Columns[last].Width = -2; // LVSCW_AUTOSIZE_USEHEADER: füllt den restlichen Platz
            }
        }

        private static void OnDisposed(object sender, EventArgs e)
        {
            Measured.Remove((ListView)sender);
            FillColumn.Remove((ListView)sender);
        }
    }

    /// <summary>
    /// Auswahllisten (ComboBox): Text mit Abstand zum linken Rand wie bei Windows 11, Markierung in der
    /// aufgeklappten Liste dezent grau. Feste Listen werden selbst gezeichnet, bei eingebbaren Listen wird
    /// der linke Rand des Eingabefelds vergrößert.
    /// </summary>
    internal static class ComboStyle
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        public static void ApplyAll(Control root)
        {
            foreach (Control c in root.Controls)
            {
                var cb = c as ComboBox;
                if (cb != null) Apply(cb);
                ApplyAll(c);
            }
        }

        private static readonly HashSet<ComboBox> Styled = new HashSet<ComboBox>();

        public static void Apply(ComboBox cb)
        {
            if (Styled.Contains(cb)) { cb.BackColor = ModernCombo.FieldBack(false); cb.Invalidate(); return; }
            Styled.Add(cb);
            cb.Disposed += (s, e) => Styled.Remove(cb);
            cb.BackColor = ModernCombo.FieldBack(false);
            ModernCombo.Attach(cb);
            if (cb.DropDownStyle == ComboBoxStyle.DropDownList)
            {
                cb.DrawMode = DrawMode.OwnerDrawFixed;
                // Windows-11-Höhe: 32 Einheiten (Feldhöhe = ItemHeight + 6)
                cb.ItemHeight = Math.Max(cb.ItemHeight, (int)Math.Round(32 * K(cb)) - 6);
                cb.DrawItem += DrawItem;
            }
            else
            {
                // Eingabefeld: linken Innenrand setzen (EM_SETMARGINS, EC_LEFTMARGIN)
                Action set = () =>
                {
                    IntPtr edit = FindWindowEx(cb.Handle, IntPtr.Zero, "Edit", null);
                    if (edit != IntPtr.Zero) SendMessage(edit, 0xD3, (IntPtr)1, (IntPtr)(int)Math.Round(8 * K(cb)));
                };
                if (cb.IsHandleCreated) set();
                cb.HandleCreated += (s, e) => set();
            }
        }

        internal static float K(Control c)
        {
            try { using (var g = c.CreateGraphics()) return g.DpiX / 96f; }
            catch { return 1f; }
        }

        private static int Pad(Control c)
        {
            try { using (var g = c.CreateGraphics()) return (int)Math.Round(6 * g.DpiX / 96f); }
            catch { return 6; }
        }

        private static void DrawItem(object sender, DrawItemEventArgs e)
        {
            var cb = (ComboBox)sender;
            var g = e.Graphics;
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color back = cb.BackColor;
            if (selected && !edit) back = Theme.Dark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(237, 237, 237);
            using (var b = new SolidBrush(back)) g.FillRectangle(b, e.Bounds);
            if (e.Index < 0) return;
            string text = cb.GetItemText(cb.Items[e.Index]);
            Color fore = cb.Enabled ? cb.ForeColor : Theme.Map(Color.FromArgb(157, 157, 157));
            int pad = Pad(cb);
            var r = new Rectangle(e.Bounds.X + pad, e.Bounds.Y, Math.Max(0, e.Bounds.Width - pad - 2), e.Bounds.Height);
            TextRenderer.DrawText(g, text, e.Font ?? cb.Font, r, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// Zeichnet Auswahl- und Eingabelisten (ComboBox) im Windows-11-Stil: abgerundetes Feld mit feinem Rahmen,
    /// Chevron-Symbol rechts, Akzent-Linie unten bei Fokus, abgerundete Aufklappliste.
    /// </summary>
    internal sealed class ModernCombo : NativeWindow
    {
        private const int WM_PAINT = 0x000F, WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008, WM_ENABLE = 0x000A,
            WM_MOUSEMOVE = 0x0200, WM_MOUSELEAVE = 0x02A3, CB_GETCOMBOBOXINFO = 0x0164;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct COMBOBOXINFO
        {
            public int cbSize;
            public RECT rcItem, rcButton;
            public int stateButton;
            public IntPtr hwndCombo, hwndItem, hwndList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public RECT rcPaint;
            public bool fRestore, fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT { public int cbSize, dwFlags; public IntPtr hwndTrack; public int dwHoverTime; }

        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")] private static extern bool GetComboBoxInfo(IntPtr hwnd, ref COMBOBOXINFO info);
        [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private readonly ComboBox _cb;
        private bool _hover;

        private ModernCombo(ComboBox cb)
        {
            _cb = cb;
            if (cb.IsHandleCreated) AssignHandle(cb.Handle);
            cb.HandleCreated += (s, e) => AssignHandle(cb.Handle);
            cb.HandleDestroyed += (s, e) => ReleaseHandle();
            cb.DropDown += (s, e) => RoundList();
            cb.DropDownClosed += (s, e) => cb.Invalidate();
            cb.EnabledChanged += (s, e) => cb.Invalidate();
        }

        public static void Attach(ComboBox cb) { new ModernCombo(cb); }

        /// <summary>Hintergrund des Feldes (Windows 11: hell weiß, dunkel leicht aufgehellt).</summary>
        public static Color FieldBack(bool hover)
        {
            if (Theme.Dark) return hover ? Color.FromArgb(56, 56, 56) : Color.FromArgb(50, 50, 50);
            return hover ? Color.FromArgb(249, 249, 249) : Color.White;
        }

        private bool IsList { get { return _cb.DropDownStyle == ComboBoxStyle.DropDownList; } }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_PAINT:
                    if (IsList)
                    {
                        // Auswahlliste ganz selbst zeichnen (ohne Flackern)
                        var ps = new PAINTSTRUCT();
                        IntPtr hdc = BeginPaint(m.HWnd, ref ps);
                        try { using (var g = Graphics.FromHdc(hdc)) DrawBuffered(g); }
                        finally { EndPaint(m.HWnd, ref ps); }
                        return;
                    }
                    base.WndProc(ref m);
                    // Eingabeliste: Windows zeichnet das Eingabefeld, wir Rahmen und Pfeil darum
                    using (var g = Graphics.FromHwnd(m.HWnd))
                    {
                        var info = Info();
                        if (info.hwndItem != IntPtr.Zero)
                        {
                            var r = info.rcItem;
                            g.ExcludeClip(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
                        }
                        Draw(g, false);
                    }
                    return;
                case WM_SETFOCUS:
                case WM_KILLFOCUS:
                case WM_ENABLE:
                    base.WndProc(ref m);
                    _cb.Invalidate();
                    return;
                case WM_MOUSEMOVE:
                    base.WndProc(ref m);
                    if (!_hover)
                    {
                        _hover = true;
                        var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT)), dwFlags = 2, hwndTrack = m.HWnd };
                        TrackMouseEvent(ref tme);
                        _cb.Invalidate();
                    }
                    return;
                case WM_MOUSELEAVE:
                    base.WndProc(ref m);
                    _hover = false;
                    _cb.Invalidate();
                    return;
            }
            base.WndProc(ref m);
        }

        private COMBOBOXINFO Info()
        {
            var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf(typeof(COMBOBOXINFO)) };
            try { GetComboBoxInfo(_cb.Handle, ref info); } catch { }
            return info;
        }

        /// <summary>Aufklappliste mit runden Ecken (Windows 11).</summary>
        private void RoundList()
        {
            try
            {
                var info = Info();
                if (info.hwndList == IntPtr.Zero) return;
                int pref = 3; // DWMWCP_ROUNDSMALL
                DwmSetWindowAttribute(info.hwndList, 33, ref pref, sizeof(int));
            }
            catch { }
        }

        private void DrawBuffered(Graphics target)
        {
            var size = _cb.ClientSize;
            if (size.Width <= 0 || size.Height <= 0) return;
            using (var bmp = new Bitmap(size.Width, size.Height))
            {
                using (var g = Graphics.FromImage(bmp)) Draw(g, true);
                target.DrawImageUnscaled(bmp, 0, 0);
            }
        }

        private void Draw(Graphics g, bool withText)
        {
            float k = g.DpiX / 96f;
            var size = _cb.ClientSize;
            bool focus = _cb.Focused || _cb.DroppedDown;
            bool hover = _hover && _cb.Enabled && IsList;
            Color parentBack = _cb.Parent != null ? _cb.Parent.BackColor : Ui.Card;
            Color back = FieldBack(hover);
            Color border = Theme.Dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(229, 229, 229);
            Color bottom = Theme.Dark ? Color.FromArgb(100, 100, 100) : Color.FromArgb(200, 200, 200);

            // Ecken außerhalb der Rundung in der Farbe des Untergrunds
            using (var b = new SolidBrush(parentBack)) g.FillRectangle(b, 0, 0, size.Width, size.Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(0.5f, 0.5f, size.Width - 1f, size.Height - 1f);
            float radius = 4 * k;
            using (var path = MonitorForm.Rounded(rect, radius))
            {
                using (var b = new SolidBrush(back)) g.FillPath(b, path);
                using (var p = new Pen(border)) g.DrawPath(p, path);

                // Linie unten: dunkler, bei Fokus 2 Einheiten in Akzentfarbe (nur innerhalb der Rundung)
                var oldClip = g.Clip;
                g.SetClip(path);
                float lh = focus ? Math.Max(2f, 2 * k) : 1f;
                using (var b = new SolidBrush(focus ? Ui.Primary : bottom))
                    g.FillRectangle(b, 0, size.Height - lh, size.Width, lh);
                g.Clip = oldClip;
            }
            g.SmoothingMode = SmoothingMode.Default;

            // Chevron rechts
            int btn = (int)(32 * k);
            var arrowRect = new Rectangle(size.Width - btn, 0, btn, size.Height);
            Color muted = _cb.Enabled ? Ui.Muted : Theme.Map(Color.FromArgb(157, 157, 157));
            string iconFont = MenuHelper.IconFont();
            if (iconFont.Length > 0)
            {
                using (var f = new Font(iconFont, 10 * k, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(g, "\uE70D", f, arrowRect, muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }

            if (!withText) return;
            int pad = (int)(11 * k);
            var textRect = new Rectangle(pad, 0, Math.Max(0, size.Width - pad - btn), size.Height);
            Color fore = _cb.Enabled ? _cb.ForeColor : muted;
            TextRenderer.DrawText(g, _cb.Text, _cb.Font, textRect, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// Knopfleiste unter einer Liste: Reicht die Breite nicht (z. B. als Seite im Hauptfenster neben "Speichern"/"Abbrechen"),
    /// werden die Knöpfe links von hinten nach vorn zu reinen Symbolknöpfen (Text als Tooltip). Der erste Knopf behält
    /// seinen Text. So überdeckt rechts nichts die Knöpfe links.
    /// </summary>
    internal static class ButtonRow
    {
        public static void Fit(Control row, FlowLayoutPanel left, Control right)
        {
            var tip = new ToolTip();
            var texts = new Dictionary<ModernButton, string>();
            foreach (var b in left.Controls.OfType<ModernButton>())
                if (!string.IsNullOrEmpty(b.Text)) texts[b] = b.Text;
            bool busy = false;
            EventHandler fit = (s, e) =>
            {
                if (busy) return;
                busy = true;
                try
                {
                    var buttons = left.Controls.OfType<ModernButton>().Where(b => texts.ContainsKey(b)).ToList();
                    foreach (var b in buttons)
                        if (b.Text != texts[b]) { b.Text = texts[b]; tip.SetToolTip(b, null); }
                    int avail = row.ClientSize.Width - right.Width - left.Padding.Horizontal;
                    for (int i = buttons.Count - 1; i >= 1 && Needed(left) > avail; i--)
                    {
                        var b = buttons[i];
                        b.Text = "";
                        tip.SetToolTip(b, texts[b]);
                    }
                }
                finally { busy = false; }
            };
            row.Resize += fit;
            right.Resize += fit;
            fit(row, EventArgs.Empty);
        }

        private static int Needed(FlowLayoutPanel left)
        {
            int w = 0;
            foreach (Control c in left.Controls) if (c.Visible) w += c.Width + c.Margin.Horizontal;
            return w;
        }
    }
}
