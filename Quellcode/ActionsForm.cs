// Monitor Dimmer – Fenster „Aktionen“ zum Bearbeiten der Profil-Aktionen
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace MonitorDimmer
{
    /// <summary>Fenster "Aktionen": was beim Aktivieren/Deaktivieren eines Profils passiert (statt .bat-Skripten).</summary>
    internal sealed class ActionsForm : Form
    {
        private sealed class ProfileData
        {
            public DimmingProfile Source;
            public List<ActionDef> Applied, Unapplied;
            public bool RemoveHandlers;
            public override string ToString() { return Source.Title; }
            public bool HasScripts { get { return Source.Handlers.Count > 0 && !RemoveHandlers; } }
        }

        private readonly List<ProfileData> _profiles;
        private readonly string _resourcesFolder;
        private readonly ComboBox _profile;
        private readonly ListView _applied, _unapplied;
        private readonly Label _appliedTitle, _unappliedTitle, _appliedCount, _unappliedCount, _scriptHint;
        private readonly ModernButton _edit, _remove, _up, _down, _test, _import, _copy;
        private readonly ImageList _images;
        private ListView _active;
        private readonly ActionChecker _checker = new ActionChecker();
        private float k = 1f;

        /// <summary>Profile mit den geänderten Aktionen (zum Speichern).</summary>
        public List<DimmingProfile> Result
        {
            get
            {
                return _profiles.Select(p => new DimmingProfile { Name = p.Source.Name, OnApplied = p.Applied, OnUnapplied = p.Unapplied }).ToList();
            }
        }

        /// <summary>Profile, deren alte Skripte (&lt;EventHandlers&gt;) entfernt werden sollen.</summary>
        public List<string> RemoveHandlers
        {
            get { return _profiles.Where(p => p.RemoveHandlers).Select(p => p.Source.Name).ToList(); }
        }

        public ActionsForm(AppConfig config, string resourcesFolder, string selectProfile, Icon icon)
        {
            _resourcesFolder = resourcesFolder;
            _profiles = config.Profiles.Where(p => !p.IsUndimmed).Select(p => new ProfileData
            {
                Source = p,
                Applied = p.OnApplied.Select(a => a.Clone()).ToList(),
                Unapplied = p.OnUnapplied.Select(a => a.Clone()).ToList()
            }).ToList();

            Text = "Monitor Dimmer - Aktionen";
            if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.Manual;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            ClientSize = new Size(S(1180), S(760));
            MinimumSize = new Size(S(900), S(560));

            var head = new HeaderPanel("Aktionen", "Was beim Wechsel in ein Profil automatisch passiert", k);

            // ---------- Bilder für die Aktionsarten ----------
            _images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(S(22), S(22)) };
            foreach (var t in ActionTypes.All)
            {
                var img = MenuHelper.Glyph(t.Glyph, Ui.AccentText, S(16), S(22));
                if (img != null)
                {
                    var bmp = new Bitmap(S(22), S(22));
                    using (var g = Graphics.FromImage(bmp)) g.DrawImage(img, 0, (S(22) - img.Height) / 2);
                    img.Dispose();
                    _images.Images.Add(t.Id, bmp);
                }
            }

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Back, Padding = new Padding(S(28), 0, S(28), 0) };

            // ---------- Profil-Auswahl ----------
            var top = new Panel { Dock = DockStyle.Top, Height = S(58), BackColor = Ui.Back };
            var lbl = new Label { Text = "Profil", ForeColor = Ui.Muted, AutoSize = true, Font = new Font("Segoe UI", 10f), Location = new Point(0, S(12)) };
            _profile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 10f), Location = new Point(S(64), S(8)), Width = S(420) };
            foreach (var p in _profiles) _profile.Items.Add(p);
            _import = new ModernButton("Skripte übernehmen", '', false, k) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _import.Click += (s, e) => ImportScripts();
            _scriptHint = new Label { ForeColor = Ui.Muted, AutoSize = true, Font = new Font("Segoe UI", 9f), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            top.Controls.Add(lbl);
            top.Controls.Add(_profile);
            top.Controls.Add(_import);
            top.Controls.Add(_scriptHint);
            top.Layout += (s, e) =>
            {
                _import.Location = new Point(top.Width - _import.Width, S(2));
                _scriptHint.Location = new Point(_import.Left - S(14) - _scriptHint.Width, S(14));
            };

            // ---------- Zwei Listen nebeneinander ----------
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Ui.Back, Padding = new Padding(0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _applied = MakeList();
            _unapplied = MakeList();
            var leftCard = MakeCard("Beim Aktivieren (Spiel startet)", _applied, out _appliedTitle, out _appliedCount);
            var rightCard = MakeCard("Beim Deaktivieren (Spiel beendet)", _unapplied, out _unappliedTitle, out _unappliedCount);
            leftCard.Margin = new Padding(0, 0, S(8), 0);
            rightCard.Margin = new Padding(S(8), 0, 0, 0);
            grid.Controls.Add(leftCard, 0, 0);
            grid.Controls.Add(rightCard, 1, 0);

            // ---------- Knöpfe ----------
            var actions = new Panel { Dock = DockStyle.Bottom, Height = S(72), BackColor = Ui.Back };
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Ui.Back, Padding = new Padding(0, S(16), 0, 0) };
            var add = new ModernButton("Hinzufügen", '', false, k);
            add.Click += (s, e) => AddNew();
            _edit = new ModernButton("Bearbeiten", '', false, k);
            _edit.Click += (s, e) => EditSelected();
            _remove = new ModernButton("Entfernen", '', false, k);
            _remove.Click += (s, e) => RemoveSelected();
            _up = new ModernButton("", '', false, k);
            _up.Click += (s, e) => MoveSelected(-1);
            _down = new ModernButton("", '', false, k);
            _down.Click += (s, e) => MoveSelected(1);
            _test = new ModernButton("Jetzt testen", '', false, k);
            _test.Click += (s, e) => TestActive();
            var tip = new ToolTip();
            tip.SetToolTip(_up, "Nach oben schieben");
            tip.SetToolTip(_down, "Nach unten schieben");
            tip.SetToolTip(_test, "Führt die markierte Liste sofort aus");
            _copy = new ModernButton("Kopieren nach", '\uE8C8', false, k);
            _copy.Click += (s, e) => ShowCopyMenu(_copy, new Point(0, _copy.Height));
            tip.SetToolTip(_copy, "Markierte Aktion oder ganze Liste in andere Profile kopieren");
            left.Controls.AddRange(new Control[] { add, _edit, _remove, _up, _down, _copy, _test });

            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, BackColor = Ui.Back, Padding = new Padding(0, S(16), 0, 0) };
            var cancel = new ModernButton("Abbrechen", '\0', false, k) { DialogResult = DialogResult.Cancel, Margin = new Padding(0) };
            var save = new ModernButton("Speichern", '', true, k);
            save.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            right.Controls.Add(cancel);
            right.Controls.Add(save);
            actions.Controls.Add(left);
            actions.Controls.Add(right);
            ButtonRow.Fit(actions, left, right); // schmal: Knöpfe links nur mit Symbol, nichts liegt unter "Speichern"

            var info = new InfoBanner("Die Aktionen laufen nacheinander im Hintergrund. Dienste und Energiesparpläne brauchen Adminrechte.\n" +
                "Doppelklick bearbeitet, Entf entfernt. Ins Protokoll kommt jede Aktion mit Ergebnis.", k) { Dock = DockStyle.Bottom };

            body.Controls.Add(grid);
            body.Controls.Add(top);
            body.Controls.Add(actions);
            body.Controls.Add(info);

            Controls.Add(body);
            Controls.Add(head);
            Controls.Add(new SignatureFooter(k));
            CancelButton = cancel;
            AcceptButton = save;

            _active = _applied;
            _profile.SelectedIndexChanged += (s, e) => ShowProfile();
            int sel = Math.Max(0, _profiles.FindIndex(p => string.Equals(p.Source.Name, selectProfile, StringComparison.OrdinalIgnoreCase)));
            if (_profiles.Count > 0) _profile.SelectedIndex = sel;
            Load += (s, e) => WindowPlacement.Center(this);
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        private ProfileData Current { get { return _profile.SelectedItem as ProfileData; } }

        private List<ActionDef> ListOf(ListView lv)
        {
            var p = Current;
            if (p == null) return new List<ActionDef>();
            return lv == _applied ? p.Applied : p.Unapplied;
        }

        private ListView MakeList()
        {
            var lv = new BufferedListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                HeaderStyle = ColumnHeaderStyle.None,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                BackColor = Ui.Card,
                Font = new Font("Segoe UI", 9.5f),
                SmallImageList = _images,
                ShowItemToolTips = true
            };
            lv.Columns.Add("Aktion", S(400));
            lv.Resize += (s, e) => { if (lv.Columns.Count > 0) lv.Columns[0].Width = Math.Max(S(100), lv.ClientSize.Width - 2); };
            lv.Enter += (s, e) => SetActive(lv);
            lv.MouseDown += (s, e) => SetActive(lv);
            lv.SelectedIndexChanged += (s, e) => UpdateButtons();
            lv.DoubleClick += (s, e) => EditSelected();
            lv.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
            lv.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                SetActive(lv);
                var hit = lv.HitTest(e.Location);
                if (hit.Item != null) hit.Item.Selected = true;
                ShowCopyMenu(lv, e.Location);
            };
            return lv;
        }

        private CardPanel MakeCard(string title, ListView lv, out Label titleLabel, out Label countLabel)
        {
            var card = new CardPanel(k) { Dock = DockStyle.Fill };
            var cardHead = new Panel { Dock = DockStyle.Top, Height = S(46), BackColor = Ui.Card };
            var t = new Label { Text = title, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Ui.Text, AutoSize = true, Location = new Point(0, S(8)), BackColor = Color.Transparent };
            var c = new Label { ForeColor = Ui.Muted, AutoSize = true, Font = new Font("Segoe UI", 10f), Location = new Point(S(80), S(10)), BackColor = Color.Transparent };
            cardHead.Controls.Add(t);
            cardHead.Controls.Add(c);
            cardHead.Layout += (s, e) => c.Left = t.Right + S(14);
            cardHead.Click += (s, e) => { SetActive(lv); lv.Focus(); };
            t.Click += (s, e) => { SetActive(lv); lv.Focus(); };
            card.Controls.Add(lv);
            card.Controls.Add(cardHead);
            titleLabel = t;
            countLabel = c;
            return card;
        }

        private void SetActive(ListView lv)
        {
            if (_active == lv) return;
            _active = lv;
            UpdateTitles();
            UpdateButtons();
        }

        private void UpdateTitles()
        {
            _appliedTitle.ForeColor = _active == _applied ? Ui.AccentText : Ui.Text;
            _unappliedTitle.ForeColor = _active == _unapplied ? Ui.AccentText : Ui.Text;
        }

        private void ShowProfile()
        {
            Fill(_applied);
            Fill(_unapplied);
            var p = Current;
            bool scripts = p != null && p.HasScripts;
            _import.Visible = _profiles.Any(x => x.HasScripts);
            _scriptHint.Text = scripts ? "Dieses Profil startet noch .bat-Skripte" : (p != null && p.RemoveHandlers ? "Skripte werden beim Speichern entfernt" : "");
            _scriptHint.Parent.PerformLayout();
            UpdateTitles();
            UpdateButtons();
        }

        private void Fill(ListView lv)
        {
            var list = ListOf(lv);
            lv.BeginUpdate();
            lv.Items.Clear();
            int errors = 0, warnings = 0;
            Cursor = Cursors.WaitCursor;
            foreach (var a in list)
            {
                var info = ActionTypes.Find(a.Type);
                var check = _checker.Check(a);
                string text = ActionTypes.Describe(a);
                if (check.Level > 0) text += "   ⚠ " + check.Text;
                var item = new ListViewItem(text) { Tag = a, ImageKey = a.Type };
                item.ToolTipText = (check.Level > 0 ? check.Text + "\r\n\r\n" : "") + (info != null ? info.Hint : "");
                if (check.Level == 2) { item.ForeColor = Theme.Map(Color.FromArgb(196, 43, 28)); errors++; }
                else if (check.Level == 1) { item.ForeColor = Theme.Map(Color.FromArgb(157, 93, 0)); warnings++; }
                lv.Items.Add(item);
            }
            Cursor = Cursors.Default;
            lv.EndUpdate();
            string n = list.Count == 1 ? "1 Aktion" : list.Count + " Aktionen";
            if (errors > 0) n += "  ·  " + errors + (errors == 1 ? " Fehler" : " Fehler");
            if (warnings > 0) n += "  ·  " + warnings + (warnings == 1 ? " Hinweis" : " Hinweise");
            var countLabel = lv == _applied ? _appliedCount : _unappliedCount;
            countLabel.Text = n;
            countLabel.ForeColor = errors > 0 ? Theme.Map(Color.FromArgb(196, 43, 28)) : Ui.Muted;
        }

        private void UpdateButtons()
        {
            var lv = _active;
            int idx = lv.SelectedIndices.Count > 0 ? lv.SelectedIndices[0] : -1;
            _edit.Enabled = _remove.Enabled = idx >= 0;
            _up.Enabled = idx > 0;
            _down.Enabled = idx >= 0 && idx < lv.Items.Count - 1;
            _test.Enabled = lv.Items.Count > 0;
        }

        private void Select(ListView lv, int index)
        {
            if (index < 0 || index >= lv.Items.Count) return;
            lv.Items[index].Selected = true;
            lv.Items[index].Focused = true;
            lv.EnsureVisible(index);
            lv.Focus();
        }

        private void AddNew()
        {
            if (Current == null) return;
            var a = new ActionDef { Type = "KillProcess" };
            using (var dlg = new ActionEditDialog(a, Icon, true))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var list = ListOf(_active);
                int at = _active.SelectedIndices.Count > 0 ? _active.SelectedIndices[0] + 1 : list.Count;
                list.Insert(at, a);
                Fill(_active);
                Select(_active, at);
            }
        }

        private void EditSelected()
        {
            if (_active.SelectedIndices.Count == 0) return;
            int i = _active.SelectedIndices[0];
            var a = ListOf(_active)[i];
            using (var dlg = new ActionEditDialog(a, Icon, false))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Fill(_active);
                Select(_active, i);
            }
        }

        private void RemoveSelected()
        {
            if (_active.SelectedIndices.Count == 0) return;
            int i = _active.SelectedIndices[0];
            ListOf(_active).RemoveAt(i);
            Fill(_active);
            Select(_active, Math.Min(i, _active.Items.Count - 1));
            UpdateButtons();
        }

        private void MoveSelected(int delta)
        {
            if (_active.SelectedIndices.Count == 0) return;
            var list = ListOf(_active);
            int i = _active.SelectedIndices[0], j = i + delta;
            if (j < 0 || j >= list.Count) return;
            var a = list[i];
            list.RemoveAt(i);
            list.Insert(j, a);
            Fill(_active);
            Select(_active, j);
        }

        // ---------------------------------------------------------------- Kopieren

        /// <summary>Menü "Kopieren nach": markierte Aktion oder ganze Liste in andere Profile (gleiche Seite: Aktivieren/Deaktivieren).</summary>
        private void ShowCopyMenu(Control owner, Point at)
        {
            var current = Current;
            if (current == null) return;
            var list = ListOf(_active);
            var others = _profiles.Where(p => p != current).ToList();
            var mb = new ModernMenuBuilder(this);
            var menu = mb.CreateMenu();
            menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
            string side = _active == _applied ? "\"Beim Aktivieren\"" : "\"Beim Deaktivieren\"";

            if (owner is ListView && _active.SelectedIndices.Count > 0)
            {
                menu.Items.Add(mb.Item("Bearbeiten", '\uE70F', EditSelected));
                menu.Items.Add(mb.Item("Entfernen", '\uE74D', RemoveSelected));
                menu.Items.Add(new ToolStripSeparator());
            }
            if (others.Count == 0)
            {
                menu.Items.Add(mb.Info("Keine anderen Profile vorhanden"));
            }
            else
            {
                if (_active.SelectedIndices.Count > 0)
                {
                    var a = list[_active.SelectedIndices[0]];
                    menu.Items.Add(CopyItem(mb, "Markierte Aktion kopieren nach", '\uE8C8', new List<ActionDef> { a }, others));
                }
                if (list.Count > 0)
                    menu.Items.Add(CopyItem(mb, "Ganze Liste " + side + " kopieren nach", '\uE8B3', list, others));
                if (menu.Items.OfType<GlyphMenuItem>().Count() == 0)
                    menu.Items.Add(mb.Info("Liste ist leer"));
            }
            menu.Show(owner, at);
        }

        private ToolStripMenuItem CopyItem(ModernMenuBuilder mb, string text, char glyph, List<ActionDef> actions, List<ProfileData> targets)
        {
            var root = mb.Item(text, glyph, null);
            foreach (var t in targets)
            {
                var target = t;
                root.DropDownItems.Add(mb.Item(target.ToString(), MenuHelper.Monitor, () => CopyTo(actions, new List<ProfileData> { target })));
            }
            if (targets.Count > 1)
            {
                root.DropDownItems.Add(new ToolStripSeparator());
                root.DropDownItems.Add(mb.Item("Alle anderen Profile", '\uE8B3', () => CopyTo(actions, targets)));
            }
            mb.StyleDropDown(root);
            return root;
        }

        /// <summary>Hängt die Aktionen an die gleiche Liste der Zielprofile an; gleiche Aktionen werden nicht doppelt eingetragen.</summary>
        private void CopyTo(List<ActionDef> actions, List<ProfileData> targets)
        {
            bool applied = _active == _applied;
            int added = 0, skipped = 0;
            foreach (var t in targets)
            {
                var dest = applied ? t.Applied : t.Unapplied;
                foreach (var a in actions.ToList())
                {
                    bool exists = dest.Any(d => string.Equals(d.Type, a.Type, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(d.Value ?? "", a.Value ?? "", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(d.Arguments ?? "", a.Arguments ?? "", StringComparison.OrdinalIgnoreCase));
                    if (exists) { skipped++; continue; }
                    dest.Add(a.Clone());
                    added++;
                }
            }
            string where = targets.Count == 1 ? "\"" + targets[0] + "\"" : targets.Count + " Profile";
            MessageBox.Show(this, added + (added == 1 ? " Aktion" : " Aktionen") + " nach " + where + " kopiert" +
                (skipped > 0 ? ", " + skipped + " schon vorhanden und übersprungen" : "") + ".\n\nWirksam nach \"Speichern\".",
                "Kopieren", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Übernimmt die .bat-Skripte aller Profile als Aktionen.</summary>
        private void ImportScripts()
        {
            var targets = _profiles.Where(p => p.HasScripts).ToList();
            if (targets.Count == 0) return;
            string names = string.Join("\n", targets.Select(t => "  • " + t.Source.Title));
            bool overwrite = targets.Any(t => t.Applied.Count > 0 || t.Unapplied.Count > 0);
            string msg = "Die .bat-Skripte dieser Profile werden in Aktionen übersetzt:\n\n" + names + "\n\n" +
                         (overwrite ? "Vorhandene Aktionen dieser Profile werden dabei ersetzt.\n" : "") +
                         "Beim Speichern werden die Skript-Einträge aus der Konfiguration entfernt (die .bat-Dateien selbst bleiben liegen).\n\nFortfahren?";
            if (MessageBox.Show(this, msg, "Skripte übernehmen", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            int total = 0, commands = 0;
            foreach (var t in targets)
            {
                var notes = new List<string>();
                t.Applied = ScriptImporter.Import(t.Source, "OnAfterDimmingProfileApplied", _resourcesFolder, notes);
                t.Unapplied = ScriptImporter.Import(t.Source, "OnAfterDimmingProfileUnapplied", _resourcesFolder, notes);
                t.RemoveHandlers = true;
                foreach (var n in notes) files.Add(n);
                total += t.Applied.Count + t.Unapplied.Count;
                commands += t.Applied.Concat(t.Unapplied).Count(a => a.Type == "Command");
            }
            ShowProfile();
            MessageBox.Show(this, total + " Aktionen aus " + string.Join(", ", files) + " übernommen." +
                (commands > 0 ? "\n\n" + commands + " Zeilen ließen sich nicht zuordnen und stehen als \"Befehl ausführen\" in der Liste." : "") +
                "\n\nBitte kurz prüfen und dann speichern.", "Skripte übernehmen", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Führt die markierte Liste sofort aus (im Hintergrund) und zeigt das Ergebnis.</summary>
        private void TestActive()
        {
            var list = ListOf(_active).Select(a => a.Clone()).ToList();
            if (list.Count == 0) return;
            string profileName = Current != null ? Current.Source.Name : null;
            string which = _active == _applied ? "\"Beim Aktivieren\"" : "\"Beim Deaktivieren\"";
            if (MessageBox.Show(this, "Die Liste " + which + " jetzt ausführen?\n\nProgramme und Dienste werden dabei wirklich beendet bzw. gestartet.",
                    "Jetzt testen", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _test.Enabled = false;
            Cursor = Cursors.AppStarting;
            var t = new Thread(() =>
            {
                var lines = new List<string>();
                foreach (var a in list)
                {
                    string r = ActionExecutor.Execute(a, profileName);
                    Log.Info("Test-Aktion: {0} -> {1}", ActionTypes.Describe(a), r);
                    lines.Add(ActionTypes.Describe(a) + "\n      → " + r);
                }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        Cursor = Cursors.Default;
                        UpdateButtons();
                        MessageBox.Show(this, string.Join("\n", lines), "Ergebnis", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
                catch (Exception) { }
            }) { IsBackground = true, Name = "Aktionen testen" };
            t.Start();
        }
    }

    /// <summary>Eine Aktion anlegen oder ändern.</summary>
    internal sealed class ActionEditDialog : Form
    {
        private readonly ActionDef _def;
        private readonly ComboBox _type, _value;
        private readonly TextBox _args;
        private readonly Label _valueLabel, _argsLabel, _hint, _error;
        private readonly ModernButton _browse;
        private float k = 1f;

        public ActionEditDialog(ActionDef def, Icon icon, bool isNew)
        {
            _def = def;
            Text = isNew ? "Aktion hinzufügen" : "Aktion bearbeiten";
            if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            SizeGripStyle = SizeGripStyle.Hide;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9.5f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            ClientSize = new Size(S(620), S(330));

            int x = S(20), lw = S(130), w = S(580), y = S(20);
            Controls.Add(new Label { Text = "Art", AutoSize = true, ForeColor = Ui.Text, Location = new Point(x, y + S(7)) }); // mittig zur Auswahlliste (32 hoch)
            _type = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x + lw, y), Width = w - lw };
            foreach (var t in ActionTypes.All) _type.Items.Add(t);
            Controls.Add(_type);
            y += S(42);

            _valueLabel = new Label { AutoSize = true, ForeColor = Ui.Text, Location = new Point(x, y + S(4)) };
            Controls.Add(_valueLabel);
            _browse = new ModernButton("", '', false, k);
            _browse.Location = new Point(x + w - _browse.Width, y - S(6));
            _browse.Click += (s, e) => Browse();
            new ToolTip().SetToolTip(_browse, "Datei auswählen");
            Controls.Add(_browse);
            _value = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Location = new Point(x + lw, y), Width = w - lw, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems, MaxDropDownItems = 20 };
            Controls.Add(_value);
            y += S(42);

            _argsLabel = new Label { Text = "Parameter", AutoSize = true, ForeColor = Ui.Text, Location = new Point(x, y + S(4)) };
            _args = new TextBox { Location = new Point(x + lw, y), Width = w - lw, Text = def.Arguments ?? "" };
            Controls.Add(_argsLabel);
            Controls.Add(_args);
            y += S(42);

            _hint = new Label { AutoSize = false, Location = new Point(x + lw, y), Size = new Size(w - lw, S(44)), ForeColor = Ui.Muted, Font = new Font("Segoe UI", 9f) };
            Controls.Add(_hint);
            y += S(46);
            _error = new Label { AutoSize = false, Location = new Point(x + lw, y), Size = new Size(w - lw, S(24)), ForeColor = Theme.Map(Color.FromArgb(196, 43, 28)) };
            Controls.Add(_error);

            var cancel = new ModernButton("Abbrechen", '\0', false, k) { DialogResult = DialogResult.Cancel };
            var ok = new ModernButton("OK", '', true, k);
            int by = ClientSize.Height - S(18) - cancel.Height;
            cancel.Location = new Point(x + w - cancel.Width, by);
            ok.Location = new Point(cancel.Left - S(10) - ok.Width, by);
            ok.Click += (s, e) => Accept();
            Controls.Add(ok);
            Controls.Add(cancel);
            CancelButton = cancel;
            AcceptButton = ok;

            _type.SelectedIndexChanged += (s, e) => TypeChanged(true);
            var cur = ActionTypes.Find(def.Type) ?? ActionTypes.All[0];
            _type.SelectedItem = cur;
            TypeChanged(false);
            SetValue(def.Value ?? "");
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x0084)
            {
                int hit = m.Result.ToInt32();
                if (hit >= 10 && hit <= 17) m.Result = (IntPtr)1; // HTCLIENT: keine Größenänderung (wie in allen Programmen)
            }
        }

        private ActionTypes.Info Type { get { return (ActionTypes.Info)_type.SelectedItem; } }

        private void TypeChanged(bool clear)
        {
            var t = Type;
            _valueLabel.Text = t.ValueLabel;
            _hint.Text = t.Hint;
            _args.Visible = _argsLabel.Visible = t.HasArguments;
            _value.Visible = _valueLabel.Visible = !t.NoValue;
            bool browse = (t.Id == "StartProgram" || t.Id == "KillProcess") && !t.NoValue;
            _browse.Visible = browse;
            _value.Width = (S(580) - S(130)) - (browse ? _browse.Width + S(8) : 0);
            _error.Text = "";

            Cursor = Cursors.WaitCursor;
            List<SystemLists.Item> items;
            try
            {
                switch (t.Id)
                {
                    case "KillProcess": items = SystemLists.Processes(); break;
                    case "StopService":
                    case "StartService": items = SystemLists.Services(); break;
                    case "PowerPlan": items = SystemLists.PowerPlans(); break;
                    case "AudioDevice": items = SystemLists.AudioDevices(); break;
                    case "TaskRun":
                    case "TaskEnable":
                    case "TaskDisable": items = SystemLists.Tasks(); break;
                    case "Wait": items = new[] { "1", "2", "3", "5", "10", "30" }.Select(v => new SystemLists.Item { Value = v, Text = v }).ToList(); break;
                    default: items = new List<SystemLists.Item>(); break;
                }
            }
            finally { Cursor = Cursors.Default; }
            string keep = _value.Text;
            _value.BeginUpdate();
            _value.Items.Clear();
            foreach (var i in items) _value.Items.Add(i);
            _value.EndUpdate();
            _value.Text = clear ? "" : keep;
        }

        private void SetValue(string v)
        {
            foreach (SystemLists.Item i in _value.Items)
                if (string.Equals(i.Value, v, StringComparison.OrdinalIgnoreCase)) { _value.SelectedItem = i; return; }
            _value.Text = v;
        }

        /// <summary>Gewählter Eintrag liefert seinen Wert (z. B. GUID, Dienstname), sonst der getippte Text.</summary>
        private string GetValue()
        {
            string text = _value.Text.Trim();
            foreach (SystemLists.Item i in _value.Items)
                if (string.Equals(i.Text, text, StringComparison.OrdinalIgnoreCase)) return i.Value;
            return text;
        }

        private void Browse()
        {
            using (var d = new OpenFileDialog { Filter = "Programme (*.exe)|*.exe|Alle Dateien (*.*)|*.*", Title = "Programm auswählen" })
            {
                string cur = Environment.ExpandEnvironmentVariables(GetValue().Trim('"'));
                try { if (cur.Length > 0 && Directory.Exists(Path.GetDirectoryName(cur))) d.InitialDirectory = Path.GetDirectoryName(cur); } catch { }
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _value.Text = Type.Id == "KillProcess" ? Path.GetFileName(d.FileName) : d.FileName;
            }
        }

        private void Accept()
        {
            var t = Type;
            string v = t.NoValue ? "" : GetValue();
            if (v.Length == 0 && !t.NoValue) { _error.Text = "Bitte " + t.ValueLabel + " angeben."; _value.Focus(); return; }
            if (t.Id == "PowerPlan") { Guid g; if (!Guid.TryParse(v, out g)) { _error.Text = "Bitte einen Energiesparplan aus der Liste wählen."; return; } }
            if (t.Id == "Wait") { int s; if (!int.TryParse(v, out s) || s < 0 || s > 60) { _error.Text = "Bitte 0 bis 60 Sekunden angeben."; return; } }
            if (t.Id == "StartProgram" && !File.Exists(Environment.ExpandEnvironmentVariables(v.Trim('"'))))
                _error.Text = "Hinweis: Datei existiert (noch) nicht.";
            _def.Type = t.Id;
            _def.Value = v;
            _def.Arguments = t.HasArguments ? _args.Text.Trim() : "";
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
