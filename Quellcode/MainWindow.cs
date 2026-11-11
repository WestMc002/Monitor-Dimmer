// Monitor Dimmer – Hauptfenster mit Navigation (Monitore, Spieleliste, Aktionen, Zeitplan, Tastenkürzel)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MonitorDimmer
{
    /// <summary>Eine Seite des Hauptfensters. Die Seite ist ein bisheriges Fenster, das eingebettet angezeigt wird.</summary>
    internal sealed class PageDef
    {
        public string Text;
        public char Glyph;
        public Func<Form> Create;
        /// <summary>Änderungen der Seite speichern; Rückgabe null = gespeichert, sonst die Fehlermeldung.</summary>
        public Func<Form, string> Save;
        /// <summary>Seite wird angezeigt bzw. verlassen (z. B. Tastenkürzel abschalten, solange die Seite zu sehen ist).</summary>
        public Action Opened, Closed;
    }

    /// <summary>Seite mit Einstellungen: Das Hauptfenster speichert alle geänderten Seiten gemeinsam.</summary>
    internal interface ISettingsPage
    {
        /// <summary>Gibt es Änderungen, die noch nicht gespeichert sind?</summary>
        bool HasChanges { get; }
        /// <summary>Eingaben prüfen (vor dem Speichern): null = in Ordnung, sonst die Fehlermeldung.</summary>
        string CheckInput();
    }

    /// <summary>
    /// Ein Fenster für alle Ansichten (wie Wake on LAN): Navigation links, die Seite rechts, Footer unten.
    /// Reines Einstellungsfenster (globale Vorgabe „Einstellungsseiten“): Ein Seitenwechsel verwirft nichts, die
    /// Änderungen werden über alle Seiten gesammelt; „Speichern“ und „Abbrechen“ unten gelten für alle Seiten.
    /// Schließen mit ungespeicherten Änderungen fragt nach.
    /// </summary>
    internal sealed class MainWindow : Form
    {
        private readonly float k;
        private readonly List<PageDef> _pages;
        private readonly Dictionary<PageDef, NavButton> _buttons = new Dictionary<PageDef, NavButton>();
        private readonly Panel _nav, _navSpacer, _content;
        private readonly NavButton _navMenu, _navPin, _navSettings, _navQuit;
        private readonly Timer _navTimer = new Timer { Interval = 400 };
        private readonly Timer _navHover = new Timer { Interval = 400 }; // nicht angepinnt: nach 400 ms über der Navigation ausklappen
        private bool _menuOpen; // Menü „Extras“ offen: Navigation nicht einklappen
        private readonly SignatureFooter _footer;
        private readonly Panel _updateHost;  // Hinweisleiste oben, nur sichtbar bei neuer Version
        private readonly UpdateBar _updateBar;
        private Action _updateInstall, _updatePage, _updateLater;
        private bool _navOpen; // eingeklappt, aber vorübergehend ausgeklappt (liegt über dem Inhalt)
        private readonly Label _message;
        private readonly Timer _messageTimer = new Timer { Interval = 4000 }; // wie WindowGrid
        private readonly Panel _bar;          // unten: Meldung, „Abbrechen“, „Speichern“ für alle Seiten
        private readonly ModernButton _save, _cancel;
        private PageDef _current;
        private Form _view;
        /// <summary>Geöffnete Seiten bleiben beim Wechsel erhalten (mit ihren ungespeicherten Änderungen).</summary>
        private readonly Dictionary<PageDef, Form> _views = new Dictionary<PageDef, Form>();
        private bool _force;  // Schließen ohne Rückfrage (Programmende bzw. schon bestätigt)

        /// <param name="settings">"Extras" unten in der Navigation: zeigt das Menü am Knopf (null = ausblenden)</param>
        /// <param name="quit">"Beenden" unten in der Navigation: Programm ganz beenden (null = ausblenden)</param>
        public MainWindow(List<PageDef> pages, Icon icon, Action<Control> settings, Action quit)
        {
            _pages = pages;
            Theme.Refresh();
            Text = "Monitor Dimmer";
            if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.Sizable;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;

            // ---------- Navigation (wie Wake on LAN): ☰ klappt aus und ein, Stecknadel pinnt an ----------
            // angepinnt = immer offen und schiebt den Inhalt; eingeklappt = nur Symbole, ausgeklappt liegt sie über dem Inhalt
            _navSpacer = new Panel { Dock = DockStyle.Left, BackColor = Ui.Back };
            _nav = new Panel { BackColor = Ui.Back };
            _nav.Paint += (s, e) =>
            {
                if (_navOpen && !NavPinned)
                    using (var pen = new Pen(Ui.Border)) e.Graphics.DrawLine(pen, _nav.Width - 1, 0, _nav.Width - 1, _nav.Height);
            };
            _navMenu = new NavButton("", MenuHelper.Menu, k) { AccessibleName = "Navigation aus- bzw. einklappen" };
            _navMenu.Click += (s, e) =>
            {
                if (NavPinned) SetNavPinned(false);
                else { _navOpen = !_navOpen; ApplyNav(); }
            };
            _navPin = new NavButton("", MenuHelper.Pin, k) { IconOnly = true };
            _navPin.Click += (s, e) => SetNavPinned(!NavPinned);
            _nav.Controls.Add(_navMenu);
            _nav.Controls.Add(_navPin);
            var tip = new ModernToolTip();
            tip.SetToolTip(_navMenu, "Navigation aus- bzw. einklappen");
            _navTip = tip;
            foreach (var p in pages)
            {
                var page = p;
                var b = new NavButton(p.Text, p.Glyph, k);
                b.Click += (s, e) => { CloseNav(); ShowPage(page); };
                _nav.Controls.Add(b);
                _buttons[p] = b;
            }
            // unten: Extras (Zahnrad; reines Einstellungsfenster, darum nicht „Einstellungen“), darunter Beenden (Ein/Aus)
            _navSettings = new NavButton("Extras", MenuHelper.Settings, k) { Visible = settings != null };
            _navSettings.Click += (s, e) =>
            {
                if (settings == null) return;
                _navSettings.Selected = true; // ausgewählt, solange das Menü offen ist
                _menuOpen = true;
                settings(_navSettings);
            };
            _nav.Controls.Add(_navSettings);
            // komplett beenden (das X schließt nur das Fenster, das Programm läuft im Infobereich weiter)
            _navQuit = new NavButton("Beenden", MenuHelper.Power, k) { AccessibleName = "Beenden (auch im Infobereich)", Visible = quit != null };
            _navQuit.Click += (s, e) => { CloseNav(); if (quit != null) quit(); }; // die Rückfrage bei Änderungen stellt der Aufrufer (ConfirmDiscard)
            _nav.Controls.Add(_navQuit);
            _nav.Resize += (s, e) => LayoutNav();
            // nicht angepinnt: verweilt die Maus 400 ms auf der eingeklappten Navigation, klappt sie aus (Referenz Dock Pilot)
            EventHandler hoverStart = (s, e) => { if (!NavPinned && !_navOpen) { _navHover.Stop(); _navHover.Start(); } };
            _nav.MouseEnter += hoverStart;
            foreach (Control c in _nav.Controls) c.MouseEnter += hoverStart;
            _nav.MouseLeave += (s, e) => { if (!_nav.RectangleToScreen(_nav.ClientRectangle).Contains(Cursor.Position)) _navHover.Stop(); };
            _navHover.Tick += (s, e) =>
            {
                _navHover.Stop();
                if (NavPinned || _navOpen || !Visible) return;
                if (!_nav.RectangleToScreen(_nav.ClientRectangle).Contains(Cursor.Position)) return;
                _navOpen = true;
                ApplyNav();
            };
            _navTimer.Tick += (s, e) =>
            {
                // ausgeklappt, nicht angepinnt: wieder einklappen, sobald die Maus die Navigation verlässt (nicht, solange das Menü offen ist)
                if (!_navOpen || NavPinned || _nav.Capture || !Visible || _menuOpen) return;
                if (!_nav.RectangleToScreen(_nav.ClientRectangle).Contains(Cursor.Position)) { _navOpen = false; ApplyNav(); }
            };
            _navTimer.Start();

            // ---------- Inhalt und Meldungszeile ----------
            _content = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Back };
            // ---------- unten: „Abbrechen“ links, „Speichern“ ganz rechts, links davon die Meldung (grün) bzw. der Fehler (rot) ----------
            // Abstand nach oben 16 wie bisher auf den Seiten (Knopfzeile unter dem Hinweis), unten 8 bis zum Footer
            _bar = new Panel { Dock = DockStyle.Bottom, BackColor = Ui.Back, Padding = new Padding(S(28), S(16), S(28), S(8)) };
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, BackColor = Ui.Back };
            _cancel = new ModernButton("Abbrechen", '\0', false, k);
            _save = new ModernButton("Speichern", '\uE74E', true, k) { Margin = new Padding(0) };
            _cancel.Click += (s, e) => DiscardAll();
            _save.Click += (s, e) => SaveAll();
            right.Controls.Add(_save);
            right.Controls.Add(_cancel);
            _message = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Ui.Green,
                BackColor = Ui.Back,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, S(12), 0),
                AutoEllipsis = true,
                UseMnemonic = false
            };
            _bar.Controls.Add(_message);
            _bar.Controls.Add(right);
            _bar.Height = _bar.Padding.Vertical + _save.Height;
            _messageTimer.Tick += (s, e) => { _messageTimer.Stop(); _message.Text = ""; };
            CancelButton = _cancel;
            AcceptButton = _save;

            // Andockreihenfolge: Footer über die volle Breite, dann Platzhalter der Navigation, dann Inhalt;
            // die Navigation selbst ist nicht angedockt (liegt ausgeklappt über dem Inhalt)
            _footer = new SignatureFooter(k);

            // ---------- Hinweisleiste bei neuer Version (globale Vorgabe Updates) ----------
            _updateBar = new UpdateBar(k) { Dock = DockStyle.Fill };
            _updateBar.Install.Click += (s, e) => { if (_updateInstall != null) _updateInstall(); };
            _updateBar.Page.Click += (s, e) => { if (_updatePage != null) _updatePage(); };
            _updateBar.Later.Click += (s, e) => { HideUpdateBar(); if (_updateLater != null) _updateLater(); };
            _updateHost = new Panel { Dock = DockStyle.Top, Height = S(64), BackColor = Ui.Back, Padding = new Padding(S(28), S(14), S(28), S(2)), Visible = false };
            _updateHost.Controls.Add(_updateBar);

            Controls.Add(_content);
            Controls.Add(_bar);
            Controls.Add(_updateHost);
            Controls.Add(_navSpacer);
            Controls.Add(_footer);
            Controls.Add(_nav);
            _nav.BringToFront();
            Resize += (s, e) => ApplyNav();
            _footer.LocationChanged += (s, e) => ApplyNav();

            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(S(230 + 1120), area.Width - S(24)), Math.Min(S(820), area.Height - S(24)));
            MinimumSize = new Size(Math.Min(S(230 + 860), area.Width), Math.Min(S(600), area.Height));
            WindowPlacement.Center(this);
            Theme.Apply(this);
            ApplyNav();
        }

        private readonly ToolTip _navTip;

        // ---------- Navigation angepinnt (Datenordner\Configurations\Fenster.ini, Standard: an) ----------

        private static string WindowSettings { get { return System.IO.Path.Combine(AppData.Configurations, "Fenster.ini"); } }

        private bool? _pinned;

        /// <summary>Navigation angepinnt (einmal aus Fenster.ini gelesen).</summary>
        private bool NavPinned
        {
            get
            {
                if (_pinned == null) _pinned = ReadPinned();
                return _pinned.Value;
            }
        }

        private static bool ReadPinned()
        {
            {
                if (Demo.Active) return true; // Screenshots: immer ausgeklappt
                try
                {
                    if (System.IO.File.Exists(WindowSettings))
                        foreach (var line in System.IO.File.ReadAllLines(WindowSettings))
                            if (line.Trim().Equals("navpinned=false", StringComparison.OrdinalIgnoreCase)) return false;
                }
                catch { }
                return true;
            }
        }

        private void SetNavPinned(bool pinned)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(WindowSettings));
                System.IO.File.WriteAllLines(WindowSettings, new[] { "; Monitor Dimmer - Fenster", "navpinned=" + (pinned ? "true" : "false") });
            }
            catch { }
            _pinned = pinned;
            _navOpen = pinned;
            ApplyNav();
        }

        /// <summary>Ausgeklappte (nicht angepinnte) Navigation nach einer Auswahl wieder einklappen.</summary>
        private void CloseNav()
        {
            if (!NavPinned && _navOpen) { _navOpen = false; ApplyNav(); }
        }

        private void ApplyNav()
        {
            if (_nav == null || _footer == null || _navPin == null) return;
            bool pinned = NavPinned, open = pinned || _navOpen;
            int full = S(230), compact = S(68);
            _navSpacer.Width = pinned ? full : compact;
            _nav.SetBounds(0, 0, open ? full : compact, Math.Max(0, _footer.Top));
            _nav.BringToFront();
            _navPin.Visible = open;
            _navPin.Glyph = pinned ? MenuHelper.Unpin : MenuHelper.Pin;
            string pinText = pinned ? "Navigation lösen (einklappbar)" : "Navigation anpinnen";
            _navPin.AccessibleName = pinText;
            _navTip.SetToolTip(_navPin, pinText);
            _nav.Invalidate();
            _navPin.Invalidate();
            LayoutNav();
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        /// <summary>Klick auf die Versions-Plakette einer Seite: Info-Fenster (kleines Pop-up) über dem Hauptfenster öffnen.</summary>
        public static bool ShowInfoFrom(Control c)
        {
            while (c != null && !(c is MainWindow)) c = c.Parent;
            var w = c as MainWindow;
            if (w == null) return false;
            AboutDialog.ShowFor(w);
            return true;
        }

        /// <summary>Fenster zeigen (auch aus dem Infobereich) und die Seite öffnen.</summary>
        public void Open(PageDef page)
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            ShowPage(page);
            Activate();
        }

        public PageDef Current { get { return _current; } }

        /// <summary>Hinweisleiste "Neue Version x verfügbar – installiert ist y." mit Jetzt aktualisieren, Release-Seite, Später.</summary>
        public void ShowUpdateBar(string version, bool canInstall, string blocker, Action install, Action page, Action later)
        {
            _updateInstall = install;
            _updatePage = page;
            _updateLater = later;
            _updateBar.Text = "Neue Version " + version + " verfügbar – installiert ist " + Ui.Version + ".";
            _updateBar.Install.Enabled = canInstall;
            if (_navTip != null) _navTip.SetToolTip(_updateBar.Install, canInstall ? "Programm beenden, auf die neue Version aktualisieren und neu starten" : blocker);
            if (!_updateHost.Visible)
            {
                _updateHost.Visible = true;
                // Fenster um die Leiste vergrößern, soweit der Bildschirm reicht
                var area = Screen.FromControl(this).WorkingArea;
                if (WindowState == FormWindowState.Normal && Bottom + _updateHost.Height <= area.Bottom) Height += _updateHost.Height;
            }
        }

        public void HideUpdateBar()
        {
            if (!_updateHost.Visible) return;
            _updateHost.Visible = false;
            if (WindowState == FormWindowState.Normal && Height - _updateHost.Height >= MinimumSize.Height) Height -= _updateHost.Height;
        }

        /// <summary>Text der Leiste während des Downloads.</summary>
        public void SetUpdateBarText(string text) { _updateBar.Text = text; }

        /// <summary>Nach dem Schließen des Menüs: Markierung von "Extras" entfernen.</summary>
        public void SettingsMenuClosed()
        {
            _menuOpen = false;
            if (!_navSettings.IsDisposed) _navSettings.Selected = false;
            CloseNav();
        }

        private void LayoutNav()
        {
            if (_navQuit == null) return;
            int x = S(10), w = _nav.Width - S(20), h = S(40), y = S(12);
            _navMenu.SetBounds(x, y, S(48), h);
            _navPin.SetBounds(_nav.Width - S(10) - S(36), y + (h - S(36)) / 2, S(36), S(36));
            y += h + S(10);
            foreach (var p in _pages)
            {
                _buttons[p].SetBounds(x, y, w, h);
                y += h + S(4);
            }
            int bottom = _nav.Height - S(14);
            _navQuit.SetBounds(x, bottom - h, w, h);
            bottom -= h + S(4);
            _navSettings.SetBounds(x, bottom - h, w, h);
        }

        /// <summary>Seite zeigen. Die bisherige Seite bleibt mit ihren Änderungen erhalten (nichts wird verworfen).</summary>
        public void ShowPage(PageDef page)
        {
            if (_view != null && _current == page) { _view.Focus(); return; }
            HideView();
            if (_message.ForeColor != Ui.Red) _message.Text = ""; // ein Fehler bleibt stehen, bis er behoben ist
            Form f;
            if (!_views.TryGetValue(page, out f) || f.IsDisposed) f = CreateView(page);
            _current = page;
            _view = f;
            foreach (var kv in _buttons) kv.Value.Selected = kv.Key == page;
            Text = "Monitor Dimmer - " + page.Text;
            if (page.Opened != null) page.Opened();
            f.Visible = true;
            f.BringToFront();
            _content.PerformLayout(); // Seiten, die ihre Größe selbst setzen (Monitore), wieder einpassen
            f.Focus();
        }

        private Form CreateView(PageDef page)
        {
            var f = page.Create();
            _views[page] = f;
            f.TopLevel = false;
            f.FormBorderStyle = FormBorderStyle.None;
            f.ShowInTaskbar = false;
            f.MinimumSize = Size.Empty;
            f.MaximumSize = Size.Empty;
            // Footer hat das Hauptfenster; "Schließen" entfällt (Fenster schließt über das X)
            foreach (var footer in f.Controls.OfType<SignatureFooter>().ToList()) f.Controls.Remove(footer);
            foreach (var b in AllButtons(f))
                if (b.Text == "Schließen") b.Visible = false;
            f.Dock = DockStyle.Fill; // jede Seite füllt den Inhaltsbereich
            f.Visible = false;
            _content.Controls.Add(f);
            return f;
        }

        private static IEnumerable<Button> AllButtons(Control c)
        {
            foreach (Control child in c.Controls)
            {
                var b = child as Button;
                if (b != null) yield return b;
                foreach (var inner in AllButtons(child)) yield return inner;
            }
        }

        /// <summary>Sichtbare Seite ausblenden (sie bleibt erhalten).</summary>
        private void HideView()
        {
            var f = _view;
            if (f == null) return;
            var page = _current;
            _view = null;
            if (!f.IsDisposed) f.Visible = false;
            if (page != null && page.Closed != null) page.Closed();
        }

        /// <summary>Alle Seiten verwerfen (nach Speichern bzw. Abbrechen); die aktuelle Seite wird auf Wunsch frisch geöffnet.</summary>
        private void ResetViews(bool reopen)
        {
            var page = _current;
            HideView();
            foreach (var f in _views.Values.ToList())
            {
                _content.Controls.Remove(f);
                if (!f.IsDisposed) f.Dispose();
            }
            _views.Clear();
            _current = null;
            if (reopen && page != null && !IsDisposed) ShowPage(page);
        }

        /// <summary>Seiten mit ungespeicherten Änderungen, in der Reihenfolge der Navigation.</summary>
        private List<PageDef> ChangedPages()
        {
            var list = new List<PageDef>();
            foreach (var p in _pages)
            {
                Form f;
                if (!_views.TryGetValue(p, out f) || f.IsDisposed) continue;
                var page = f as ISettingsPage;
                if (page != null && page.HasChanges) list.Add(p);
            }
            return list;
        }

        public bool HasChanges { get { return ChangedPages().Count > 0; } }

        /// <summary>„Speichern“: erst alle geänderten Seiten prüfen, dann alle speichern. Bei einem Fehler die Seite zeigen.</summary>
        private void SaveAll()
        {
            var changed = ChangedPages();
            if (changed.Count == 0) { ShowMessage("Es gibt keine Änderungen.", true); return; }
            foreach (var p in changed)
            {
                string error = ((ISettingsPage)_views[p]).CheckInput();
                if (error == null) continue;
                ShowPage(p);
                ShowMessage(changed.Count > 1 ? p.Text + ": " + error : error, false);
                return;
            }
            foreach (var p in changed)
            {
                string error = p.Save != null ? p.Save(_views[p]) : null;
                if (error == null) continue;
                ShowPage(p);
                ShowMessage(error, false);
                return;
            }
            ResetViews(true); // Seiten frisch aus der gespeicherten Konfiguration
            ShowMessage(changed.Count == 1 ? changed[0].Text + " gespeichert." : "Gespeichert: " + string.Join(", ", changed.Select(x => x.Text)) + ".", true);
        }

        /// <summary>„Abbrechen“: verwirft die Änderungen aller Seiten ohne Rückfrage.</summary>
        private void DiscardAll()
        {
            bool changed = HasChanges;
            ResetViews(true);
            if (changed) ShowMessage("Änderungen verworfen.", true);
            else _message.Text = "";
        }

        /// <summary>
        /// Rückfrage bei ungespeicherten Änderungen (globale Vorgabe): „Nein“ ist vorausgewählt und bleibt auf der Seite.
        /// Ohne Änderungen keine Rückfrage. Rückgabe true = verwerfen bzw. nichts zu verwerfen.
        /// </summary>
        public bool ConfirmDiscard()
        {
            var changed = ChangedPages();
            if (changed.Count == 0) return true;
            // reines Einstellungsfenster: der Text nennt keine Seite (die Änderungen können auf mehreren Seiten liegen)
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            if (MessageBox.Show(this, "Die Änderungen sind noch nicht gespeichert.\r\n\r\nÄnderungen verwerfen?", "Monitor Dimmer",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            {
                _force = true; // bestätigt: das anschließende Schließen fragt nicht noch einmal
                return true;
            }
            return false; // auf der aktuellen Seite bleiben
        }

        /// <summary>Schließen ohne Rückfrage (Programmende).</summary>
        public void ForceClose()
        {
            _force = true;
            Close();
        }

        public void ShowMessage(string text, bool ok)
        {
            _message.Text = text;
            _message.ForeColor = ok ? Ui.Green : Ui.Red;
            _messageTimer.Stop();
            if (ok) _messageTimer.Start(); // ein Fehler bleibt stehen
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            // Schließen über das X: bei ungespeicherten Änderungen erst nachfragen; das Programm läuft im Infobereich weiter
            if (!_force && e.CloseReason == CloseReason.UserClosing && !ConfirmDiscard()) { e.Cancel = true; return; }
            ResetViews(false);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _messageTimer.Stop();
            _messageTimer.Dispose();
            _navTimer.Stop();
            _navTimer.Dispose();
            _navHover.Stop();
            _navHover.Dispose();
            base.OnFormClosed(e);
        }
    }
}
