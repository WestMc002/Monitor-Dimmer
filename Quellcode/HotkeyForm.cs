// Monitor Dimmer – Fenster „Tastenkürzel“
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MonitorDimmer
{
    /// <summary>Beschreibung der verfügbaren Hotkey-Aktionen.</summary>
    internal static class HotkeyActions
    {
        public sealed class Info
        {
            public string Id;
            public string Title;
            public bool NeedsProfile;
            public string Help;
            public override string ToString() { return Title; }
        }

        public static readonly List<Info> All = new List<Info>
        {
            new Info { Id = "TogglePause", Title = "Umschalten: Deaktiviert \u2194 Automatik",
                Help = "1. Druck: Monitore ungedimmt, die Spielerkennung pausiert.\r\n2. Druck: zurück zur Automatik." },
            new Info { Id = "EnforceProfile", Title = "Umschalten: Profil \u2194 Automatik", NeedsProfile = true,
                Help = "1. Druck: das gewählte Profil wird eingeschaltet, die Spielerkennung pausiert.\r\n2. Druck: zurück zur Automatik." },
            new Info { Id = "SetProfile", Title = "Profil erzwingen", NeedsProfile = true,
                Help = "Schaltet das gewählte Profil ein. Weitere Drücke ändern nichts.\r\nZurück über \u201eAutomatik einschalten\u201c oder das Menü." },
            new Info { Id = "Automatic", Title = "Automatik einschalten",
                Help = "Schaltet die Automatik ein: Das Profil richtet sich nach dem Spiel im Vordergrund." },
            new Info { Id = "AddForegroundProcess", Title = "Aktives Spiel zur Liste hinzufügen", NeedsProfile = true,
                Help = "Nimmt das zuletzt aktive Programm (z. B. das laufende Spiel) in die Spieleliste des gewählten Profils auf." },
            new Info { Id = "Reapply", Title = "Profil erneut anwenden",
                Help = "Sendet Helligkeit und Kontrast des aktuellen Profils noch einmal an die Monitore,\r\nz. B. wenn ein Monitor seine Werte verloren hat." },
            new Info { Id = "ShowMonitors", Title = "Monitore anzeigen",
                Help = "Öffnet die Monitor-Übersicht und blendet auf jedem Monitor seine Nummer ein." },
            new Info { Id = "ShowGameList", Title = "Spieleliste öffnen",
                Help = "Öffnet das Fenster \u201eSpieleliste\u201c." }
        };

        public static Info Find(string id)
        {
            return All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // ---------- Tasten: Speicherform "Ctrl+Alt+F10", Anzeige "Strg+Alt+F10" ----------

        public static string Display(string keys)
        {
            if (string.IsNullOrWhiteSpace(keys)) return "";
            var parts = keys.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).Select(p =>
            {
                string l = p.ToLowerInvariant();
                if (l == "ctrl" || l == "control" || l == "strg") return "Strg";
                if (l == "shift" || l == "umschalt") return "Umschalt";
                if (l == "alt") return "Alt";
                if (l == "win") return "Win";
                return KeyName(p);
            });
            return string.Join(" + ", parts);
        }

        public static string KeyName(string key)
        {
            if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1])) return key.Substring(1);
            switch (key.ToLowerInvariant())
            {
                case "next": return "Bild ab";
                case "pagedown": return "Bild ab";
                case "prior": return "Bild auf";
                case "pageup": return "Bild auf";
                case "insert": return "Einfg";
                case "delete": return "Entf";
                case "home": return "Pos1";
                case "end": return "Ende";
                case "pause": return "Pause";
                case "scroll": return "Rollen";
                case "space": return "Leertaste";
            }
            if (key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase)) return "Num " + key.Substring(6);
            return key;
        }

        /// <summary>Normalisiert für den Vergleich (Reihenfolge der Modifikatoren egal).</summary>
        public static string Normalize(string keys)
        {
            if (string.IsNullOrWhiteSpace(keys)) return "";
            var parts = keys.Split('+').Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0).Select(p =>
                p == "strg" || p == "control" ? "ctrl" : p == "umschalt" ? "shift" : p).ToList();
            var mods = new[] { "ctrl", "alt", "shift", "win" }.Where(parts.Contains);
            var rest = parts.Where(p => p != "ctrl" && p != "alt" && p != "shift" && p != "win");
            return string.Join("+", mods.Concat(rest));
        }

        public static bool HasModifier(string keys)
        {
            string n = Normalize(keys);
            return n.Contains("ctrl+") || n.Contains("alt+") || n.Contains("win+") || n.StartsWith("ctrl") || n.StartsWith("alt") || n.StartsWith("win");
        }
    }

    /// <summary>Fenster zum Anzeigen und Ändern der Tastenkürzel.</summary>
    internal sealed class HotkeyForm : Form
    {
        private static Color CBack { get { return Theme.Map(Color.FromArgb(243, 243, 243)); } }
        private static Color CText { get { return Theme.Map(Color.FromArgb(27, 27, 27)); } }
        private static Color CMuted { get { return Theme.Map(Color.FromArgb(97, 97, 97)); } }
        private static Color COk { get { return Theme.Map(Color.FromArgb(15, 123, 15)); } }
        private static Color CBad { get { return Theme.Map(Color.FromArgb(196, 43, 28)); } }

        private readonly ListView _list;
        private readonly List<HotkeyDef> _items;
        private readonly AppConfig _config;
        private readonly Func<string, string> _status;
        private readonly ModernButton _edit, _remove, _up, _down;
        private readonly Label _count;
        private readonly Label _error;
        private int _sortColumn = -1;
        private bool _sortDescending;
        private float k = 1f;

        public List<HotkeyDef> Result { get { return _items; } }

        public HotkeyForm(AppConfig config, Func<string, string> status, Icon icon)
        {
            _config = config;
            _status = status;
            _items = config.Hotkeys.Select(h => new HotkeyDef { Keys = h.Keys, Action = h.Action, Profile = h.Profile }).ToList();

            Text = "Monitor Dimmer - Tastenkürzel";
            if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.Manual;
            BackColor = CBack;
            Font = new Font("Segoe UI", 9f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            ClientSize = new Size(S(980), S(680));
            MinimumSize = new Size(S(640), S(320));

            var head = new HeaderPanel("Tastenkürzel", "Gelten systemweit, auch im Spiel", k);

            _list = new BufferedListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                BackColor = Ui.Card,
                Font = new Font("Segoe UI", 9.5f)
            };
            _list.Columns.Add("Tastenkürzel", S(190));
            _list.Columns.Add("Aktion", S(300));
            _list.Columns.Add("Profil", S(200));
            _list.Columns.Add("Status", S(130));
            _list.DoubleClick += (s, e) => EditSelected();
            _list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
            _list.ShowItemToolTips = true;
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            _list.ColumnClick += (s, e) => SortBy(e.Column);
            _list.Resize += (s, e) => StretchLastColumn();

            var body = new Panel { Dock = DockStyle.Fill, BackColor = CBack, Padding = new Padding(S(28), 0, S(28), 0) };
            var card = new CardPanel(k) { Dock = DockStyle.Fill };
            var cardHead = new Panel { Dock = DockStyle.Top, Height = S(46), BackColor = Ui.Card };
            var cardTitle = new Label { Text = "Kürzel", Font = new Font("Segoe UI Semibold", 11f), ForeColor = CText, AutoSize = true, Location = new Point(0, S(8)), BackColor = Color.Transparent };
            _count = new Label { ForeColor = CMuted, AutoSize = true, Font = new Font("Segoe UI", 10f), Location = new Point(S(80), S(10)), BackColor = Color.Transparent };
            cardHead.Controls.Add(cardTitle);
            cardHead.Controls.Add(_count);
            cardHead.Layout += (s, e) => _count.Left = cardTitle.Right + S(16);
            card.Controls.Add(_list);
            card.Controls.Add(cardHead);

            var actions = new Panel { Dock = DockStyle.Bottom, Height = S(72), BackColor = CBack };
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = CBack, Padding = new Padding(0, S(16), 0, 0) };
            var add = new ModernButton("Hinzufügen", '\uE710', false, k);
            add.Click += (s, e) => AddNew();
            _edit = new ModernButton("Bearbeiten", '\uE70F', false, k);
            _edit.Click += (s, e) => EditSelected();
            _remove = new ModernButton("Entfernen", '\uE74D', false, k);
            _remove.Click += (s, e) => RemoveSelected();
            _up = new ModernButton("", '\uE70E', false, k);
            _up.Click += (s, e) => MoveSelected(-1);
            _down = new ModernButton("", '\uE70D', false, k);
            _down.Click += (s, e) => MoveSelected(1);
            var tip = new ToolTip();
            tip.SetToolTip(_up, "Nach oben schieben");
            tip.SetToolTip(_down, "Nach unten schieben");
            left.Controls.AddRange(new Control[] { add, _edit, _remove, _up, _down });

            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, BackColor = CBack, Padding = new Padding(0, S(16), 0, 0) };
            // „Abbrechen“ links, „Speichern“ ganz rechts (globale Vorgabe)
            var cancel = new ModernButton("Abbrechen", '\0', false, k) { DialogResult = DialogResult.Cancel };
            var save = new ModernButton("Speichern", '\uE74E', true, k) { Margin = new Padding(0) };
            save.Click += (s, e) => { if (Validate()) { DialogResult = DialogResult.OK; Close(); } };
            // links neben den Knöpfen: rote Fehlerzeile, das Fenster bleibt dann offen
            _error = new Label { AutoSize = true, ForeColor = Theme.Map(Color.FromArgb(196, 43, 28)), BackColor = CBack, MinimumSize = new Size(0, save.Height), TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0, 0, S(12), 0) };
            right.Controls.Add(save);
            right.Controls.Add(cancel);
            right.Controls.Add(_error);
            actions.Controls.Add(left);
            actions.Controls.Add(right);
            ButtonRow.Fit(actions, left, right); // schmal: Knöpfe links nur mit Symbol, nichts liegt unter "Speichern"

            var info = new InfoBanner("Solange dieses Fenster offen ist, sind die Tastenkürzel abgeschaltet.\n" +
                "Klick auf eine Spaltenüberschrift sortiert, die Pfeile verschieben den markierten Eintrag.", k) { Dock = DockStyle.Bottom };

            body.Controls.Add(card);
            body.Controls.Add(actions);
            body.Controls.Add(info);

            Controls.Add(body);
            Controls.Add(head);
            Controls.Add(new SignatureFooter(k));
            CancelButton = cancel;
            AcceptButton = save;
            FillList();
            Load += (s, e) => WindowPlacement.Center(this);
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        private void FillList()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            var dupes = _items.GroupBy(h => HotkeyActions.Normalize(h.Keys)).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            foreach (var h in _items)
            {
                var info = HotkeyActions.Find(h.Action);
                var item = new ListViewItem(HotkeyActions.Display(h.Keys)) { Tag = h, UseItemStyleForSubItems = false };
                if (info != null) item.ToolTipText = info.Title + "\r\n\r\n" + info.Help;
                item.Font = new Font(_list.Font, FontStyle.Bold);
                item.SubItems.Add(info != null ? info.Title : "Unbekannt: " + h.Action);
                var prof = info != null && info.NeedsProfile ? _config.Find(h.Profile ?? "") : null;
                item.SubItems.Add(info != null && info.NeedsProfile ? (prof != null ? prof.Title : (h.Profile ?? "(Standard)")) : "");

                string st; Color c;
                if (dupes.Contains(HotkeyActions.Normalize(h.Keys))) { st = "✗ doppelt"; c = CBad; }
                else
                {
                    string err = _status(HotkeyActions.Normalize(h.Keys));
                    if (err == null) { st = "neu / geändert"; c = CMuted; }
                    else if (err.Length == 0) { st = "✓ aktiv"; c = COk; }
                    else { st = "✗ belegt"; c = CBad; }
                }
                var sub = item.SubItems.Add(st);
                sub.ForeColor = c;
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            FitColumns();
            if (_count != null) _count.Text = _items.Count == 1 ? "1 Eintrag" : _items.Count + " Einträge";
            UpdateButtons();
        }

        // ================================================================== Sortieren / Spalten

        private string SortKey(HotkeyDef h, int column)
        {
            var info = HotkeyActions.Find(h.Action);
            switch (column)
            {
                case 0:
                    // Erst nach Haupttaste (F8 vor F9 vor F10), dann nach Modifikatoren
                    string n = HotkeyActions.Normalize(h.Keys);
                    string key = n.Contains("+") ? n.Substring(n.LastIndexOf('+') + 1) : n;
                    int num;
                    if (key.StartsWith("f") && int.TryParse(key.Substring(1), out num)) key = "f" + num.ToString("D2");
                    return key + "|" + n;
                case 1: return info != null ? info.Title : h.Action;
                case 2:
                    var p = _config.Find(h.Profile ?? "");
                    return p != null ? p.Title : (h.Profile ?? "");
                default:
                    string st = _status(HotkeyActions.Normalize(h.Keys));
                    return st == null ? "1" : st.Length == 0 ? "0" : "2";
            }
        }

        private void SortBy(int column)
        {
            if (_sortColumn == column) _sortDescending = !_sortDescending;
            else { _sortColumn = column; _sortDescending = false; }
            var sel = Selected;
            var sorted = _sortDescending
                ? _items.OrderByDescending(h => SortKey(h, column), StringComparer.CurrentCultureIgnoreCase).ToList()
                : _items.OrderBy(h => SortKey(h, column), StringComparer.CurrentCultureIgnoreCase).ToList();
            _items.Clear();
            _items.AddRange(sorted);
            FillList();
            if (sel != null) Select(sel);
        }

        private void MoveSelected(int delta)
        {
            var h = Selected;
            if (h == null) return;
            int i = _items.IndexOf(h), j = i + delta;
            if (j < 0 || j >= _items.Count) return;
            _items.RemoveAt(i);
            _items.Insert(j, h);
            _sortColumn = -1;
            FillList();
            Select(h);
        }

        private void FitColumns()
        {
            ListViewHelper.Fit(_list, S(24), 2); // Profil bekommt den restlichen Platz, Status bleibt schmal
        }

        private void StretchLastColumn()
        {
            ListViewHelper.Stretch(_list);
        }

        private void UpdateButtons()
        {
            bool sel = _list.SelectedItems.Count > 0;
            _edit.Enabled = sel;
            _remove.Enabled = sel;
            int idx = sel ? _items.IndexOf(Selected) : -1;
            _up.Enabled = idx > 0;
            _down.Enabled = idx >= 0 && idx < _items.Count - 1;
        }

        private HotkeyDef Selected
        {
            get { return _list.SelectedItems.Count == 0 ? null : (HotkeyDef)_list.SelectedItems[0].Tag; }
        }

        private void AddNew()
        {
            var h = new HotkeyDef { Keys = "", Action = "TogglePause" };
            using (var dlg = new HotkeyEditDialog(h, _config, Icon))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _items.Add(h);
                FillList();
                Select(h);
            }
        }

        private void EditSelected()
        {
            var h = Selected;
            if (h == null) return;
            using (var dlg = new HotkeyEditDialog(h, _config, Icon))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                FillList();
                Select(h);
            }
        }

        private void RemoveSelected()
        {
            var h = Selected;
            if (h == null) return;
            _items.Remove(h);
            FillList();
        }

        private void Select(HotkeyDef h)
        {
            foreach (ListViewItem i in _list.Items)
                if (i.Tag == h) { i.Selected = true; i.EnsureVisible(); }
            _list.Focus();
        }

        private new bool Validate()
        {
            var dupes = _items.GroupBy(h => HotkeyActions.Normalize(h.Keys)).Where(g => g.Count() > 1).ToList();
            if (dupes.Count > 0)
            {
                _error.Text = "Mehrfach vergeben: " + string.Join(", ", dupes.Select(g => HotkeyActions.Display(g.First().Keys)));
                return false;
            }
            _error.Text = "";
            return true;
        }
    }

    /// <summary>Ein Tastenkürzel bearbeiten: Kombination aufnehmen, Aktion und Profil wählen.</summary>
    internal sealed class HotkeyEditDialog : Form
    {
        private readonly HotkeyDef _def;
        private readonly HotkeyBox _box;
        private readonly ComboBox _action, _profile;
        private readonly Label _hint, _help, _desc;
        private readonly ToolTip _tip;
        private float k = 1f;

        public HotkeyEditDialog(HotkeyDef def, AppConfig cfg, Icon icon)
        {
            _def = def;
            Text = string.IsNullOrEmpty(def.Keys) ? "Tastenkürzel hinzufügen" : "Tastenkürzel bearbeiten";
            if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.CenterParent;
            // Normaler Rahmen wie bei den Hauptfenstern (Icon mit gleichem Abstand), Größe bleibt fest
            FormBorderStyle = FormBorderStyle.Sizable;
            SizeGripStyle = SizeGripStyle.Hide;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(243, 243, 243);
            Font = new Font("Segoe UI", 9.5f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            ClientSize = new Size(S(520), S(334));

            int x = S(20), w = S(480), y = S(16);
            Controls.Add(new Label { Text = "Tastenkombination (ins Feld klicken und drücken):", AutoSize = true, Location = new Point(x, y) });
            y += S(24);
            _box = new HotkeyBox { Location = new Point(x, y), Width = w, Font = new Font("Segoe UI Semibold", 12f), Keys = def.Keys };
            Controls.Add(_box);
            y += _box.Height + S(14);

            Controls.Add(new Label { Text = "Aktion:", AutoSize = true, Location = new Point(x, y + S(7)) }); // mittig zur Auswahlliste (32 hoch)
            _action = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x + S(70), y), Width = w - S(70) - S(28) };
            foreach (var a in HotkeyActions.All) _action.Items.Add(a);
            var cur = HotkeyActions.Find(def.Action);
            _action.SelectedItem = cur ?? HotkeyActions.All[0];
            Controls.Add(_action);

            // Kleines, dezentes "?" (grauer Kreis); beim Darüberfahren blau
            int hs = S(18);
            _help = new Label
            {
                Text = "",
                Size = new Size(hs, hs),
                Location = new Point(x + w - S(22), y + (_action.Height - hs + 1) / 2 + 1),
                Cursor = Cursors.Help,
                BackColor = Color.Transparent
            };
            bool hover = false;
            _help.MouseEnter += (s, e) => { hover = true; _help.Invalidate(); };
            _help.MouseLeave += (s, e) => { hover = false; _help.Invalidate(); };
            _help.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var blue = Ui.AccentText;
                float pw = Math.Max(1.2f, 1.2f * k);
                var r = new RectangleF(pw / 2, pw / 2, _help.Width - pw, _help.Height - pw);
                if (hover)
                    using (var fill = new SolidBrush(blue)) g.FillEllipse(fill, r);
                using (var pen = new Pen(blue, pw))
                    g.DrawEllipse(pen, r);
                using (var f = new Font("Segoe UI Semibold", 7.5f))
                    TextRenderer.DrawText(g, "?", f, new Rectangle(0, 0, _help.Width, _help.Height), hover ? (Accent.Luminance(blue) > 0.5 ? Color.FromArgb(20, 20, 20) : Color.White) : blue,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            };
            _tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 150, ReshowDelay = 100, ToolTipTitle = "Was passiert?" };
            Controls.Add(_help);
            y += S(32);

            _desc = new Label { AutoSize = false, Location = new Point(x + S(70), y), Size = new Size(w - S(70), S(40)), ForeColor = Color.FromArgb(97, 97, 97), Font = new Font("Segoe UI", 8.5f) };
            Controls.Add(_desc);
            y += S(46);

            Controls.Add(new Label { Text = "Profil:", AutoSize = true, Location = new Point(x, y + S(7)) });
            _profile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x + S(70), y), Width = w - S(70) };
            foreach (var p in cfg.Profiles) _profile.Items.Add(new ProfileChoice { Profile = p });
            var sel = _profile.Items.Cast<ProfileChoice>().FirstOrDefault(c => string.Equals(c.Profile.Name, def.Profile, StringComparison.OrdinalIgnoreCase))
                ?? _profile.Items.Cast<ProfileChoice>().FirstOrDefault(c => c.Profile.IsAutomatic && c.Profile.Patterns.Count > 0)
                ?? _profile.Items.Cast<ProfileChoice>().FirstOrDefault();
            _profile.SelectedItem = sel;
            Controls.Add(_profile);
            y += S(40);

            _hint = new Label { AutoSize = false, Location = new Point(x, y), Size = new Size(w, S(36)), ForeColor = Color.FromArgb(196, 43, 28) };
            Controls.Add(_hint);

            var cancel = new ModernButton("Abbrechen", '\0', false, k) { DialogResult = DialogResult.Cancel };
            var ok = new ModernButton("Übernehmen", '\uE73E', true, k); // Unterdialog speichert nichts selbst
            int by = ClientSize.Height - S(16) - cancel.Height;
            ok.Location = new Point(x + w - ok.Width, by);
            cancel.Location = new Point(ok.Left - S(10) - cancel.Width, by);
            ok.Click += (s, e) => Accept();
            Controls.Add(ok);
            Controls.Add(cancel);
            CancelButton = cancel;

            _action.SelectedIndexChanged += (s, e) => UpdateState();
            _box.KeysChanged += UpdateState;
            UpdateState();
            Shown += (s, e) => _box.Focus();
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

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
            // WM_NCHITTEST: Rand und Ecken wie festen Rahmen behandeln -> Größe bleibt fest
            if (m.Msg == 0x0084)
            {
                int hit = m.Result.ToInt32();
                if (hit >= 10 && hit <= 17) m.Result = (IntPtr)1; // HTCLIENT: keine Größenänderung (wie in allen Programmen)
            }
        }

        private sealed class ProfileChoice
        {
            public DimmingProfile Profile;
            public override string ToString() { return Profile.Title; }
        }

        private void UpdateState()
        {
            var a = _action.SelectedItem as HotkeyActions.Info;
            _profile.Enabled = a != null && a.NeedsProfile;
            string help = a != null ? a.Help : "";
            _desc.Text = (help ?? "").Replace("\r\n", " ");
            _tip.SetToolTip(_help, help);
            _tip.SetToolTip(_action, help);
            string keys = _box.Keys;
            if (string.IsNullOrEmpty(keys)) _hint.Text = "";
            else if (!HotkeyActions.HasModifier(keys) && !IsSafeAlone(keys))
                _hint.Text = "Ohne Strg, Alt oder Win würde die Taste in Spielen abgefangen. Bitte eine Kombination wählen.";
            else _hint.Text = "";
        }

        private static bool IsSafeAlone(string keys)
        {
            // F13-F24, Pause und Rollen sind auf normalen Tastaturen unbelegt bzw. ungefährlich
            string n = HotkeyActions.Normalize(keys);
            if (n == "pause" || n == "scroll") return true;
            int f;
            return n.StartsWith("f") && int.TryParse(n.Substring(1), out f) && f >= 13;
        }

        private void Accept()
        {
            string keys = _box.Keys;
            if (string.IsNullOrEmpty(keys))
            {
                _hint.Text = "Bitte eine Tastenkombination drücken.";
                _box.Focus();
                return;
            }
            if (!HotkeyActions.HasModifier(keys) && !IsSafeAlone(keys))
            {
                _box.Focus();
                return;
            }
            var a = (HotkeyActions.Info)_action.SelectedItem;
            _def.Keys = keys;
            _def.Action = a.Id;
            var p = _profile.SelectedItem as ProfileChoice;
            _def.Profile = a.NeedsProfile && p != null ? p.Profile.Name : null;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>Eingabefeld, das eine gedrückte Tastenkombination aufnimmt statt Text.</summary>
    internal sealed class HotkeyBox : TextBox
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vk);

        private string _keys = "";
        public event Action KeysChanged;

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = Color.White;
            TextAlign = HorizontalAlignment.Center;
            Cursor = Cursors.Hand;
            ShortcutsEnabled = false;
        }

        /// <summary>Speicherform, z. B. "Ctrl+Alt+F10".</summary>
        public string Keys
        {
            get { return _keys; }
            set
            {
                _keys = value ?? "";
                Text = _keys.Length == 0 ? "(keine)" : HotkeyActions.Display(_keys);
                var h = KeysChanged;
                if (h != null) h();
            }
        }

        protected override bool IsInputKey(System.Windows.Forms.Keys keyData) { return true; }

        protected override bool ProcessCmdKey(ref Message msg, System.Windows.Forms.Keys keyData)
        {
            if (!Focused) return base.ProcessCmdKey(ref msg, keyData);
            TakeKeys(keyData);
            return true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            e.Handled = true;
        }

        private void TakeKeys(System.Windows.Forms.Keys keyData)
        {
            var key = keyData & System.Windows.Forms.Keys.KeyCode;
            var mods = keyData & System.Windows.Forms.Keys.Modifiers;
            bool win = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

            if (key == System.Windows.Forms.Keys.ControlKey || key == System.Windows.Forms.Keys.ShiftKey ||
                key == System.Windows.Forms.Keys.Menu || key == System.Windows.Forms.Keys.LWin || key == System.Windows.Forms.Keys.RWin)
            {
                // Nur Modifikator gedrückt: Vorschau anzeigen
                Text = ModText(mods, win) + "...";
                return;
            }
            if (mods == System.Windows.Forms.Keys.None && !win &&
                (key == System.Windows.Forms.Keys.Back || key == System.Windows.Forms.Keys.Delete))
            {
                Keys = "";
                return;
            }
            if (mods == System.Windows.Forms.Keys.None && !win && (key == System.Windows.Forms.Keys.Escape || key == System.Windows.Forms.Keys.Tab))
            {
                // Esc/Tab ohne Modifikator: Dialog bedienen statt aufnehmen
                if (key == System.Windows.Forms.Keys.Escape) { var f = FindForm(); if (f != null) f.Close(); }
                else { var f = FindForm(); if (f != null) f.SelectNextControl(this, true, true, true, true); }
                return;
            }

            var parts = new List<string>();
            if ((mods & System.Windows.Forms.Keys.Control) != 0) parts.Add("Ctrl");
            if ((mods & System.Windows.Forms.Keys.Alt) != 0) parts.Add("Alt");
            if ((mods & System.Windows.Forms.Keys.Shift) != 0) parts.Add("Shift");
            if (win) parts.Add("Win");
            parts.Add(key.ToString());
            Keys = string.Join("+", parts);
        }

        private static string ModText(System.Windows.Forms.Keys mods, bool win)
        {
            var p = new List<string>();
            if ((mods & System.Windows.Forms.Keys.Control) != 0) p.Add("Strg");
            if ((mods & System.Windows.Forms.Keys.Alt) != 0) p.Add("Alt");
            if ((mods & System.Windows.Forms.Keys.Shift) != 0) p.Add("Umschalt");
            if (win) p.Add("Win");
            return string.Join(" + ", p) + (p.Count > 0 ? " + " : "");
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Keys = _keys; // halbe Eingabe ("Strg + ...") zurücksetzen
        }
    }
}
