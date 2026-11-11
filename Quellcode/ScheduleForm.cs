// Monitor Dimmer – Seite „Zeitplan“
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MonitorDimmer
{
    /// <summary>
    /// Zeitplan: In einem Zeitraum (z. B. abends) gelten im Leerlauf die Monitorwerte eines Profils statt „Ungedimmt“.
    /// Übernommen wird erst mit „Speichern“ im Hauptfenster (globale Vorgabe Einstellungsseiten).
    /// </summary>
    internal sealed class ScheduleForm : Form, ISettingsPage
    {
        private readonly ModernCheck _enabled;
        private readonly ComboBox _profile;
        private readonly ModernTextBox _from, _to;
        private string _initial = ""; // Stand beim Öffnen, für HasChanges
        private readonly Label _profileLabel, _fromLabel, _toLabel, _clockLabel;
        private float k = 1f;

        public ScheduleDef Result { get; private set; }

        private sealed class ProfileChoice
        {
            public DimmingProfile Profile;
            public override string ToString() { return Profile.Title; }
        }

        public ScheduleForm(AppConfig config, Icon icon)
        {
            var current = config.Schedule ?? new ScheduleDef();
            Text = "Monitor Dimmer - Zeitplan";
            if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.Manual;
            BackColor = Ui.Back;
            Font = new Font("Segoe UI", 9f);
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            ClientSize = new Size(S(980), S(680));
            MinimumSize = new Size(S(640), S(420));

            var head = new HeaderPanel("Zeitplan", "Zu bestimmten Zeiten automatisch dimmen, z. B. abends", k);

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Back, Padding = new Padding(S(28), 0, S(28), 0) };
            var card = new CardPanel(k) { Dock = DockStyle.Top, Height = S(240) };
            var input = new Font("Segoe UI", 9.5f);
            int x = S(24), col = x + S(110), y = S(16);

            var title = new Label { Text = "Zeitraum", Font = new Font("Segoe UI Semibold", 11f), ForeColor = Ui.Text, BackColor = Ui.Card, AutoSize = true, Location = new Point(x, y) };
            card.Controls.Add(title);
            y += S(40);

            _enabled = new ModernCheck("Im Leerlauf zeitgesteuert dimmen", k) { Location = new Point(x, y), Checked = current.Enabled };
            card.Controls.Add(_enabled);
            y += S(44);

            _profileLabel = new Label { Text = "Profil", Font = input, ForeColor = Ui.Text, BackColor = Ui.Card, AutoSize = true, Location = new Point(x, y + S(7)) };
            _profile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = input, Location = new Point(col, y), Width = S(360) };
            foreach (var p in config.Profiles.Where(q => !q.IsUndimmed)) _profile.Items.Add(new ProfileChoice { Profile = p });
            var selected = _profile.Items.Cast<ProfileChoice>().FirstOrDefault(c => string.Equals(c.Profile.Name, current.Profile, StringComparison.OrdinalIgnoreCase))
                ?? _profile.Items.Cast<ProfileChoice>().FirstOrDefault();
            if (selected != null) _profile.SelectedItem = selected;
            card.Controls.Add(_profileLabel);
            card.Controls.Add(_profile);
            y += S(44);

            // Eingabefelder im Stil des Auswahlfelds darüber (gleiche Höhe, gleicher Rahmen)
            _fromLabel = new Label { Text = "Von", Font = input, ForeColor = Ui.Text, BackColor = Ui.Card, AutoSize = true, Location = new Point(x, y + S(7)) };
            _from = new ModernTextBox(k) { Location = new Point(col, y), Width = S(84), MaxLength = 5, TextAlign = HorizontalAlignment.Center, Text = ScheduleDef.Format(current.From) };
            _toLabel = new Label { Text = "bis", Font = input, ForeColor = Ui.Text, BackColor = Ui.Card, AutoSize = true, Location = new Point(col + S(98), y + S(7)) };
            _to = new ModernTextBox(k) { Location = new Point(col + S(132), y), Width = S(84), MaxLength = 5, TextAlign = HorizontalAlignment.Center, Text = ScheduleDef.Format(current.To) };
            _clockLabel = new Label { Text = "Uhr", Font = input, ForeColor = Ui.Text, BackColor = Ui.Card, AutoSize = true, Location = new Point(col + S(230), y + S(7)) };
            card.Controls.AddRange(new Control[] { _fromLabel, _from, _toLabel, _to, _clockLabel });
            y += S(46);

            var note = new Label
            {
                Text = "Liegt „bis“ vor „Von“, gilt der Zeitraum über Mitternacht (z. B. 20:00 bis 07:00).",
                Font = new Font("Segoe UI", 9f), ForeColor = Ui.Muted, BackColor = Ui.Card, AutoSize = true, Location = new Point(col, y)
            };
            card.Controls.Add(note);

            var info = new InfoBanner("Im Zeitraum bekommen die Monitore die Werte des Profils, solange die Automatik aktiv ist und kein Spiel läuft.\n" +
                "Aktionen des Profils laufen dabei nicht. „Deaktiviert (Ungedimmt)“ im Menü hebt den Zeitplan auf.", k) { Dock = DockStyle.Bottom };

            body.Controls.Add(card);
            body.Controls.Add(info);

            Controls.Add(body);
            Controls.Add(head);
            Controls.Add(new SignatureFooter(k));
            _enabled.CheckedChanged += (s, e) => UpdateState();
            UpdateState();
            _initial = State();
            Load += (s, e) => WindowPlacement.Center(this);
            Theme.Apply(this);
        }

        private int S(float v) { return (int)Math.Round(v * k); }

        /// <summary>Abhängige Felder sofort sperren bzw. freigeben; übernommen wird erst beim Speichern.</summary>
        private void UpdateState()
        {
            bool on = _enabled.Checked;
            _profile.Enabled = on && _profile.Items.Count > 0;
            _from.Enabled = on;
            _to.Enabled = on;
            var color = on ? Ui.Text : Ui.Disabled;
            _profileLabel.ForeColor = color;
            _fromLabel.ForeColor = color;
            _toLabel.ForeColor = color;
            _clockLabel.ForeColor = color;
        }

        // ---------- ISettingsPage: das Hauptfenster speichert alle Seiten gemeinsam ----------

        private string State()
        {
            var choice = _profile.SelectedItem as ProfileChoice;
            return (_enabled.Checked ? "1" : "0") + "|" + (choice != null ? choice.Profile.Name : "") + "|" + _from.Text.Trim() + "|" + _to.Text.Trim();
        }

        public bool HasChanges { get { return State() != _initial; } }

        /// <summary>Erst alles prüfen, dann das Ergebnis bereitstellen. Rückgabe null = in Ordnung, sonst die Fehlermeldung.</summary>
        public string CheckInput()
        {
            TimeSpan start, end;
            var choice = _profile.SelectedItem as ProfileChoice;
            if (!ScheduleDef.TryParseTime(_from.Text, out start))
            {
                _from.Focus();
                return "„Von“ ist keine Uhrzeit (z. B. 20:00)";
            }
            if (!ScheduleDef.TryParseTime(_to.Text, out end))
            {
                _to.Focus();
                return "„bis“ ist keine Uhrzeit (z. B. 07:00)";
            }
            if (_enabled.Checked && choice == null) return "Es gibt kein Profil zum Dimmen";
            if (_enabled.Checked && start == end)
            {
                _to.Focus();
                return "„Von“ und „bis“ sind gleich";
            }
            Result = new ScheduleDef
            {
                Enabled = _enabled.Checked,
                Profile = choice != null ? choice.Profile.Name : "",
                From = start,
                To = end
            };
            return null;
        }
    }
}
