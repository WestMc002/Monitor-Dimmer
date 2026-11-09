// Monitor Dimmer – Hauptfenster mit Navigation (Monitore, Spieleliste, Tastenkürzel, Aktionen)
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
        /// <summary>Nach "Speichern" bzw. "Abbrechen" der Seite; Rückgabe = kurze Meldung im Fenster (oder null).</summary>
        public Func<Form, DialogResult, string> Done;
        /// <summary>Seite wurde geöffnet bzw. geschlossen (z. B. Tastenkürzel abschalten, solange die Seite offen ist).</summary>
        public Action Opened, Closed;
    }

    /// <summary>
    /// Ein Fenster für alle Ansichten (wie Wake on LAN): Navigation links, die Seite rechts, Footer unten.
    /// Speichern und Abbrechen gelten je Seite; ein Wechsel über die Navigation gilt wie "Abbrechen".
    /// </summary>
    internal sealed class MainWindow : Form
    {
        private readonly float k;
        private readonly List<PageDef> _pages;
        private readonly Dictionary<PageDef, NavButton> _buttons = new Dictionary<PageDef, NavButton>();
        private readonly Panel _nav, _navSpacer, _content;
        private readonly NavButton _navMenu, _navPin, _navSettings, _navQuit;
        private readonly Timer _navTimer = new Timer { Interval = 400 };
        private readonly SignatureFooter _footer;
        private readonly Panel _updateHost;  // Hinweisleiste oben, nur sichtbar bei neuer Version
        private readonly UpdateBar _updateBar;
        private Action _updateInstall, _updatePage, _updateLater;
        private bool _navOpen; // eingeklappt, aber vorübergehend ausgeklappt (liegt über dem Inhalt)
        private readonly Label _message;
        private readonly Timer _messageTimer = new Timer { Interval = 6000 };
        private PageDef _current;
        private Form _view;
        private bool _leaving; // Seite wird über die Navigation oder beim Schließen verlassen (nicht neu öffnen)

        /// <param name="settings">"Einstellungen" unten in der Navigation: zeigt das Einstellungsmenü am Knopf (null = ausblenden)</param>
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
            // unten wie bei Wake on LAN und Sync Pilot: Einstellungen (Zahnrad), darunter Beenden (Ein/Aus)
            _navSettings = new NavButton("Einstellungen", MenuHelper.Settings, k) { Visible = settings != null };
            _navSettings.Click += (s, e) =>
            {
                if (settings == null) return;
                _navSettings.Selected = true; // ausgewählt, solange das Menü offen ist
                settings(_navSettings);
            };
            _nav.Controls.Add(_navSettings);
            // komplett beenden (das X schließt nur das Fenster, das Programm läuft im Infobereich weiter)
            _navQuit = new NavButton("Beenden", MenuHelper.Power, k) { AccessibleName = "Beenden (auch im Infobereich)", Visible = quit != null };
            _navQuit.Click += (s, e) => { CloseNav(); if (quit != null) quit(); };
            _nav.Controls.Add(_navQuit);
            _nav.Resize += (s, e) => LayoutNav();
            _navTimer.Tick += (s, e) =>
            {
                // ausgeklappt, nicht angepinnt: wieder einklappen, sobald die Maus die Navigation verlässt
                if (!_navOpen || NavPinned || _nav.Capture || !Visible) return;
                if (!_nav.RectangleToScreen(_nav.ClientRectangle).Contains(Cursor.Position)) { _navOpen = false; ApplyNav(); }
            };
            _navTimer.Start();

            // ---------- Inhalt und Meldungszeile ----------
            _content = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Back };
            _message = new Label
            {
                Dock = DockStyle.Bottom,
                Height = S(30),
                ForeColor = Ui.Green,
                BackColor = Ui.Back,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(S(28), 0, S(28), 0),
                AutoEllipsis = true,
                UseMnemonic = false,
                Visible = false
            };
            _messageTimer.Tick += (s, e) => { _messageTimer.Stop(); _message.Visible = false; };

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
            Controls.Add(_message);
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

        /// <summary>Nach dem Schließen des Einstellungsmenüs: Markierung von "Einstellungen" entfernen.</summary>
        public void SettingsMenuClosed()
        {
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

        /// <summary>Seite öffnen. Eine offene andere Seite wird verlassen (wie "Abbrechen").</summary>
        public void ShowPage(PageDef page)
        {
            if (_view != null && _current == page) { _view.Focus(); return; }
            LeaveView();
            _message.Visible = false;
            OpenView(page);
        }

        private void OpenView(PageDef page)
        {
            var f = page.Create();
            _current = page;
            _view = f;
            foreach (var kv in _buttons) kv.Value.Selected = kv.Key == page;
            Text = "Monitor Dimmer - " + page.Text;

            f.TopLevel = false;
            f.FormBorderStyle = FormBorderStyle.None;
            f.ShowInTaskbar = false;
            f.MinimumSize = Size.Empty;
            f.MaximumSize = Size.Empty;
            // Footer hat das Hauptfenster; "Schließen" entfällt (Fenster schließt über das X)
            foreach (var footer in f.Controls.OfType<SignatureFooter>().ToList()) f.Controls.Remove(footer);
            foreach (var b in AllButtons(f))
            {
                var button = b;
                if (button.Text == "Schließen") { button.Visible = false; continue; }
                // Knöpfe mit DialogResult schließen ein eingebettetes Fenster nicht von selbst
                if (button.DialogResult != DialogResult.None)
                    button.Click += (s, e) => { f.DialogResult = button.DialogResult; BeginInvoke(new Action(f.Close)); };
            }
            f.FormClosed += (s, e) => BeginInvoke(new Action(() => EndView(f)));
            f.Dock = DockStyle.Fill; // jede Seite füllt den Inhaltsbereich (Info mittelt ihren Inhalt selbst)
            _content.Controls.Add(f);
            if (page.Opened != null) page.Opened();
            f.Show();
            _content.PerformLayout(); // Seiten, die ihre Größe selbst setzen (Monitore), wieder einpassen
            f.Focus();
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

        /// <summary>Offene Seite ohne Speichern verlassen.</summary>
        private void LeaveView()
        {
            var f = _view;
            if (f == null) return;
            _leaving = true;
            try
            {
                f.DialogResult = DialogResult.Cancel;
                if (!f.IsDisposed) f.Close();
                EndView(f);
            }
            finally { _leaving = false; }
        }

        private void EndView(Form f)
        {
            if (_view != f) return;
            var page = _current;
            var result = f.DialogResult;
            bool reopen = !_leaving && !IsDisposed && Visible;
            _view = null;
            string msg = null;
            try { if (page.Done != null) msg = page.Done(f, result); }
            finally
            {
                if (page.Closed != null) page.Closed();
                _content.Controls.Remove(f);
                if (!f.IsDisposed) f.Dispose();
            }
            if (!reopen) return;
            // nach Speichern bzw. Abbrechen: Seite frisch anzeigen, mit kurzer Meldung
            OpenView(page);
            if (!string.IsNullOrEmpty(msg)) ShowMessage(msg, result == DialogResult.OK);
        }

        public void ShowMessage(string text, bool ok)
        {
            _message.Text = text;
            _message.ForeColor = ok ? Ui.Green : Ui.Red;
            _message.Visible = true;
            _messageTimer.Stop();
            _messageTimer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (!e.Cancel) LeaveView(); // Schließen über das X verwirft wie "Abbrechen"; das Programm läuft im Infobereich weiter
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _messageTimer.Stop();
            _messageTimer.Dispose();
            _navTimer.Stop();
            _navTimer.Dispose();
            base.OnFormClosed(e);
        }
    }
}
