// Monitor Dimmer – Meldungen und Rückfragen im Programmstil (statt Windows-MessageBox)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MonitorDimmer
{
    /// <summary>
    /// Ersatz für System.Windows.Forms.MessageBox mit denselben Show-Überladungen (globale Vorgabe „Dialogfenster“,
    /// Referenz Sync Pilot ab 1.44). Weil die Klasse im Namensraum des Programms liegt, nutzt der vorhandene Code
    /// automatisch das eigene Fenster, das hell/dunkel folgt.
    /// </summary>
    internal static class MessageBox
    {
        public static DialogResult Show(string text) { return Show(null, text, "", MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(string text, string caption) { return Show(null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons) { return Show(null, text, caption, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) { return Show(null, text, caption, buttons, icon, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def) { return Show(null, text, caption, buttons, icon, def); }
        public static DialogResult Show(IWin32Window owner, string text) { return Show(owner, text, "", MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(IWin32Window owner, string text, string caption) { return Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons) { return Show(owner, text, caption, buttons, MessageBoxIcon.None, MessageBoxDefaultButton.Button1); }
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) { return Show(owner, text, caption, buttons, icon, MessageBoxDefaultButton.Button1); }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def)
        {
            Form ownerForm = ResolveOwner(owner);
            try
            {
                using (var dlg = new MsgDialog(text, caption, buttons, icon, def, ownerForm != null))
                    return ownerForm != null ? dlg.ShowDialog(ownerForm) : dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                // Rückfall (z. B. sehr früh beim Start): Windows-Fenster
                Log.Error("Eigenes Meldungsfenster nicht möglich: {0}", ex.Message);
                return System.Windows.Forms.MessageBox.Show(text, string.IsNullOrEmpty(caption) ? "Monitor Dimmer" : caption, buttons, icon, def);
            }
        }

        /// <summary>Owner: das übergebene Fenster (bei eingebetteten Seiten das Hauptfenster), sonst das aktive Programmfenster.</summary>
        private static Form ResolveOwner(IWin32Window owner)
        {
            var c = owner as Control;
            if (c != null && !c.IsDisposed)
            {
                var top = c.TopLevelControl as Form;
                if (top != null && top.Visible) return top;
            }
            var active = Form.ActiveForm;
            if (active != null && !active.InvokeRequired && active.Visible) return active;
            return null;
        }
    }

    /// <summary>Meldungsfenster im Programmstil: Symbol im Kreis, Text, Schaltflächen rechts unten.</summary>
    internal sealed class MsgDialog : Form
    {
        private readonly float k;
        private readonly MessageBoxIcon _icon;
        private readonly CheckBox _check;

        /// <summary>Optionales Kästchen (z. B. „Nicht mehr fragen“); null = keins.</summary>
        public bool Checked { get { return _check != null && _check.Checked; } }

        public MsgDialog(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def, bool hasOwner, string checkText = null)
        {
            _icon = icon;
            Theme.Refresh();
            Text = "Monitor Dimmer"; // Fenstertitel = Programmname
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            StartPosition = hasOwner ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            SizeGripStyle = SizeGripStyle.Hide;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = !hasOwner;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;

            int pad = S(24), circle = S(32), gap = S(16);
            int textLeft = pad + (icon != MessageBoxIcon.None ? circle + gap : 0);

            // Überschrift nur, wenn der Titel mehr sagt als der Programmname
            string head = caption ?? "";
            if (head.StartsWith("Monitor Dimmer", StringComparison.OrdinalIgnoreCase)) head = "";
            text = (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");

            // Breite nach Textlänge: mindestens 420, höchstens 600 × Skalierung
            int minW = S(420), maxW = S(600);
            int maxText = maxW - textLeft - pad;
            Size need;
            using (var f = new Font("Segoe UI", 9.5f))
                need = TextRenderer.MeasureText(text, f, new Size(maxText, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            int width = Math.Max(minW, Math.Min(maxW, textLeft + need.Width + pad + S(4)));
            int textW = width - textLeft - pad;

            int y = pad;
            if (head.Length > 0)
            {
                var h = new Label
                {
                    Text = head, AutoSize = true, MaximumSize = new Size(textW, 0), UseMnemonic = false,
                    Font = new Font("Segoe UI Semibold", 11f), ForeColor = Ui.Text, BackColor = Ui.Back, Location = new Point(textLeft, y)
                };
                Controls.Add(h);
                y += h.GetPreferredSize(new Size(textW, 0)).Height + S(8);
            }
            var body = new Label
            {
                Text = text, AutoSize = true, MaximumSize = new Size(textW, 0), UseMnemonic = false,
                ForeColor = Ui.Text, BackColor = Ui.Back, Location = new Point(textLeft, y)
            };
            Controls.Add(body);
            y += Math.Max(body.GetPreferredSize(new Size(textW, 0)).Height, head.Length > 0 ? 0 : circle) + S(4);
            if (icon != MessageBoxIcon.None && y < pad + circle) y = pad + circle;

            if (!string.IsNullOrEmpty(checkText))
            {
                _check = new CheckBox { Text = checkText, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Back, Location = new Point(textLeft, y + S(12)) };
                Controls.Add(_check);
                y = _check.Bottom;
            }

            // Schaltflächen rechtsbündig, Reihenfolge wie bei Windows, Mindestbreite 110, Abstand 8
            var defs = new List<KeyValuePair<string, DialogResult>>();
            switch (buttons)
            {
                case MessageBoxButtons.OKCancel: defs.Add(Pair("OK", DialogResult.OK)); defs.Add(Pair("Abbrechen", DialogResult.Cancel)); break;
                case MessageBoxButtons.YesNo: defs.Add(Pair("Ja", DialogResult.Yes)); defs.Add(Pair("Nein", DialogResult.No)); break;
                case MessageBoxButtons.YesNoCancel: defs.Add(Pair("Ja", DialogResult.Yes)); defs.Add(Pair("Nein", DialogResult.No)); defs.Add(Pair("Abbrechen", DialogResult.Cancel)); break;
                case MessageBoxButtons.RetryCancel: defs.Add(Pair("Wiederholen", DialogResult.Retry)); defs.Add(Pair("Abbrechen", DialogResult.Cancel)); break;
                case MessageBoxButtons.AbortRetryIgnore: defs.Add(Pair("Abbrechen", DialogResult.Abort)); defs.Add(Pair("Wiederholen", DialogResult.Retry)); defs.Add(Pair("Ignorieren", DialogResult.Ignore)); break;
                default: defs.Add(Pair("OK", DialogResult.OK)); break;
            }
            int defIndex = def == MessageBoxDefaultButton.Button2 ? 1 : def == MessageBoxDefaultButton.Button3 ? 2 : 0;
            if (defIndex >= defs.Count) defIndex = 0;

            var made = new List<ModernButton>();
            for (int i = 0; i < defs.Count; i++)
            {
                var b = new ModernButton(defs[i].Key, '\0', i == defIndex, k) { DialogResult = defs[i].Value };
                b.Width = Math.Max(b.Width, S(110));
                b.Click += (s, e) => _clicked = true;
                made.Add(b);
            }
            int by = y + S(24);
            int bx = width - pad;
            for (int i = made.Count - 1; i >= 0; i--)
            {
                bx -= made[i].Width;
                made[i].Location = new Point(bx, by);
                bx -= S(8);
                Controls.Add(made[i]);
            }
            AcceptButton = made[defIndex];
            // Esc bzw. X: bei Ja/Nein wie „Nein“, bei OK wie „OK“, sonst wie „Abbrechen“
            DialogResult escape = buttons == MessageBoxButtons.YesNo ? DialogResult.No
                : buttons == MessageBoxButtons.OK ? DialogResult.OK : DialogResult.Cancel;
            foreach (var b in made) if (b.DialogResult == escape) CancelButton = b;
            _escape = escape;

            ClientSize = new Size(width, by + made[0].Height + pad);
            Shown += (s, e) => made[defIndex].Focus();
            Theme.Apply(this);
        }

        private readonly DialogResult _escape;
        private bool _clicked; // eine Schaltfläche (oder Esc/Enter) hat das Fenster geschlossen

        private static KeyValuePair<string, DialogResult> Pair(string t, DialogResult r) { return new KeyValuePair<string, DialogResult>(t, r); }

        private int S(float v) { return (int)Math.Round(v * k); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // X ohne Knopf: Ergebnis wie Esc
            if (!_clicked) DialogResult = _escape;
            base.OnFormClosing(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_icon == MessageBoxIcon.None) return;
            Color fill;
            string glyph;
            switch (_icon)
            {
                case MessageBoxIcon.Question: fill = Ui.Primary; glyph = "?"; break;
                case MessageBoxIcon.Warning: fill = Color.FromArgb(247, 99, 12); glyph = "!"; break; // Warnfarbe orange
                case MessageBoxIcon.Error: fill = Color.FromArgb(196, 43, 28); glyph = "×"; break;     // Fehlerfarbe rot
                default: fill = Ui.Primary; glyph = "i"; break;                                       // Information
            }
            int d = S(32), x = S(24), y = S(24);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, x, y, d, d);
            using (var f = new Font("Segoe UI", 13f, FontStyle.Bold))
                TextRenderer.DrawText(g, glyph, f, new Rectangle(x, y - S(1), d, d), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        /// <summary>Feste Größe mit normalem Rahmen (globale Vorgabe Fensterrahmen).</summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            MinimumSize = Size;
            MaximumSize = Size;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x0084)
            {
                int hit = m.Result.ToInt32();
                if (hit >= 10 && hit <= 17) m.Result = (IntPtr)1; // HTCLIENT: keine Größenänderung
            }
        }
    }
}
