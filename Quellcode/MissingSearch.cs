// Monitor Dimmer – Suche nach neuen und fehlenden Spielen
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace MonitorDimmer
{
    /// <summary>
    /// Durchsucht die Spiele-Ordner einmal und liefert
    /// a) neue Pfade für fehlende Einträge (Suche nach Dateinamen) und
    /// b) Spielordner, zu denen es noch keinen Eintrag gibt, mit der wahrscheinlichen Haupt-exe.
    /// </summary>
    internal static class GameScan
    {
        public enum Kind { Missing, New }

        public sealed class Result
        {
            public Kind Kind;
            public string Name;          // Spielname
            public string OldPath;       // nur bei Missing
            public string Folder;        // nur bei New
            public List<string> Candidates = new List<string>();
        }

        private static readonly HashSet<string> SkipFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$RECYCLE.BIN", "System Volume Information", "shadercache", "downloading", "temp", "workshop",
            "_CommonRedist", "CommonRedist", "Redist", "Redistributables", "DirectX", "DXSETUP", "vcredist", "Support",
            "__Installer", "Installer", "_Installer", "EasyAntiCheat", "EasyAntiCheat_EOS", "BattlEye", "PunkBuster",
            "CrashReportClient", "ThirdParty", "Extras", "dotNetFx", "PhysX", "Prerequisites", "Prereqs"
        };

        // Programme, die sicher kein Spiel sind
        private static readonly Regex NotAGame = new Regex(
            "unins|setup|install|redist|dxsetup|directx|oalinst|physx|dotnet|prereq|easyanticheat|eac_|beservice|battleye|" +
            "crash|report|uploader|cleanup|touchup|activation|cefprocess|cefsharp|updater|patcher|helper|overlay|pnkbstr|punkbuster|" +
            "dxdiag|vc_|vcredist|quicksfv|unrar|7z|uninst|register|regsvr|webview|subprocess|dumper|bugsplat|sentry|" +
            "unitycrash|errorhandler|anticheat|ezfrd|eossdk|steamservice|xinput",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Ordner, die durchsucht werden: aus der Konfiguration, sonst C:\Spiele, plus X:\Spiele aus den fehlenden Pfaden.</summary>
        public static List<string> Roots(AppConfig cfg, IEnumerable<string> missing)
        {
            var roots = new List<string>(cfg.GameSearchFolders.Count > 0 ? cfg.GameSearchFolders : new List<string> { @"C:\Spiele" });
            foreach (var m in missing)
            {
                string p = Environment.ExpandEnvironmentVariables(m);
                if (p.Length > 3 && p[1] == ':')
                {
                    int i = p.IndexOf('\\', 3);
                    if (i > 0)
                    {
                        string r = p.Substring(0, i);
                        if (!roots.Any(x => string.Equals(x.TrimEnd('\\'), r, StringComparison.OrdinalIgnoreCase)) && SafeDirExists(r))
                            roots.Add(r);
                    }
                }
            }
            return roots.Select(x => Environment.ExpandEnvironmentVariables(x).TrimEnd('\\')).Where(SafeDirExists)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Spielordner: Unterordner der Wurzeln; enthält einer davon steamapps\common, dann dessen Unterordner.</summary>
        private static List<string> GameFolders(List<string> roots)
        {
            var result = new List<string>();
            foreach (var root in roots)
            {
                foreach (var d in SafeDirs(root))
                {
                    if (SkipFolders.Contains(Path.GetFileName(d))) continue;
                    string common = FindSteamCommon(d);
                    if (common != null)
                    {
                        result.AddRange(SafeDirs(common));
                        continue;
                    }
                    result.Add(d);
                }
                string rootCommon = FindSteamCommon(root);
                if (rootCommon != null) result.AddRange(SafeDirs(rootCommon));
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string FindSteamCommon(string dir)
        {
            foreach (var s in SafeDirs(dir))
            {
                if (!Path.GetFileName(s).Equals("steamapps", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var c in SafeDirs(s))
                    if (Path.GetFileName(c).Equals("common", StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }

        public static List<Result> Run(List<string> roots, AppConfig cfg, List<string> missing, Action<int> progress)
        {
            // ---------- 1. Alle exe-Dateien einmal einsammeln ----------
            var allExe = new List<string>();
            int folders = 0;
            foreach (var root in roots)
            {
                var stack = new Stack<string>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    string dir = stack.Pop();
                    folders++;
                    if (progress != null && folders % 200 == 0) progress(folders);
                    try
                    {
                        allExe.AddRange(Directory.EnumerateFiles(dir, "*.exe"));
                        foreach (var d in Directory.EnumerateDirectories(dir))
                        {
                            try
                            {
                                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                            }
                            catch { continue; }
                            stack.Push(d);
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
            if (progress != null) progress(folders);

            var results = new List<Result>();

            // ---------- 2. Fehlende: nach Dateinamen ----------
            var byName = allExe.GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var m in missing)
            {
                string expanded = Environment.ExpandEnvironmentVariables(m);
                var r = new Result { Kind = Kind.Missing, OldPath = m, Name = GameListForm.GameName(expanded) };
                List<string> list;
                if (byName.TryGetValue(Path.GetFileName(expanded), out list))
                    r.Candidates = list.OrderByDescending(c => PathScore(expanded, c)).ThenBy(c => c.Length).ToList();
                results.Add(r);
            }

            // ---------- 3. Neue Spiele ----------
            var patterns = cfg.Profiles.SelectMany(p => p.Patterns).ToList();
            var knownPrefixes = patterns.Where(p => !p.HasWildcards && p.Expanded.IndexOf('\\') >= 0)
                .Select(p => p.Expanded).ToList();
            var ignored = new HashSet<string>(cfg.IgnoredGameFolders.Select(x => Environment.ExpandEnvironmentVariables(x).TrimEnd('\\')),
                StringComparer.OrdinalIgnoreCase);

            foreach (var folder in GameFolders(roots))
            {
                if (ignored.Contains(folder)) continue;
                string prefix = folder.TrimEnd('\\') + "\\";
                // Schon bekannt? (ein Eintrag liegt in diesem Ordner, auch ein fehlender mit gleichem Ordner)
                if (knownPrefixes.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;

                var exes = allExe.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (exes.Count == 0) continue;
                // Platzhalter- oder Dateinamen-Einträge, die hier schon greifen
                if (exes.Any(f => patterns.Any(p => p.Matches(f, Path.GetFileName(f))))) continue;
                // Fehlender Eintrag, der durch eine Datei hier ersetzt würde -> nicht doppelt vorschlagen
                if (results.Any(r => r.Kind == Kind.Missing && r.Candidates.Any(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))) continue;

                string name = Path.GetFileName(folder);
                var candidates = exes.Where(f => !InSkippedFolder(f, prefix)).ToList();
                var games = candidates.Where(f => !NotAGame.IsMatch(Path.GetFileNameWithoutExtension(f))).ToList();
                if (games.Count == 0) continue;
                results.Add(new Result
                {
                    Kind = Kind.New,
                    Name = name,
                    Folder = folder,
                    Candidates = games.OrderByDescending(f => ExeScore(f, prefix, name)).Take(10).ToList()
                });
            }
            return results;
        }

        private static bool InSkippedFolder(string file, string prefix)
        {
            string rel = file.Substring(prefix.Length);
            var parts = rel.Split('\\');
            for (int i = 0; i < parts.Length - 1; i++)
                if (SkipFolders.Contains(parts[i])) return true;
            return false;
        }

        /// <summary>Wie wahrscheinlich ist diese exe das eigentliche Spiel?</summary>
        private static int ExeScore(string file, string prefix, string folderName)
        {
            string n = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            string rel = file.Substring(prefix.Length).ToLowerInvariant();
            int score = 0;
            if (n.Contains("shipping")) score += 30;
            if (rel.Contains("binaries\\win64\\")) score += 15;
            if (n.Contains("server") || n.Contains("editor") || n.Contains("dedicated") || n.Contains("tool")) score -= 40;
            if (n.Contains("launcher") || n.Contains("config") || n.Contains("settings") || n.Contains("benchmark")) score -= 20;
            if (n.Contains("x86") || n.Contains("32")) score -= 3;
            if (n.Contains("dx12") || n.Contains("dx11") || n.Contains("avx")) score -= 2;
            score -= (rel.Split('\\').Length - 1) * 2;
            try { score += (int)Math.Min(60, new FileInfo(file).Length / (1024 * 1024) / 2); } catch { }
            // Gemeinsame Wortteile mit dem Ordnernamen (z. B. "SnowRunner" / "snowrunner.exe")
            var words = Regex.Split(folderName.ToLowerInvariant(), "[^a-z0-9]+").Where(w => w.Length >= 3).ToList();
            string compact = Regex.Replace(folderName.ToLowerInvariant(), "[^a-z0-9]", "");
            if (compact.Length >= 3 && (compact.Contains(Regex.Replace(n, "[^a-z0-9]", "")) || Regex.Replace(n, "[^a-z0-9]", "").Contains(compact))) score += 25;
            score += words.Count(w => n.Contains(w)) * 8;
            return score;
        }

        /// <summary>Wie ähnlich sind sich alter und neuer Pfad (gemeinsame Ordnernamen, ähnlicher Spielordner)?</summary>
        private static int PathScore(string oldPath, string candidate)
        {
            var a = Segments(oldPath);
            var b = Segments(candidate);
            int score = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count() * 10;
            foreach (var x in a)
                foreach (var y in b)
                    if (x.Length > 3 && y.Length > 3 && !string.Equals(x, y, StringComparison.OrdinalIgnoreCase) &&
                        (y.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0 || x.IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0))
                        score += 4;
            if (a.Count == b.Count) score += 2;
            return score;
        }

        private static List<string> Segments(string path)
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            return dir.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.GetDirectories(dir); }
            catch { return new string[0]; }
        }

        private static bool SafeDirExists(string p)
        {
            try { return Directory.Exists(p); } catch { return false; }
        }
    }

    /// <summary>Ergebnisfenster: fehlende (neuer Pfad) und neue Spiele zur Übernahme auswählen.</summary>
    internal sealed class GameScanDialog : Form
    {
        private readonly DataGridView _grid;
        private float k = 1f;

        /// <summary>Alte Pfade → neue Pfade (fehlende).</summary>
        public Dictionary<string, string> Replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Neu hinzuzufügende Pfade.</summary>
        public List<string> Additions = new List<string>();
        /// <summary>Ordner, die nicht mehr vorgeschlagen werden sollen.</summary>
        public List<string> Ignore = new List<string>();

        public GameScanDialog(List<GameScan.Result> results, List<string> roots, string profileTitle, Icon icon)
        {
            Text = "Monitor Dimmer - Neue und fehlende Spiele";
            if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable; // Dialog mit veränderbarer Größe: normaler Rahmen, ohne Minimieren
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(243, 243, 243);
            Font = new Font("Segoe UI", 9f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(S(1250), area.Width - S(40)), Math.Min(S(820), area.Height - S(80)));
            MinimumSize = new Size(S(820), S(480));

            var missing = results.Where(r => r.Kind == GameScan.Kind.Missing).ToList();
            var fresh = results.Where(r => r.Kind == GameScan.Kind.New).ToList();
            int foundMissing = missing.Count(r => r.Candidates.Count > 0);

            var head = new HeaderPanel("Neue und fehlende Spiele",
                "Ziel: \"" + profileTitle + "\"   ·   Durchsucht: " + string.Join(", ", roots), k);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                EditMode = DataGridViewEditMode.EditOnEnter,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            ModernToolTip.AttachCells(_grid); // Hinweise im Programmdesign statt des Windows-Hinweises
            _grid.RowTemplate.Height = S(28);
            // Kopfzeile: Höhe passend zur Skalierung, gleicher Stil wie die anderen Fenster
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(97, 97, 97);
            _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 8.5f);
            _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(S(4), S(6), S(4), S(6));
            _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _grid.GridColor = Color.FromArgb(234, 234, 234);
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.DefaultCellStyle.Padding = new Padding(S(4), 0, S(4), 0);
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(237, 237, 237); // Windows-11-Markierung (dezent grau)
            _grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(27, 27, 27);
            var colUse = new DataGridViewCheckBoxColumn { HeaderText = "Übernehmen", FillWeight = 9, ToolTipText = "Klicken: alle an- oder abhaken" };
            var colKind = new DataGridViewTextBoxColumn { HeaderText = "Art", ReadOnly = true, FillWeight = 8 };
            var colName = new DataGridViewTextBoxColumn { HeaderText = "Spiel", ReadOnly = true, FillWeight = 20 };
            var colOld = new DataGridViewTextBoxColumn { HeaderText = "Bisher", ReadOnly = true, FillWeight = 25 };
            var colNew = new DataGridViewComboBoxColumn { HeaderText = "Programm", FillWeight = 38, FlatStyle = FlatStyle.Flat };
            var colIgnore = new DataGridViewCheckBoxColumn { HeaderText = "Nicht mehr vorschlagen", FillWeight = 12 };
            _grid.Columns.AddRange(new DataGridViewColumn[] { colUse, colKind, colName, colOld, colNew, colIgnore });
            colOld.DefaultCellStyle.ForeColor = Color.FromArgb(97, 97, 97);

            var ordered = missing.Where(r => r.Candidates.Count > 0).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .Concat(fresh.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
                .Concat(missing.Where(r => r.Candidates.Count == 0).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase));
            foreach (var r in ordered)
            {
                int i = _grid.Rows.Add();
                var row = _grid.Rows[i];
                row.Tag = r;
                bool isNew = r.Kind == GameScan.Kind.New;
                row.Cells[1].Value = isNew ? "Neu" : "Fehlt";
                row.Cells[1].Style.ForeColor = isNew ? Color.FromArgb(15, 123, 15) : Color.FromArgb(196, 43, 28);
                row.Cells[2].Value = r.Name;
                row.Cells[3].Value = isNew ? r.Folder : r.OldPath;
                var combo = (DataGridViewComboBoxCell)row.Cells[4];
                if (r.Candidates.Count > 0)
                {
                    foreach (var c in r.Candidates) combo.Items.Add(c);
                    combo.Value = r.Candidates[0];
                    row.Cells[0].Value = !isNew;
                }
                else
                {
                    combo.Items.Add("(nicht gefunden)");
                    combo.Value = "(nicht gefunden)";
                    combo.ReadOnly = true;
                    row.Cells[0].Value = false;
                    row.Cells[0].ReadOnly = true;
                    row.DefaultCellStyle.ForeColor = Color.FromArgb(157, 157, 157);
                }
                row.Cells[5].Value = false;
                if (!isNew) row.Cells[5].ReadOnly = true;
            }
            // Ein Haken bei "Nicht mehr vorschlagen" nimmt den Haken bei "Übernehmen" weg und umgekehrt
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _grid.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var row = _grid.Rows[e.RowIndex];
                if (e.ColumnIndex == 5 && IsChecked(row.Cells[5])) row.Cells[0].Value = false;
                if (e.ColumnIndex == 0 && IsChecked(row.Cells[0]) && !row.Cells[5].ReadOnly) row.Cells[5].Value = false;
                UpdateToggle();
            };
            _grid.DataError += (s, e) => { e.ThrowException = false; };

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(243, 243, 243), Padding = new Padding(S(28), 0, S(28), 0) };
            var card = new CardPanel(k) { Dock = DockStyle.Fill };
            var cardHead = new Panel { Dock = DockStyle.Top, Height = S(46), BackColor = Color.White };
            cardHead.Controls.Add(new Label
            {
                Text = TitleText(fresh.Count, foundMissing, missing.Count),
                Font = new Font("Segoe UI Semibold", 11f),
                ForeColor = Color.FromArgb(27, 27, 27),
                AutoSize = true,
                Location = new Point(0, S(8)),
                BackColor = Color.Transparent
            });
            card.Controls.Add(_grid);
            card.Controls.Add(cardHead);

            var actions = new Panel { Dock = DockStyle.Bottom, Height = S(72), BackColor = Color.FromArgb(243, 243, 243) };
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.FromArgb(243, 243, 243), Padding = new Padding(0, S(16), 0, 0) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, BackColor = Color.FromArgb(243, 243, 243), Padding = new Padding(0, S(16), 0, 0) };
            var cancel = new ModernButton("Abbrechen", '\0', false, k) { DialogResult = DialogResult.Cancel };
            var ok = new ModernButton("Übernehmen", '\uE73E', true, k) { Margin = new Padding(0) };
            ok.Click += (s, e) => { Collect(); DialogResult = DialogResult.OK; Close(); };
            _allNew = new ModernButton("Alle neuen anhaken", '\uE73A', false, k);
            var allNew = _allNew;
            allNew.Click += (s, e) =>
            {
                bool check = !AllNewChecked();
                _grid.EndEdit();
                foreach (DataGridViewRow row in _grid.Rows)
                    if (((GameScan.Result)row.Tag).Kind == GameScan.Kind.New && !IsChecked(row.Cells[5]))
                        row.Cells[0].Value = check;
                UpdateToggle();
            };
            // Klick auf die Überschrift "Übernehmen": alle Haken der Spalte umschalten
            _grid.ColumnHeaderMouseClick += (s, e) =>
            {
                if (e.ColumnIndex != 0) return;
                _grid.EndEdit();
                var rows = _grid.Rows.Cast<DataGridViewRow>().Where(r => !r.Cells[0].ReadOnly && !IsChecked(r.Cells[5])).ToList();
                bool check = rows.Any(r => !IsChecked(r.Cells[0]));
                foreach (var gridRow in rows) gridRow.Cells[0].Value = check;
                UpdateToggle();
            };
            buttons.Controls.Add(ok); // „Abbrechen“ links, „Übernehmen“ ganz rechts
            buttons.Controls.Add(cancel);
            left.Controls.Add(allNew);
            actions.Controls.Add(left);
            actions.Controls.Add(buttons);

            var info = new InfoBanner("Neue Spiele sind nicht vorausgewählt. Klick auf die Überschrift \"Übernehmen\" hakt alle an oder ab.\n" +
                "\"Nicht mehr vorschlagen\" merkt den Ordner und blendet ihn bei der nächsten Suche aus.", k) { Dock = DockStyle.Bottom };

            body.Controls.Add(card);
            body.Controls.Add(actions);
            body.Controls.Add(info);

            Controls.Add(body);
            Controls.Add(head);
            Controls.Add(new SignatureFooter(k));
            CancelButton = cancel;
            Shown += (s, e) => { _grid.ClearSelection(); _grid.CurrentCell = null; };
            Theme.Apply(this);
        }

        private static string TitleText(int fresh, int foundMissing, int missing)
        {
            var parts = new List<string>();
            parts.Add(fresh == 1 ? "1 neues Spiel" : fresh + " neue Spiele");
            if (missing > 0) parts.Add(foundMissing + " von " + missing + " fehlenden gefunden");
            return string.Join("  ·  ", parts);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        private ModernButton _allNew;

        private bool AllNewChecked()
        {
            var rows = _grid.Rows.Cast<DataGridViewRow>()
                .Where(r => ((GameScan.Result)r.Tag).Kind == GameScan.Kind.New && !IsChecked(r.Cells[5])).ToList();
            return rows.Count > 0 && rows.All(r => IsChecked(r.Cells[0]));
        }

        private void UpdateToggle()
        {
            if (_allNew != null) _allNew.Text = AllNewChecked() ? "Alle neuen abhaken" : "Alle neuen anhaken";
        }

        private static bool IsChecked(DataGridViewCell c)
        {
            return c.Value is bool && (bool)c.Value;
        }

        private void Collect()
        {
            _grid.EndEdit();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var r = (GameScan.Result)row.Tag;
                if (r.Kind == GameScan.Kind.New && IsChecked(row.Cells[5]))
                {
                    Ignore.Add(r.Folder);
                    continue;
                }
                if (r.Candidates.Count == 0 || !IsChecked(row.Cells[0])) continue;
                string chosen = row.Cells[4].Value as string;
                if (string.IsNullOrEmpty(chosen) || !r.Candidates.Contains(chosen)) continue;
                if (r.Kind == GameScan.Kind.Missing) Replacements[r.OldPath] = chosen;
                else Additions.Add(chosen);
            }
        }
    }
}
