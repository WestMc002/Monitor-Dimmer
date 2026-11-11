// Monitor Dimmer – Fenster „Spieleliste“
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MonitorDimmer
{
    /// <summary>Fenster mittig auf den Bildschirm setzen, auf dem die Maus ist (nie unter die Taskleiste).</summary>
    internal static class WindowPlacement
    {
        public static void Center(Form f)
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int margin = 24;
            if (f.Width > area.Width - margin) f.Width = area.Width - margin;
            if (f.Height > area.Height - margin) f.Height = area.Height - margin;
            f.Location = new Point(area.Left + (area.Width - f.Width) / 2, area.Top + (area.Height - f.Height) / 2);
        }
    }

    /// <summary>Spieleliste je Profil anzeigen, prüfen und bearbeiten.</summary>
    internal sealed class GameListForm : Form, ISettingsPage
    {
        private static Color CBack { get { return Theme.Map(Color.FromArgb(243, 243, 243)); } }
        private static Color CText { get { return Theme.Map(Color.FromArgb(27, 27, 27)); } }
        private static Color CMuted { get { return Theme.Map(Color.FromArgb(97, 97, 97)); } }
        private static Color COk { get { return Theme.Map(Color.FromArgb(15, 123, 15)); } }
        private static Color CMissing { get { return Theme.Map(Color.FromArgb(196, 43, 28)); } }
        private static Color CInfo { get { return Theme.Map(Color.FromArgb(91, 123, 213)); } }

        private static GameListForm _instance;

        private readonly Func<AppConfig> _config;
        private readonly Action<string, List<string>> _save;
        private readonly Action<List<string>> _ignore;
        private readonly ComboBox _profile;
        private readonly ListView _list;
        private readonly Label _summary;
        private readonly ImageList _icons;
        private readonly ModernButton _moveButton;
        private readonly ModernButton _searchButton;
        private readonly ModernTextBox _filter;
        /// <summary>Alle fehlenden Einträge des Profils, auch wenn das Suchfeld sie gerade ausblendet.</summary>
        private readonly List<string> _missingAll = new List<string>();

        // Änderungen werden gesammelt und erst mit "Speichern" übernommen (globale Vorgabe Einstellungsseiten)
        private readonly Dictionary<string, List<string>> _pending = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _pendingIgnore = new List<string>();
        private float k = 1f;

        private enum EntryState { Found, Missing, Wildcard, FileNameOnly, NotCheckable }

        private sealed class Entry
        {
            public string Raw;
            public EntryState State;
        }

        private static readonly HashSet<string> GenericFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "bin32", "bin64", "binaries", "win32", "win64", "x86", "x64", "_retail_", "retail", "game",
            "launcher", "sources", "en_us", "sp22", "shipping", "client", "release", "sp", "mp", "system", "src",
            "data", "exe", "program", "programs", "x64_ship", "x86_ship"
        };

        public static void ShowWindow(Func<AppConfig> config, Action<string, List<string>> save, Action<List<string>> ignore, Icon icon)
        {
            if (_instance != null && !_instance.IsDisposed)
            {
                _instance.Reload();
                _instance.Activate();
                return;
            }
            _instance = new GameListForm(config, save, ignore, icon);
            _instance.Show();
        }

        /// <summary>Nach dem Neuladen der Konfiguration die offene Liste aktualisieren.</summary>
        public static void RefreshIfOpen()
        {
            if (_instance != null && !_instance.IsDisposed) _instance.Reload();
        }

        internal GameListForm(Func<AppConfig> config, Action<string, List<string>> save, Action<List<string>> ignore, Icon icon)
        {
            _config = config;
            _instance = this; // RefreshIfOpen: auch als Seite im Hauptfenster
            FormClosed += (s, e) => { if (_instance == this) _instance = null; };
            _save = save;
            _ignore = ignore;
            Text = "Monitor Dimmer - Spieleliste";
            if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.Manual;
            BackColor = CBack;
            Font = new Font("Segoe UI", 9f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            ClientSize = new Size(S(1100), S(800));
            MinimumSize = new Size(S(700), S(420));

            // ---------- Kopf ----------
            var head = new HeaderPanel("Spieleliste", "Programme, bei denen ein Profil automatisch aktiv wird", k);
            _searchButton = new ModernButton("Neue / fehlende suchen...", '\uE721', false, k);
            _searchButton.Click += (s, e) => SearchMissing();
            head.SetRight(_searchButton);

            // ---------- Karte mit Liste ----------
            var body = new Panel { Dock = DockStyle.Fill, BackColor = CBack, Padding = new Padding(S(28), 0, S(28), 0) };
            var card = new CardPanel(k) { Dock = DockStyle.Fill };
            var cardHead = new Panel { Dock = DockStyle.Top, Height = S(52), BackColor = Ui.Card };
            var title = new Label { Text = "Spiele", Font = new Font("Segoe UI Semibold", 11f), ForeColor = CText, AutoSize = true, Location = new Point(0, S(10)), BackColor = Color.Transparent };
            _summary = new Label { ForeColor = CMuted, AutoSize = true, Font = new Font("Segoe UI", 10f), Location = new Point(S(80), S(12)), BackColor = Color.Transparent };
            _profile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(360), Font = new Font("Segoe UI", 9.5f), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _profile.SelectedIndexChanged += (s, e) => FillList();
            var lbl = new Label { Text = "Profil", ForeColor = CMuted, AutoSize = true, Font = new Font("Segoe UI", 10f), Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Color.Transparent };
            // Suchfeld: zeigt nur Einträge, deren Spielname oder Pfad den Text enthält
            // (gleicher Rahmen und gleiche Höhe wie das Auswahlfeld „Profil“ daneben, mit Lupe)
            _filter = new ModernTextBox(k) { Width = S(220), Anchor = AnchorStyles.Top | AnchorStyles.Right, SearchIcon = true, CueText = "Suchen" };
            _filter.TextChanged += (s, e) => FillList();
            _filter.SetTip(new ModernToolTip(), "Nur Einträge zeigen, deren Spielname oder Pfad den Text enthält");
            cardHead.Controls.AddRange(new Control[] { _filter, title, _summary, lbl, _profile }); // Suchfeld liegt bei wenig Platz über der Zählung
            cardHead.Layout += (s, e) =>
            {
                _profile.Location = new Point(cardHead.Width - _profile.Width, S(8));
                lbl.Location = new Point(_profile.Left - lbl.Width - S(10), S(12));
                _filter.Height = _profile.Height;
                _filter.Location = new Point(lbl.Left - _filter.Width - S(24), _profile.Top);
                _summary.Left = title.Right + S(16);
            };

            _icons = new ImageList { ImageSize = new Size(S(16), S(16)), ColorDepth = ColorDepth.Depth32Bit };
            _list = new BufferedListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                ShowItemToolTips = true, // voller Pfad beim Überfahren
                MultiSelect = true,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                SmallImageList = _icons,
                BackColor = Ui.Card,
                Font = new Font("Segoe UI", 9.5f)
            };
            ModernToolTip.AttachItems(_list); // voller Pfad beim Überfahren, im Programmdesign
            _list.Columns.Add("Spiel", S(230));
            _list.Columns.Add("Programm", S(200));
            _list.Columns.Add("Status", S(110));
            _list.Columns.Add("Pfad", S(600));
            _list.DoubleClick += (s, e) => OpenFolder();
            _list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            card.Controls.Add(_list);
            card.Controls.Add(cardHead);

            // ---------- Knöpfe ----------
            var actions = new Panel { Dock = DockStyle.Bottom, Height = S(72), BackColor = CBack };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = CBack, Padding = new Padding(0, S(16), 0, 0) };
            var add = new ModernButton("Spiel hinzufügen", '\uE710', true, k);
            add.Click += (s, e) => AddGame();
            var remove = new ModernButton("Entfernen", '\uE74D', false, k) { Tag = "sel" };
            remove.Click += (s, e) => RemoveSelected();
            _moveButton = new ModernButton("In Profil verschieben  \u25BE", '\uE8DE', false, k) { Tag = "sel" };
            _moveButton.Click += (s, e) => ShowMoveMenu();
            var removeMissing = new ModernButton("Alle fehlenden entfernen", '\uE894', false, k);
            removeMissing.Click += (s, e) => RemoveMissing();
            var open = new ModernButton("Ordner öffnen", '\uE838', false, k) { Tag = "sel" };
            open.Click += (s, e) => OpenFolder();
            buttons.Controls.AddRange(new Control[] { add, remove, _moveButton, removeMissing, open });
            actions.Controls.Add(buttons);
            ButtonRow.Fit(actions, buttons, null); // schmal: Knöpfe nur mit Symbol („Speichern“ und „Abbrechen“ hat das Hauptfenster)

            // ---------- Hinweis ----------
            var info = new InfoBanner("Änderungen werden erst mit „Speichern“ übernommen – alphabetisch sortiert, ohne Doppelte und mit vorheriger Sicherung (Einstellungen > Sicherungen öffnen).\n" +
                "Doppelklick auf ein Spiel öffnet seinen Ordner, Entf entfernt die markierten Einträge.", k) { Dock = DockStyle.Bottom };

            body.Controls.Add(card);
            body.Controls.Add(actions);
            body.Controls.Add(info);

            Controls.Add(body);
            Controls.Add(head);
            Controls.Add(new SignatureFooter(k));
            Reload();
            Resize += (s, e) => StretchPathColumn();
            Load += (s, e) => FitWindowWidth();
            Theme.Apply(this);
        }

        // ================================================================== Spaltenbreiten

        private readonly int[] _contentWidth = new int[4];

        /// <summary>Spaltenbreite = breitester Inhalt (mindestens Überschrift), ohne Flackern.</summary>
        private void FitColumns()
        {
            if (_list.Columns.Count < 4) return;
            var w = ListViewHelper.Fit(_list, S(12));
            Array.Copy(w, _contentWidth, Math.Min(w.Length, _contentWidth.Length));
            StretchPathColumn();
        }

        /// <summary>
        /// Spalten an die Breite anpassen, ohne dass die Liste waagrecht scrollt: Passt alles, bekommen die Spalten ihre
        /// volle Breite und "Pfad" den Rest. Sonst werden "Spiel" und "Programm" begrenzt und "Pfad" wird gekürzt
        /// (mit „…“, der volle Pfad steht im Tooltip).
        /// </summary>
        private void StretchPathColumn()
        {
            if (_list.Columns.Count < 4 || _contentWidth[0] == 0) return;
            int avail = _list.ClientSize.Width - 1;
            int game = _contentWidth[0], prog = _contentWidth[1], status = _contentWidth[2];
            if (game + prog + status + _contentWidth[3] > avail)
            {
                game = Math.Min(game, S(280));
                prog = Math.Min(prog, S(200));
            }
            int path = Math.Max(S(160), avail - game - prog - status);
            _list.BeginUpdate();
            if (_list.Columns[0].Width != game) _list.Columns[0].Width = game;
            if (_list.Columns[1].Width != prog) _list.Columns[1].Width = prog;
            if (_list.Columns[2].Width != status) _list.Columns[2].Width = status;
            if (_list.Columns[3].Width != path) _list.Columns[3].Width = path;
            _list.EndUpdate();
        }

        /// <summary>Fenster beim Öffnen so breit machen, dass alle Spalten passen (höchstens Bildschirmbreite).</summary>
        private void FitWindowWidth()
        {
            if (!TopLevel) { StretchPathColumn(); return; } // als Seite im Hauptfenster: Breite gibt das Fenster vor
            int needed = _contentWidth.Sum() + SystemInformation.VerticalScrollBarWidth + S(4);
            int extra = Width - _list.ClientSize.Width;   // Rahmen, Abstände, Liste-Rand
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Width = Math.Min(Math.Max(Width, needed + extra), area.Width - S(24));
            WindowPlacement.Center(this);
            StretchPathColumn();
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _instance = null;
            base.OnFormClosed(e);
        }

        // ================================================================== Daten

        private sealed class ProfileItem
        {
            public DimmingProfile Profile;
            public override string ToString() { return Profile.Title + "   (" + Profile.Patterns.Count + ")"; }
        }

        private void Reload()
        {
            string selected = _profile.SelectedItem is ProfileItem ? ((ProfileItem)_profile.SelectedItem).Profile.Name : null;
            _profile.Items.Clear();
            foreach (var p in _config().Profiles.Where(x => x.IsAutomatic))
                _profile.Items.Add(new ProfileItem { Profile = WithPending(p) });
            if (_profile.Items.Count == 0) return;
            int idx = 0;
            for (int i = 0; i < _profile.Items.Count; i++)
                if (((ProfileItem)_profile.Items[i]).Profile.Name == selected) idx = i;
            _profile.SelectedIndex = idx;
            FillList();
        }

        /// <summary>Profil mit den noch nicht gespeicherten Änderungen (nur für die Anzeige).</summary>
        private DimmingProfile WithPending(DimmingProfile p)
        {
            List<string> paths;
            if (!_pending.TryGetValue(p.Name, out paths)) return p;
            return new DimmingProfile
            {
                Name = p.Name,
                DisplayName = p.DisplayName,
                IsUndimmed = p.IsUndimmed,
                AutoApply = p.AutoApply,
                ForegroundTracker = p.ForegroundTracker,
                MouseTracker = p.MouseTracker,
                Patterns = paths.Select(x => new ProcessPattern(x)).ToList()
            };
        }

        /// <summary>Aktuelle Pfade eines Profils einschließlich der gesammelten Änderungen.</summary>
        private List<string> PathsOf(DimmingProfile p)
        {
            List<string> paths;
            return _pending.TryGetValue(p.Name, out paths) ? paths.ToList() : p.Patterns.Select(x => x.Raw).ToList();
        }

        private DimmingProfile CurrentProfile
        {
            get
            {
                var it = _profile.SelectedItem as ProfileItem;
                return it == null ? null : it.Profile;
            }
        }

        private void FillList()
        {
            var p = CurrentProfile;
            _list.BeginUpdate();
            _list.Items.Clear();
            if (p == null) { _list.EndUpdate(); return; }

            int found = 0, missing = 0, shown = 0;
            string filter = _filter == null ? "" : _filter.Text.Trim();
            _missingAll.Clear();
            foreach (var pat in p.Patterns.OrderBy(x => GameName(x.Expanded), StringComparer.CurrentCultureIgnoreCase))
            {
                var e = new Entry { Raw = pat.Raw, State = Check(pat) };
                if (e.State == EntryState.Found) found++;
                if (e.State == EntryState.Missing) { missing++; _missingAll.Add(pat.Raw); }
                if (filter.Length > 0 && GameName(pat.Expanded).IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) < 0
                    && pat.Raw.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                shown++;

                string status;
                Color color;
                switch (e.State)
                {
                    case EntryState.Found: status = "✓ gefunden"; color = COk; break;
                    case EntryState.Missing: status = "✗ fehlt"; color = CMissing; break;
                    case EntryState.Wildcard: status = "Platzhalter"; color = CInfo; break;
                    case EntryState.FileNameOnly: status = "jeder Ordner"; color = CInfo; break;
                    default: status = "nicht prüfbar"; color = CMuted; break;
                }
                var item = new ListViewItem(GameName(pat.Expanded)) { Tag = e, UseItemStyleForSubItems = false, ToolTipText = pat.Raw };
                item.SubItems.Add(Path.GetFileName(pat.Expanded));
                var st = item.SubItems.Add(status);
                st.ForeColor = color;
                var pathSub = item.SubItems.Add(pat.Raw);
                pathSub.ForeColor = CMuted;
                if (e.State == EntryState.Missing) item.ForeColor = CMuted;
                item.ImageKey = IconKey(pat.Expanded, e.State == EntryState.Found);
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            FitColumns();
            _summary.Text = string.Format("{0} Einträge   ·   {1} gefunden   ·   {2} fehlen", p.Patterns.Count, found, missing)
                + (filter.Length > 0 ? "   ·   " + shown + " angezeigt" : "");
            _summary.ForeColor = missing > 0 ? CMissing : CMuted;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool sel = _list.SelectedItems.Count > 0;
            foreach (Control c in _moveButton.Parent.Controls)
                if ((c.Tag as string) == "sel") c.Enabled = sel;
        }

        private static EntryState Check(ProcessPattern p)
        {
            if (p.HasWildcards) return EntryState.Wildcard;
            if (p.Expanded.IndexOf('\\') < 0) return EntryState.FileNameOnly;
            if (p.Expanded.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0) return EntryState.NotCheckable;
            if (Demo.Active) return EntryState.Found; // Beispieldaten für die Screenshots
            try { return File.Exists(p.Expanded) ? EntryState.Found : EntryState.Missing; }
            catch { return EntryState.NotCheckable; }
        }

        /// <summary>Spielname aus dem Ordner ableiten (technische Unterordner wie Win64, bin, x64 überspringen).</summary>
        // Ordner, unter denen die Spiele liegen. Der Spielname ist der erste Ordner danach.
        private static readonly string[] LibraryMarkers =
        {
            "\\steamapps\\common\\", "\\Spiele\\", "\\Games\\", "\\Epic Games\\", "\\GOG Galaxy\\Games\\",
            "\\XboxGames\\", "\\Ubisoft Game Launcher\\games\\", "\\EA Games\\", "\\Origin Games\\",
            "\\Program Files (x86)\\", "\\Program Files\\"
        };

        /// <summary>
        /// Spielname = erster Ordner unter dem Spiele-Verzeichnis (C:\Spiele\..., steamapps\common\... usw.),
        /// damit Unterordner wie SP, SYSTEM oder Binaries\Win64 keine Rolle spielen.
        /// </summary>
        internal static string GameName(string path)
        {
            try
            {
                // Microsoft Store: ...\WindowsApps\Microsoft.SunriseBaseGame_1.0_x64__xyz\Game.exe
                int wa = path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);
                if (wa >= 0)
                {
                    string pkg = path.Substring(wa + 13).Split('\\')[0];
                    int us = pkg.IndexOf('_');
                    if (us > 0) pkg = pkg.Substring(0, us);
                    int dot = pkg.LastIndexOf('.');
                    if (dot >= 0 && dot < pkg.Length - 1) pkg = pkg.Substring(dot + 1);
                    return pkg;
                }

                int best = -1, bestLen = 0;
                foreach (var m in LibraryMarkers)
                {
                    int i = path.LastIndexOf(m, StringComparison.OrdinalIgnoreCase);
                    if (i > best) { best = i; bestLen = m.Length; }
                }
                if (best >= 0)
                {
                    string rest = path.Substring(best + bestLen);
                    int slash = rest.IndexOf('\\');
                    if (slash > 0) return rest.Substring(0, slash);
                }

                // Sonst: nächster Ordner, der kein technischer Unterordner ist
                string dir = Path.GetDirectoryName(path);
                while (!string.IsNullOrEmpty(dir))
                {
                    string name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name)) break;
                    if (!GenericFolders.Contains(name)) return name;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return Path.GetFileNameWithoutExtension(path);
        }

        private string IconKey(string path, bool exists)
        {
            string key = exists ? path.ToLowerInvariant() : "?";
            if (_icons.Images.ContainsKey(key)) return key;
            Image img = null;
            if (exists)
            {
                try
                {
                    using (var ico = Icon.ExtractAssociatedIcon(path))
                        if (ico != null) img = new Icon(ico, _icons.ImageSize).ToBitmap();
                }
                catch { }
            }
            if (img == null)
            {
                var bmp = new Bitmap(_icons.ImageSize.Width, _icons.ImageSize.Height);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(Color.FromArgb(219, 219, 219)))
                        g.FillEllipse(b, 2, 2, bmp.Width - 4, bmp.Height - 4);
                }
                img = bmp;
                key = "?";
                if (_icons.Images.ContainsKey(key)) return key;
            }
            _icons.Images.Add(key, img);
            return key;
        }

        // ================================================================== Aktionen

        private List<string> CurrentPaths()
        {
            return CurrentProfile.Patterns.Select(x => x.Raw).ToList();
        }

        private List<string> SelectedPaths()
        {
            return _list.SelectedItems.Cast<ListViewItem>().Select(i => ((Entry)i.Tag).Raw).ToList();
        }

        /// <summary>Änderung vormerken (übernommen wird erst mit "Speichern").</summary>
        private void Save(string profileName, List<string> paths)
        {
            _pending[profileName] = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Reload();
        }

        // ---------- ISettingsPage: das Hauptfenster speichert alle Seiten gemeinsam ----------

        public bool HasChanges { get { return _pending.Count > 0 || _pendingIgnore.Count > 0; } }

        public string CheckInput() { return null; }

        /// <summary>Alle gesammelten Änderungen speichern; Rückgabe null = gespeichert, sonst die Fehlermeldung.</summary>
        public string SaveAll()
        {
            try
            {
                if (_pendingIgnore.Count > 0) { _ignore(_pendingIgnore.ToList()); _pendingIgnore.Clear(); }
                foreach (var name in _pending.Keys.ToList())
                {
                    _save(name, _pending[name]);
                    _pending.Remove(name);
                }
            }
            catch (Exception ex)
            {
                Reload();
                return "Speichern fehlgeschlagen: " + ex.Message;
            }
            return null;
        }

        private void AddGame()
        {
            var p = CurrentProfile;
            if (p == null) return;
            using (var dlg = new OpenFileDialog
            {
                Title = "Spiel für \"" + p.Title + "\" auswählen",
                Filter = "Programme (*.exe)|*.exe",
                Multiselect = true,
                InitialDirectory = Directory.Exists(@"C:\Spiele") ? @"C:\Spiele" : ""
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var paths = CurrentPaths();
                paths.AddRange(dlg.FileNames);
                Save(p.Name, paths);
                SelectPaths(dlg.FileNames);
            }
        }

        private void RemoveSelected()
        {
            var p = CurrentProfile;
            var sel = SelectedPaths();
            if (p == null || sel.Count == 0) return;
            string what = sel.Count == 1 ? "\"" + Path.GetFileName(sel[0]) + "\"" : sel.Count + " Einträge";
            if (MessageBox.Show(this, what + " aus \"" + p.Title + "\" entfernen?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var set = new HashSet<string>(sel, StringComparer.OrdinalIgnoreCase);
            Save(p.Name, CurrentPaths().Where(x => !set.Contains(x)).ToList());
        }

        private void SearchMissing()
        {
            var p = CurrentProfile;
            if (p == null) return;
            var missing = _missingAll.ToList(); // alle fehlenden, auch vom Suchfeld ausgeblendete
            var cfg = _config();
            var roots = GameScan.Roots(cfg, missing);
            if (roots.Count == 0)
            {
                MessageBox.Show(this, "Der Ordner C:\\Spiele wurde nicht gefunden.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string profileName = p.Name, profileTitle = p.Title;
            _searchButton.Enabled = false;
            _summary.ForeColor = CMuted;
            _summary.Text = "Suche in " + string.Join(", ", roots) + " ...";
            UseWaitCursor = true;
            var ui = System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext();
            System.Threading.Tasks.Task.Factory.StartNew(() => GameScan.Run(roots, cfg, missing, n =>
                    BeginInvoke((Action)(() => _summary.Text = "Suche in " + string.Join(", ", roots) + " ...   " + n + " Ordner durchsucht"))))
                .ContinueWith(t =>
                {
                    if (IsDisposed) return;
                    UseWaitCursor = false;
                    _searchButton.Enabled = true;
                    FillList();
                    if (t.Exception != null)
                    {
                        MessageBox.Show(this, "Suche fehlgeschlagen:\r\n" + t.Exception.GetBaseException().Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    var results = t.Result;
                    if (!results.Any(r => r.Candidates.Count > 0))
                    {
                        MessageBox.Show(this, "Keine neuen Spiele und keine fehlenden Programme gefunden.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    using (var dlg = new GameScanDialog(results, roots, profileTitle, Icon))
                    {
                        if (dlg.ShowDialog(this) != DialogResult.OK) return;
                        if (dlg.Ignore.Count > 0) _pendingIgnore.AddRange(dlg.Ignore); // wird beim Speichern übernommen
                        if (dlg.Replacements.Count == 0 && dlg.Additions.Count == 0) { Reload(); return; }
                        var target = _config().Find(profileName);
                        if (target == null) return;
                        var paths = PathsOf(target)
                            .Select(x => dlg.Replacements.ContainsKey(x) ? dlg.Replacements[x] : x).ToList();
                        paths.AddRange(dlg.Additions);
                        Save(profileName, paths);
                        SelectPaths(dlg.Replacements.Values.Concat(dlg.Additions));
                    }
                }, ui);
        }

        private void RemoveMissing()
        {
            var p = CurrentProfile;
            if (p == null) return;
            var missing = _missingAll.ToList(); // alle fehlenden, auch vom Suchfeld ausgeblendete
            if (missing.Count == 0)
            {
                MessageBox.Show(this, "Alle Programme in diesem Profil sind vorhanden.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string preview = string.Join("\r\n", missing.Take(15).Select(x => "  " + x));
            if (missing.Count > 15) preview += "\r\n  ... und " + (missing.Count - 15) + " weitere";
            if (MessageBox.Show(this, missing.Count + " nicht gefundene Einträge aus \"" + p.Title + "\" entfernen?\r\n\r\n" + preview +
                    "\r\n\r\nTipp: Liegt ein Spiel nur auf einem gerade nicht angeschlossenen Laufwerk, besser behalten.",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var set = new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase);
            Save(p.Name, CurrentPaths().Where(x => !set.Contains(x)).ToList());
        }

        /// <summary>Menü "In Profil verschieben" im gleichen Stil wie Tray- und Kontextmenüs.</summary>
        private void ShowMoveMenu()
        {
            var mb = new ModernMenuBuilder(this);
            var menu = mb.CreateMenu();
            menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
            var cur = CurrentProfile;
            foreach (var p in _config().Profiles.Where(x => x.IsAutomatic && cur != null && x.Name != cur.Name))
            {
                var target = p;
                menu.Items.Add(mb.Item(target.Title, '\0', () => MoveSelected(target)));
            }
            if (menu.Items.Count == 0) menu.Items.Add(mb.Info("Kein anderes Profil"));
            MenuHelper.ShowOnScreen(menu, _moveButton, new Point(0, _moveButton.Height), false);
        }

        private void MoveSelected(DimmingProfile target)
        {
            var src = CurrentProfile;
            var sel = SelectedPaths();
            if (src == null || sel.Count == 0) return;
            var set = new HashSet<string>(sel, StringComparer.OrdinalIgnoreCase);
            var targetPaths = PathsOf(target);
            targetPaths.AddRange(sel);
            _pending[target.Name] = targetPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Save(src.Name, CurrentPaths().Where(x => !set.Contains(x)).ToList());
        }

        private void SelectPaths(IEnumerable<string> paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem i in _list.Items)
            {
                if (set.Contains(((Entry)i.Tag).Raw))
                {
                    i.Selected = true;
                    i.EnsureVisible();
                }
            }
            _list.Focus();
        }

        private void OpenFolder()
        {
            var sel = SelectedPaths();
            if (sel.Count == 0) return;
            string path = Environment.ExpandEnvironmentVariables(sel[0]);
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else
                {
                    string dir = Path.GetDirectoryName(path);
                    while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrEmpty(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
                }
            }
            catch { }
        }
    }
}
