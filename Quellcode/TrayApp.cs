// Monitor Dimmer – Symbol im Infobereich, Menü und Profilsteuerung
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MonitorDimmer
{
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly string _baseDir;
        private readonly string _configPath;
        private readonly string _resourcesDir;
        private readonly string _dataDir = ""; // Laufzeitdaten (%LOCALAPPDATA%\Monitor Dimmer)

        private AppConfig _config;
        private readonly MonitorManager _monitors;
        private readonly EventRunner _events;
        private readonly ForegroundWatcher _watcher;
        private readonly MessageWindow _window;
        private readonly NotifyIcon _tray;
        private readonly FileSystemWatcher _configWatcher;
        private readonly Timer _undimTimer = new Timer();
        private readonly Timer _reloadTimer = new Timer { Interval = 1000 };
        private readonly Timer _reapplyTimer = new Timer();
        private readonly Timer _gameExitTimer = new Timer { Interval = 2000 };
        /// <summary>Update-Prüfung: erstes Mal 1 Minute nach dem Start, danach stündlich nachsehen (geprüft wird höchstens alle 20 h).</summary>
        private readonly Timer _updateTimer = new Timer { Interval = 60 * 1000 };
        private ReleaseInfo _available;      // neuere Version auf GitHub, sonst null
        private string _notifiedVersion;     // für diese Version wurde schon ein Hinweis gezeigt
        private bool _installing;
        /// <summary>Gesetzt beim Neustart nach einem Update (--nach-update).</summary>
        public static bool AfterUpdate;

        private DimmingProfile _current;          // gerade angewendetes Profil (Monitore)
        private DimmingProfile _scriptProfile;    // Profil, dessen "Applied"-Skripte zuletzt gelaufen sind
        private Process _game;                    // Spiel, das die Skripte ausgelöst hat (läuft es noch?)
        private DimmingProfile _enforced;         // per Menü/Hotkey erzwungenes Profil (null = Automatik)
        private ForegroundInfo _foreground;       // aktuelles Vordergrundprogramm
        private ForegroundInfo _lastCandidate;    // zuletzt aktives "fremdes" Programm (für "zur Liste hinzufügen")
        private string _matchedBy;                // Programm, das die Dimmung ausgelöst hat
        private bool _exiting;
        private Icon _icon;
        private readonly Dictionary<string, string> _hotkeyStatus = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _hotkeysSuspended;
        private System.Threading.SynchronizationContext _sync;

        private static readonly HashSet<string> ShellProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe", "SearchApp.exe",
            "SearchUI.exe", "LockApp.exe", "ApplicationFrameHost.exe", "TextInputHost.exe", "dwm.exe", "Taskmgr.exe",
            "ShellHost.exe", "SystemSettings.exe", "MonitorDimmer.exe", "Monitor Dimmer.exe"
        };

        /// <summary>Nur für die Screenshots: Menü mit Beispieldaten, ohne Monitore, Tastenkürzel und Überwachung.</summary>
        private TrayApp(AppConfig demo, DimmingProfile current, string matchedBy, ForegroundInfo candidate)
        {
            _baseDir = "";
            _configPath = "";
            _resourcesDir = "";
            _config = demo;
            _current = current;
            _matchedBy = matchedBy;
            _lastCandidate = candidate;
            _tray = new NotifyIcon { ContextMenuStrip = new ContextMenuStrip() };
        }

        /// <summary>Tray-Menü mit Beispieldaten (für die Screenshots).</summary>
        internal static ContextMenuStrip CreateDemoMenu(AppConfig demo, DimmingProfile current, string matchedBy, ForegroundInfo candidate)
        {
            var app = new TrayApp(demo, current, matchedBy, candidate);
            app.BuildMenu();
            var menu = app._tray.ContextMenuStrip;
            menu.Opened += (s, e) => MenuHelper.RoundCorners(menu);
            return menu;
        }

        public TrayApp(string baseDir, string dataDir, string configName)
        {
            _baseDir = baseDir;
            _dataDir = dataDir;
            _configPath = Path.Combine(dataDir, "Configurations", configName + ".xml"); // %LOCALAPPDATA%\Monitor Dimmer
            _resourcesDir = Path.Combine(baseDir, "Resources");

            Log.Info("==== Start Monitor Dimmer {0} (Benutzer {1}\\{2}, {3}) ====",
                typeof(TrayApp).Assembly.GetName().Version, Environment.UserDomainName, Environment.UserName,
                Autostart.IsElevated ? "mit Adminrechten" : "ohne Adminrechte");
            Log.Info("Konfiguration: {0}", _configPath);

            if (!File.Exists(_configPath))
            {
                AppConfig.EnsureExists(_configPath);
                Log.Info("Erster Start: Startkonfiguration angelegt");
            }
            _config = AppConfig.Load(_configPath);
            Accent.Enabled = _config.UseWindowsAccentColor;
            Log.SetMaxSize(_config.TraceLogFileMaxSize);
            LogConfigSummary();
            ConfigBackup.Create(_configPath); // auch Änderungen von Hand landen so in den Sicherungen

            // Handle-Nummern hängen fest am Monitor (Configurations\Monitore.ini), nicht an der Reihenfolge von Windows
            _monitors = new MonitorManager(Path.Combine(dataDir, "Configurations", "Monitore.ini"));
            _monitors.Smooth = _config.SmoothTransition;
            _monitors.Refresh();
            _events = new EventRunner(_resourcesDir);

            _tray = new NotifyIcon();
            _tray.Icon = LoadIcon();
            _tray.ContextMenuStrip = new ContextMenuStrip();
            _sync = System.Threading.SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            // Monitor nimmt die Werte nicht an: kommt aus dem DDC-Thread, Hinweis im Infobereich
            _monitors.Problem += text => _sync.Post(_ => { if (!_exiting) ShowBalloon(text, ToolTipIcon.Warning); }, null);
            _tray.ContextMenuStrip.Opening += (s, e) => BuildMenu();
            _tray.ContextMenuStrip.Opened += (s, e) =>
            {
                MenuHelper.KeepOnScreen(_tray.ContextMenuStrip); // nicht auf den Nachbarbildschirm ragen
                MenuHelper.RoundCorners(_tray.ContextMenuStrip);
            };
            // Doppelklick auf das Symbol im Infobereich: Fenster mit der Spieleliste öffnen (ist es schon offen: nach vorn holen)
            _tray.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMainWindow(); };
            BuildMenu();
            _tray.Visible = true;

            _undimTimer.Tick += (s, e) => { _undimTimer.Stop(); Evaluate(true); };
            _reloadTimer.Tick += (s, e) => { _reloadTimer.Stop(); ReloadConfig(false); };
            _reapplyTimer.Tick += (s, e) => { _reapplyTimer.Stop(); Reapply("Bildschirm geändert / Monitor eingeschaltet"); };

            _window = new MessageWindow();
            _window.DisplayChanged += () => ScheduleReapply(true);
            _window.DisplayTurnedOn += () => ScheduleReapply(false);
            _window.Resumed += () => ScheduleReapply(true);
            RegisterHotkeys();

            _current = _config.Undimmed;
            _scriptProfile = _current;
            ApplyMonitors(_current, "Programmstart");
            _gameExitTimer.Tick += (s, e) => CheckGameExit();
            _scheduleTimer.Tick += (s, e) => CheckSchedule();
            _scheduleTimer.Start();

            _watcher = new ForegroundWatcher();
            _watcher.Changed += OnForegroundChanged;
            _watcher.Check(true);

            _configWatcher = new FileSystemWatcher(Path.GetDirectoryName(_configPath), Path.GetFileName(_configPath));
            _configWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
            _configWatcher.Changed += (s, e) => OnConfigFileChanged();
            _configWatcher.Created += (s, e) => OnConfigFileChanged();
            _configWatcher.Renamed += (s, e) => OnConfigFileChanged();
            _configWatcher.EnableRaisingEvents = true;

            SystemEvents.SessionEnding += OnSessionEnding;
            UpdateTrayText();
            CheckAutostartPath();

            // ---------- Update-Prüfung (wie Fensterpositionen wiederherstellen) ----------
            if (AfterUpdate)
            {
                string result = Updater.LastResult();
                if (result != null && result.StartsWith("OK"))
                {
                    Log.Info("Update auf Version {0} installiert.", result.Substring(2).Trim());
                    ShowBalloon("Update auf Version " + result.Substring(2).Trim() + " installiert.");
                }
                else if (result != null)
                {
                    string msg = result.Replace("FEHLER", "").Trim();
                    Log.Error("Update fehlgeschlagen: {0} - die bisherige Version läuft weiter.", msg);
                    ShowBalloon("Update fehlgeschlagen: " + msg + "\r\nDie bisherige Version läuft weiter.", ToolTipIcon.Warning);
                }
            }
            AboutDialog.Updates = new UpdateHooks
            {
                Status = UpdateStatusNow,
                Auto = () => Updater.Auto,
                SetAuto = on => Updater.Auto = on,
                CheckNow = () => CheckForUpdates(true),
                CanInstall = () => _available != null && !string.IsNullOrEmpty(_available.ZipUrl) && Updater.InstallBlocker() == null,
                Install = InstallUpdate,
                OpenPage = OpenReleasePage
            };
            _tray.BalloonTipClicked += (s, e) => { if (_available != null && _balloonIsUpdate) InstallUpdate(); };
            // erste Prüfung 60 s nach dem Start, danach alle 6 Stunden nachsehen, ob sie fällig ist (höchstens alle 20 Stunden)
            _updateTimer.Tick += (s, e) => { _updateTimer.Interval = 6 * 60 * 60 * 1000; CheckForUpdates(false); };
            _updateTimer.Start();
        }

        // ================================================================== Kernlogik

        private void OnForegroundChanged(ForegroundInfo info)
        {
            _foreground = info;
            if (info.FileName != null && !ShellProcesses.Contains(info.FileName) && info.ProcessId != (uint)Process.GetCurrentProcess().Id
                && !IsWindowsProgram(info.FullPath))
                _lastCandidate = info;
            Evaluate(false);
        }

        private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

        /// <summary>Programme aus dem Windows-Ordner (mmc.exe, notepad.exe ...) sind nie Spiele.</summary>
        private static bool IsWindowsProgram(string path)
        {
            return path != null && path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase);
        }

        private DimmingProfile FindAutomaticProfile(ForegroundInfo fg, out ProcessPattern pattern)
        {
            pattern = null;
            if (fg == null || fg.FileName == null) return null;
            foreach (var p in _config.Profiles.Where(x => x.IsAutomatic))
            {
                pattern = p.FindMatch(fg.FullPath, fg.FileName, fg.FullPath == null);
                if (pattern != null) return p;
            }
            return null;
        }

        /// <summary>Entscheidet, welches Profil gelten soll. immediate = Verzögerung beim Entdimmen überspringen.</summary>
        private void Evaluate(bool immediate)
        {
            if (_exiting) return;
            DimmingProfile target;
            ProcessPattern pattern = null;
            if (_enforced != null)
                target = _enforced;
            else
                target = FindAutomaticProfile(_foreground, out pattern) ?? _config.Undimmed;

            if (target == _current)
            {
                _undimTimer.Stop();
                return;
            }

            // Kurzes Wegklicken aus dem Spiel (Alt+Tab, Overlay) soll nicht sofort alles umschalten.
            if (_enforced == null && target.IsUndimmed && !immediate && _config.UndimDelaySeconds > 0)
            {
                if (!_undimTimer.Enabled)
                {
                    _undimTimer.Interval = _config.UndimDelaySeconds * 1000;
                    _undimTimer.Start();
                }
                return;
            }
            _undimTimer.Stop();

            string reason;
            if (_enforced != null) reason = "manuell gewählt";
            else if (pattern != null) reason = "Vordergrund: " + _foreground.Display;
            else reason = "kein Spiel im Vordergrund";
            _matchedBy = pattern != null && _foreground != null ? _foreground.FileName : null;
            SwitchTo(target, reason);
        }

        private void SwitchTo(DimmingProfile target, string reason)
        {
            var previous = _current;
            _current = target;
            Log.Info("Wechsel \"{0}\" -> \"{1}\" ({2})", previous.Name, target.Name, reason);
            ApplyMonitors(target, reason);

            bool automatic = _enforced == null;
            if (automatic && _config.UndimScriptsOnGameExit && target == _scriptProfile && !target.IsUndimmed)
            {
                // Zurück ins laufende Spiel: nur die Monitore dimmen, Skripte sind schon gelaufen
                RememberGame();
                Log.Info("Zurück im Spiel - Skripte laufen nicht erneut");
            }
            else if (automatic && _config.UndimScriptsOnGameExit && target.IsUndimmed && GameRunning())
            {
                // Spiel nur verlassen (Alt+Tab, Fenstermodus, minimiert): Monitore entdimmen, Skripte warten
                Log.Info("Spiel \"{0}\" läuft noch - Skripte erst nach dem Beenden", SafeName(_game));
                _gameExitTimer.Start();
            }
            else
            {
                RunScripts(target);
            }
            UpdateTrayText();
        }

        /// <summary>Führt die Skripte für den Wechsel vom zuletzt "geskripteten" Profil zu target aus.</summary>
        private void RunScripts(DimmingProfile target)
        {
            _gameExitTimer.Stop();
            var previous = _scriptProfile ?? _config.Undimmed;
            if (previous != target)
            {
                _events.Run(previous, "OnAfterDimmingProfileUnapplied");
                _events.Run(target, "OnAfterDimmingProfileApplied");
            }
            _scriptProfile = target;
            if (target.IsUndimmed) ForgetGame();
            else RememberGame();
        }

        private void RememberGame()
        {
            ForgetGame();
            if (_enforced != null || _foreground == null || _foreground.ProcessId == 0) return;
            try { _game = Process.GetProcessById((int)_foreground.ProcessId); }
            catch { _game = null; }
        }

        private void ForgetGame()
        {
            if (_game != null) { try { _game.Dispose(); } catch { } }
            _game = null;
        }

        private bool GameRunning()
        {
            if (_game == null) return false;
            try { return !_game.HasExited; }
            catch { return false; }
        }

        private static string SafeName(Process p)
        {
            try { return p.ProcessName + ".exe"; } catch { return "?"; }
        }

        /// <summary>Alle 2 s, solange Skripte auf das Spielende warten.</summary>
        private void CheckGameExit()
        {
            if (_exiting) { _gameExitTimer.Stop(); return; }
            if (_current != _config.Undimmed || _scriptProfile == _current) { _gameExitTimer.Stop(); return; }
            if (GameRunning()) return;
            Log.Info("Spiel beendet - Skripte für \"{0}\" werden ausgeführt", _current.Name);
            RunScripts(_current);
            UpdateTrayText();
        }

        private void Reapply(string reason)
        {
            if (_current != null && !_exiting) ApplyMonitors(_current, reason);
        }

        // ================================================================== Zeitplan

        private readonly Timer _scheduleTimer = new Timer { Interval = 30 * 1000 };
        /// <summary>Profil, dessen Monitorwerte zuletzt gesetzt wurden (im Zeitraum des Zeitplans nicht _current).</summary>
        private DimmingProfile _applied;

        /// <summary>
        /// Im Leerlauf (Automatik, kein Spiel) gelten im Zeitraum des Zeitplans die Monitorwerte seines Profils statt
        /// "Ungedimmt". Nur die Monitorwerte: Aktionen und Skripte des Profils laufen nicht, der Zustand bleibt "Ungedimmt".
        /// </summary>
        private DimmingProfile Effective(DimmingProfile profile)
        {
            if (profile == null || !profile.IsUndimmed || _enforced != null) return profile;
            var plan = _config.Schedule;
            if (!plan.Enabled || !plan.Contains(DateTime.Now.TimeOfDay)) return profile;
            var p = _config.Find(plan.Profile);
            return p == null || p.IsUndimmed ? profile : p;
        }

        private void ApplyMonitors(DimmingProfile profile, string reason)
        {
            var effective = Effective(profile);
            _applied = effective;
            _monitors.Apply(effective, effective == profile ? reason : reason + ", Zeitplan");
        }

        /// <summary>Alle 30 s und nach dem Umschalten des Modus: Beginnt oder endet der Zeitraum, die Monitore neu setzen.</summary>
        private void CheckSchedule()
        {
            if (_exiting || _current == null || Effective(_current) == _applied) return;
            Log.Info("Zeitplan: Zeitraum {0}", _current.IsUndimmed && Effective(_current) != _current ? "beginnt" : "endet");
            ApplyMonitors(_current, "Zeitplan");
            UpdateTrayText();
        }

        /// <summary>Die Monitore sind gerade über den Zeitplan gedimmt.</summary>
        private bool ScheduleDimmed
        {
            get { return _applied != null && !_applied.IsUndimmed && _current != null && _current.IsUndimmed; }
        }

        private void ScheduleReapply(bool refreshMonitors)
        {
            if (_exiting) return;
            if (refreshMonitors)
            {
                Log.Info("Bildschirmkonfiguration geändert - Monitore werden neu erkannt");
                _reapplyTimer.Stop();
                _reapplyTimer.Interval = Math.Max(1, _config.ReapplyDelaySeconds) * 1000;
                _monitors.Refresh(); // läuft im DDC-Thread vor dem erneuten Anwenden
            }
            else
            {
                _reapplyTimer.Interval = Math.Max(1, _config.ReapplyDelaySeconds) * 1000;
            }
            _reapplyTimer.Stop();
            _reapplyTimer.Start();
        }

        private void Enforce(DimmingProfile profile)
        {
            _enforced = profile;
            Log.Info(profile == null ? "Automatik aktiviert" : "Profil \"{0}\" erzwungen", profile == null ? null : profile.Name);
            Evaluate(true);
            CheckSchedule(); // erzwungenes "Ungedimmt" hebt den Zeitplan auf, Automatik schaltet ihn wieder ein
            UpdateTrayText();
        }

        private void TogglePause()
        {
            if (_enforced != null && _enforced.IsUndimmed)
            {
                Enforce(null);
                ShowBalloon("Automatik ist wieder aktiv.");
            }
            else
            {
                Enforce(_config.Undimmed);
                ShowBalloon("Pausiert - Monitore bleiben ungedimmt.");
            }
        }

        // ================================================================== Spieleliste

        private DimmingProfile DefaultListProfile()
        {
            return _config.Profiles.FirstOrDefault(p => p.IsAutomatic && p.Patterns.Count > 0)
                ?? _config.Profiles.FirstOrDefault(p => p.IsAutomatic);
        }

        private void AddCandidateTo(DimmingProfile profile)
        {
            var c = _lastCandidate;
            if (c == null || c.FullPath == null)
            {
                ShowBalloon("Kein Programm erkannt. Erst das Spiel in den Vordergrund holen.", ToolTipIcon.Warning);
                return;
            }
            if (profile == null)
            {
                ShowBalloon("Es gibt kein Profil mit Automatik.", ToolTipIcon.Warning);
                return;
            }
            ProcessPattern existing;
            var owner = FindAutomaticProfile(c, out existing);
            if (owner != null)
            {
                ShowBalloon(c.FileName + " ist schon in \"" + owner.Title + "\" enthalten.");
                return;
            }
            try
            {
                _configWatcher.EnableRaisingEvents = false;
                AppConfig.AddProcessPath(_configPath, profile.Name, c.FullPath);
                Log.Info("\"{0}\" zu Profil \"{1}\" hinzugefügt", c.FullPath, profile.Name);
                ReloadConfig(true);
                ShowBalloon(c.FileName + " wurde zu \"" + profile.Title + "\" hinzugefügt.");
            }
            catch (Exception ex)
            {
                Log.Error("Hinzufügen fehlgeschlagen: {0}", ex);
                ShowBalloon("Hinzufügen fehlgeschlagen: " + ex.Message, ToolTipIcon.Error);
            }
            finally
            {
                _configWatcher.EnableRaisingEvents = true;
            }
        }

        /// <summary>Merkt Spielordner, die nicht mehr vorgeschlagen werden sollen.</summary>
        private void SaveIgnoredFolders(List<string> folders)
        {
            _configWatcher.EnableRaisingEvents = false;
            try
            {
                AppConfig.AddIgnoredFolders(_configPath, folders);
                Log.Info("{0} Spielordner werden nicht mehr vorgeschlagen", folders.Count);
                ReloadConfig(true);
            }
            finally
            {
                _configWatcher.EnableRaisingEvents = true;
            }
        }

        /// <summary>Speichert die Programmliste eines Profils (aus dem Fenster "Spieleliste").</summary>
        private void SaveProcessList(string profileName, List<string> paths)
        {
            _configWatcher.EnableRaisingEvents = false;
            try
            {
                AppConfig.SetProcessPaths(_configPath, profileName, paths);
                Log.Info("Spieleliste von \"{0}\" gespeichert ({1} Einträge)", profileName, paths.Count);
                ReloadConfig(true);
            }
            finally
            {
                _configWatcher.EnableRaisingEvents = true;
            }
        }

        private static bool SafeExists(string path)
        {
            try { return File.Exists(path); } catch { return false; }
        }

        // ================================================================== Konfiguration

        private void OnConfigFileChanged()
        {
            // Kommt aus einem anderen Thread -> auf den UI-Thread holen
            _sync.Post(_ => { if (!_exiting) { _reloadTimer.Stop(); _reloadTimer.Start(); } }, null);
        }

        private void ReloadConfig(bool quiet)
        {
            AppConfig cfg;
            try
            {
                cfg = AppConfig.Load(_configPath);
            }
            catch (Exception ex)
            {
                Log.Error("Konfiguration fehlerhaft, alte Einstellungen bleiben aktiv: {0}", ex.Message);
                ShowBalloon("Konfiguration fehlerhaft - alte Einstellungen bleiben aktiv.\r\n" + ex.Message, ToolTipIcon.Error);
                return;
            }
            _config = cfg;
            Accent.Enabled = cfg.UseWindowsAccentColor;
            Accent.Refresh();
            Log.SetMaxSize(cfg.TraceLogFileMaxSize);
            Log.Info("Konfiguration neu geladen");
            ConfigBackup.Create(_configPath);
            LogConfigSummary();

            _current = cfg.Find(_current.Name) ?? cfg.Undimmed;
            if (_scriptProfile != null) _scriptProfile = cfg.Find(_scriptProfile.Name) ?? cfg.Undimmed;
            if (_enforced != null) _enforced = cfg.Find(_enforced.Name);
            _monitors.Smooth = cfg.SmoothTransition;
            ApplyMonitors(_current, "Konfiguration neu geladen");
            if (!_hotkeysSuspended)
            {
                _window.UnregisterAllHotkeys();
                RegisterHotkeys();
            }
            var ico = LoadIcon();
            _tray.Icon = ico;
            Evaluate(false);
            UpdateTrayText();
            GameListForm.RefreshIfOpen();
            if (!quiet) ShowBalloon("Konfiguration neu geladen.");
        }

        private void LogConfigSummary()
        {
            foreach (var p in _config.Profiles)
            {
                Log.Info("  Profil \"{0}\" ({1}){2}{3}: {4} Programme, {5} Skripte", p.Name, p.Title,
                    p.IsUndimmed ? " [ungedimmt]" : "", p.IsAutomatic ? " [Automatik]" : "", p.Patterns.Count, p.Handlers.Count);
            }
        }

        private void RegisterHotkeys()
        {
            if (_hotkeysSuspended) return;
            _hotkeyStatus.Clear();
            foreach (var hk in _config.Hotkeys)
            {
                var def = hk;
                Action action = null;
                string act = (def.Action ?? "").Trim();
                if (act.Equals("AddForegroundProcess", StringComparison.OrdinalIgnoreCase))
                    action = () => AddCandidateTo(def.Profile != null ? _config.Find(def.Profile) : DefaultListProfile());
                else if (act.Equals("EnforceProfile", StringComparison.OrdinalIgnoreCase))
                    action = () =>
                    {
                        var p = _config.Find(def.Profile ?? "");
                        if (p == null) return;
                        Enforce(_enforced == p ? null : p);
                        ShowBalloon(_enforced == null ? "Automatik ist wieder aktiv." : "Erzwungen: " + p.Title);
                    };
                else if (act.Equals("SetProfile", StringComparison.OrdinalIgnoreCase))
                    action = () =>
                    {
                        var p = _config.Find(def.Profile ?? "");
                        if (p == null) return;
                        if (_enforced != p) Enforce(p);
                        ShowBalloon("Fest eingestellt: " + p.Title);
                    };
                else if (act.Equals("Automatic", StringComparison.OrdinalIgnoreCase))
                    action = () => { Enforce(null); ShowBalloon("Automatik ist wieder aktiv."); };
                else if (act.Equals("TogglePause", StringComparison.OrdinalIgnoreCase))
                    action = TogglePause;
                else if (act.Equals("Reapply", StringComparison.OrdinalIgnoreCase))
                    action = () => Reapply("Hotkey");
                else if (act.Equals("ShowMonitors", StringComparison.OrdinalIgnoreCase))
                    action = ShowMonitors;
                else if (act.Equals("ShowGameList", StringComparison.OrdinalIgnoreCase))
                    action = ShowGameList;

                if (action == null)
                {
                    Log.Warn("Hotkey {0}: unbekannte Aktion \"{1}\"", def.Keys, def.Action);
                    continue;
                }
                string err = _window.RegisterHotkey(def.Keys, action);
                _hotkeyStatus[HotkeyActions.Normalize(def.Keys)] = err ?? "";
                if (err != null)
                {
                    Log.Warn("Hotkey {0} ({1}) nicht registriert: {2}", def.Keys, def.Action, err);
                    ShowBalloon("Hotkey " + def.Keys + " nicht verfügbar: " + err, ToolTipIcon.Warning);
                }
                else
                {
                    Log.Info("Hotkey {0} -> {1}{2}", def.Keys, def.Action, def.Profile != null ? " (" + def.Profile + ")" : "");
                }
            }
        }

        // ================================================================== Oberfläche

        private Icon LoadIcon()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_config.TrayIconFile))
                {
                    string p = _config.TrayIconFile;
                    if (!Path.IsPathRooted(p)) p = Path.Combine(_resourcesDir, p);
                    if (File.Exists(p))
                    {
                        var ico = new Icon(p, SystemInformation.SmallIconSize);
                        if (_icon != null) _icon.Dispose();
                        _icon = ico;
                        return ico;
                    }
                    Log.Warn("Tray-Icon nicht gefunden: {0}", p);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Tray-Icon konnte nicht geladen werden: {0}", ex.Message);
            }
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }

        private string StatusText()
        {
            string s;
            if (_current == null || _current.IsUndimmed) s = ScheduleDimmed ? "Zeitplan: " + _applied.Title : "Ungedimmt";
            else s = _current.Title;
            if (_enforced != null) s += _enforced.IsUndimmed ? " (pausiert)" : " (erzwungen)";
            else if (_matchedBy != null && _current != null && !_current.IsUndimmed) s += " - " + _matchedBy;
            if (_gameExitTimer.Enabled && _game != null) s += " (Spiel läuft noch)";
            return s;
        }

        private void UpdateTrayText()
        {
            string t = _config.ProductName + "\n" + StatusText();
            _tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        private MenuTheme _theme;
        private ModernRenderer _renderer;
        private float _menuScale = 1f;

        private void BuildMenu()
        {
            Theme.Refresh();
            var menu = _tray.ContextMenuStrip;
            var oldItems = menu.Items.Cast<ToolStripItem>().ToList();
            menu.Items.Clear();
            foreach (var old in oldItems) old.Dispose();

            using (var g = menu.CreateGraphics()) _menuScale = g.DpiX / 96f;
            _theme = MenuTheme.Current();
            _renderer = new ModernRenderer(_theme, _menuScale);
            menu.Renderer = _renderer;
            menu.Font = new Font("Segoe UI", 9.5f);
            menu.ShowCheckMargin = false;
            menu.ShowItemToolTips = false; // Hinweisfenster würden unter dem Mauszeiger Klicks abfangen
            menu.ShowImageMargin = true;
            // Breite der Symbolspalte; Symbole, Haken und Punkte werden darin mittig gezeichnet
            menu.ImageScalingSize = new Size(IconWidth, (int)(16 * _menuScale));
            menu.Padding = new Padding(0, (int)(4 * _menuScale), 0, 0); // unten über MenuSpacer (Padding.Bottom wird ignoriert)
            MenuHelper.NoLegacyShadow(menu);

            // ---------- Kopf ----------
            Image appIcon = null;
            try { using (var ico = new Icon(Icon.ExtractAssociatedIcon(Application.ExecutablePath), 64, 64)) appIcon = ico.ToBitmap(); } catch { }
            var header = new MenuHeader(_theme, _menuScale, appIcon)
            {
                Title = _config.ProductName,
                Badge = Autostart.IsElevated && !Demo.Active ? "ADMIN" : null,
                Status = StatusText(),
                Version = "v" + typeof(TrayApp).Assembly.GetName().Version
            };
            bool dimmed = (_current != null && !_current.IsUndimmed) || ScheduleDimmed;
            header.StatusColor = dimmed ? _theme.Accent
                : _enforced != null ? Color.FromArgb(150, 150, 150)
                : Color.FromArgb(34, 160, 80);
            header.Size = header.GetPreferredSize(Size.Empty);
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            // ---------- Neue Version (wie die Hinweisleiste bei Fensterpositionen) ----------
            if (_available != null)
            {
                // oben im Menü fett (globale Vorgabe); im Projektordner bzw. ohne ZIP öffnet der Eintrag die Release-Seite
                bool canInstall = Updater.InstallBlocker() == null && !string.IsNullOrEmpty(_available.ZipUrl);
                var install = Item(_installing ? "Update wird geladen ..." : "Update auf " + _available.Version + " installieren ...", MenuHelper.Download,
                    canInstall ? (Action)InstallUpdate : OpenReleasePage);
                install.Font = new Font(menu.Font, FontStyle.Bold);
                install.Enabled = !_installing;
                menu.Items.Add(install);
                menu.Items.Add(new ToolStripSeparator());
            }

            // ---------- Modus ----------
            menu.Items.Add(Section("Modus"));
            var auto = Item("Automatik (Spiele erkennen)", MenuHelper.Auto, () => Enforce(null));
            auto.Checked = _enforced == null;
            auto.Radio = true;
            auto.ShortcutKeyDisplayString = HotkeyText("Automatic", null);
            menu.Items.Add(auto);
            foreach (var p in _config.Profiles)
            {
                var prof = p;
                var item = Item(prof.Title, '\0', () => Enforce(prof));
                item.Checked = _enforced == prof;
                item.Radio = true;
                item.ToolTipText = "Dieses Profil fest verwenden, bis wieder Automatik gewählt wird";
                item.ShortcutKeyDisplayString = HotkeyText("SetProfile", prof.Name)
                    ?? (prof.IsUndimmed ? HotkeyText("TogglePause", null) : HotkeyText("EnforceProfile", prof.Name));
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());

            // ---------- Spiel ----------
            var c = _lastCandidate;
            if (c != null && c.FullPath != null)
            {
                ProcessPattern dummy;
                var owner = FindAutomaticProfile(c, out dummy);
                if (owner != null)
                {
                    var known = Item(c.FileName + " ist in der Spieleliste", MenuHelper.Game, null);
                    known.Enabled = false;
                    menu.Items.Add(known);
                }
                else
                {
                    var add = Item(c.FileName + " zur Spieleliste hinzufügen", MenuHelper.Add, null);
                    add.ToolTipText = c.FullPath;
                    add.ShortcutKeyDisplayString = HotkeyText("AddForegroundProcess", null);
                    foreach (var p in _config.Profiles.Where(x => x.IsAutomatic))
                    {
                        var prof = p;
                        add.DropDownItems.Add(Item(prof.Title, '\0', () => AddCandidateTo(prof)));
                    }
                    StyleDropDown(add);
                    menu.Items.Add(add);
                }
            }
            var reapply = Item("Profil erneut anwenden", MenuHelper.Refresh, () => Reapply("manuell"));
            reapply.ShortcutKeyDisplayString = HotkeyText("Reapply", null);
            menu.Items.Add(reapply);
            menu.Items.Add(new ToolStripSeparator());

            // ---------- Fenster ----------
            var mon = Item("Monitore anzeigen...", MenuHelper.Monitor, ShowMonitors);
            mon.ShortcutKeyDisplayString = HotkeyText("ShowMonitors", null);
            menu.Items.Add(mon);
            var games = Item("Spieleliste bearbeiten...", MenuHelper.Game, ShowGameList);
            games.ShortcutKeyDisplayString = HotkeyText("ShowGameList", null);
            menu.Items.Add(games);
            menu.Items.Add(Item("Aktionen...", MenuHelper.Actions, ShowActions));
            menu.Items.Add(Item("Zeitplan...", MenuHelper.Clock, ShowSchedule));
            menu.Items.Add(Item("Tastenkürzel...", MenuHelper.Keyboard, ShowHotkeys));
            menu.Items.Add(new ToolStripSeparator());

            // ---------- Einstellungen ----------
            var settings = Item("Einstellungen", MenuHelper.Settings, null);
            settings.DropDownItems.Add(Item("Konfiguration bearbeiten", MenuHelper.Document, () => OpenFile(_configPath, true)));
            settings.DropDownItems.Add(Item("Konfiguration neu laden", MenuHelper.Sync, () => ReloadConfig(false)));
            settings.DropDownItems.Add(Item("Sicherungen öffnen", MenuHelper.History, OpenBackups));
            settings.DropDownItems.Add(Item("Protokoll öffnen", MenuHelper.Log, () => OpenFile(Log.FilePath, true)));
            settings.DropDownItems.Add(Item("Programmordner öffnen", MenuHelper.Folder, () => OpenFile(_baseDir, false)));
            settings.DropDownItems.Add(Item("Datenordner öffnen", MenuHelper.Folder, () => OpenFile(_dataDir, false)));
            settings.DropDownItems.Add(Item("Info über Monitor Dimmer", MenuHelper.Info, ShowInfo));
            settings.DropDownItems.Add(new ToolStripSeparator());
            bool autoOn = Autostart.IsEnabled();
            var autostart = Item("Mit Windows starten (mit Adminrechten)", MenuHelper.Startup, () => ToggleAutostart(autoOn));
            autostart.IsToggle = true;
            autostart.ToggleOn = autoOn;
            autostart.Text = "Mit Windows starten";
            autostart.ToolTipText = autoOn ? "An: startet bei der Anmeldung mit Adminrechten" : "Aus: klicken, um den Autostart (mit Adminrechten) einzurichten";
            // Platz rechts für den Schalter: Windows reserviert die Breite des "Tastenkürzel"-Texts
            autostart.ShortcutKeyDisplayString = "\u2007\u2007\u2007\u2007\u2007\u2007";
            settings.DropDownItems.Add(autostart);
            var smooth = Item("Sanft überblenden", MenuHelper.Brightness, ToggleSmooth);
            smooth.IsToggle = true;
            smooth.ToggleOn = _config.SmoothTransition;
            smooth.ToolTipText = "Helligkeit beim Profilwechsel in mehreren Schritten ändern statt springen";
            smooth.ShortcutKeyDisplayString = "\u2007\u2007\u2007\u2007\u2007\u2007";
            settings.DropDownItems.Add(smooth);
            StyleDropDown(settings);
            menu.Items.Add(settings);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(Item("Beenden", MenuHelper.Power, Quit));
            MenuHelper.AddBottomSpacer(menu, _menuScale);
        }

        private GlyphMenuItem Item(string text, char glyph, Action action)
        {
            var item = new GlyphMenuItem(text);
            int pad = (int)(5 * _menuScale);
            item.Padding = new Padding(0, pad, (int)(8 * _menuScale), pad);
            item.ShowShortcutKeys = true;
            item.FixedHeight = RowHeight;
            if (glyph != '\0') item.Glyph = MenuHelper.Glyph(glyph, _theme.Text, (int)(16 * _menuScale));
            // Unsichtbarer Platzhalter: Windows reserviert damit die volle Breite der Symbolspalte
            item.Image = MenuHelper.Blank(IconWidth, (int)(16 * _menuScale));
            item.ImageScaling = ToolStripItemImageScaling.None;
            if (action != null) item.Click += (s, e) => action();
            return item;
        }

        private int IconWidth { get { return (int)(36 * _menuScale); } }

        /// <summary>Einheitliche Zeilenhöhe wie im Hauptmenü: Schrifthöhe + je 4 Einheiten oben und unten.</summary>
        private int RowHeight
        {
            get
            {
                var f = _tray.ContextMenuStrip.Font;
                return TextRenderer.MeasureText("Ag", f).Height + (int)(8 * _menuScale);
            }
        }

        /// <summary>Autostart umschalten, im Hintergrund (die Adminabfrage von Windows blockiert sonst das Programm).</summary>
        private void ToggleAutostart(bool currentlyOn)
        {
            Log.Info("Autostart wird {0}", currentlyOn ? "entfernt" : "eingerichtet");
            string exe = Application.ExecutablePath;
            var ui = System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext();
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                bool ok = currentlyOn ? Autostart.Disable() : Autostart.Enable(exe);
                return ok ? (Autostart.IsEnabled() ? 1 : 0) : -1;
            }).ContinueWith(t =>
            {
                int r = t.Exception != null ? -1 : t.Result;
                if (r < 0) ShowBalloon("Autostart konnte nicht geändert werden (Adminrechte abgelehnt?).", ToolTipIcon.Warning);
                else ShowBalloon(r == 1 ? "Autostart ist eingerichtet." : "Autostart ist entfernt.");
            }, ui);
        }

        private MenuSection Section(string text)
        {
            var sec = new MenuSection(text, _theme, _menuScale);
            sec.Size = sec.GetPreferredSize(Size.Empty);
            return sec;
        }

        private void StyleDropDown(ToolStripMenuItem parent)
        {
            var dd = parent.DropDown;
            dd.Renderer = _renderer;
            dd.ShowItemToolTips = false;
            dd.Font = _tray.ContextMenuStrip.Font;
            var ddm = dd as ToolStripDropDownMenu;
            if (ddm != null)
            {
                ddm.ShowCheckMargin = false;
                ddm.ShowImageMargin = true;
                ddm.ImageScalingSize = _tray.ContextMenuStrip.ImageScalingSize;
            }
            // Windows berücksichtigt den oberen Innenabstand bei Untermenüs nicht zuverlässig:
            // oben eine feste, unsichtbare Leerzeile, unten der normale Rand
            dd.Padding = Padding.Empty; // Rand oben 4 über AddEdgeSpacers, unten MenuSpacer (6)
            MenuHelper.NoLegacyShadow(dd);
            MenuHelper.AddEdgeSpacers(dd, (int)(4 * _menuScale));
            MenuHelper.AddBottomSpacer(dd, _menuScale);
            dd.Opened += (s, e) => MenuHelper.RoundCorners(dd);
        }

        /// <summary>Tastenkürzel für eine Aktion als Anzeigetext (oder null).</summary>
        private string HotkeyText(string action, string profile)
        {
            var hk = _config.Hotkeys.FirstOrDefault(h => string.Equals(h.Action, action, StringComparison.OrdinalIgnoreCase) &&
                (profile == null || string.Equals(h.Profile, profile, StringComparison.OrdinalIgnoreCase)));
            return hk == null ? null : HotkeyActions.Display(hk.Keys).Replace(" + ", "+");
        }

        // ================================================================== Hauptfenster mit Navigation

        private MainWindow _main;
        private PageDef _pageMonitors, _pageGames, _pageHotkeys, _pageActions, _pageSchedule;

        /// <summary>Seiten des Hauptfensters (ein Fenster statt einzelner Fenster, wie Wake on LAN).</summary>
        private void CreatePages()
        {
            Func<Icon> icon = () => Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            _pageMonitors = new PageDef
            {
                Text = "Monitore", Glyph = MenuHelper.Monitor,
                Create = () => new MonitorForm(_monitors, () => _config, () => ScheduleDimmed ? _applied : _current, icon())
            };
            _pageGames = new PageDef
            {
                Text = "Spieleliste", Glyph = MenuHelper.Game,
                Create = () => new GameListForm(() => _config, SaveProcessList, SaveIgnoredFolders, icon()),
                Save = f => ((GameListForm)f).SaveAll()
            };
            _pageHotkeys = new PageDef
            {
                Text = "Tastenkürzel", Glyph = MenuHelper.Keyboard,
                Create = () => new HotkeyForm(_config, keys =>
                {
                    string st;
                    return _hotkeyStatus.TryGetValue(keys, out st) ? st : null;
                }, icon()),
                // solange die Seite zu sehen ist, sind die Tastenkürzel abgeschaltet
                Opened = () =>
                {
                    _hotkeysSuspended = true;
                    _window.UnregisterAllHotkeys();
                },
                Save = f => SaveHotkeys(((HotkeyForm)f).Result),
                Closed = () =>
                {
                    _hotkeysSuspended = false;
                    _window.UnregisterAllHotkeys();
                    RegisterHotkeys();
                }
            };
            _pageActions = new PageDef
            {
                Text = "Aktionen", Glyph = MenuHelper.Actions,
                Create = () =>
                {
                    var def = DefaultListProfile();
                    string select = _current != null && !_current.IsUndimmed ? _current.Name : (def != null ? def.Name : null);
                    return new ActionsForm(_config, _resourcesDir, select, icon());
                },
                Save = f => SaveActions((ActionsForm)f)
            };
            _pageSchedule = new PageDef
            {
                Text = "Zeitplan", Glyph = MenuHelper.Clock,
                Create = () => new ScheduleForm(_config, icon()),
                Save = f => SaveSchedule(((ScheduleForm)f).Result)
            };
        }

        /// <summary>Beenden über das Menü bzw. die Navigation: bei ungespeicherten Änderungen im Fenster erst nachfragen.</summary>
        private void Quit()
        {
            if (_main != null && !_main.IsDisposed && !_main.ConfirmDiscard()) return;
            ExitThread();
        }

        private void OpenPage(PageDef page)
        {
            if (_pageMonitors == null) CreatePages();
            if (_main == null || _main.IsDisposed)
            {
                _main = new MainWindow(new List<PageDef> { _pageMonitors, _pageGames, _pageActions, _pageSchedule, _pageHotkeys },
                    Icon.ExtractAssociatedIcon(Application.ExecutablePath),
                    ShowSettingsMenu,
                    () => _sync.Post(_ => Quit(), null)); // "Beenden" erst nach dem Klick ausführen
                _main.FormClosed += (s, e) => _main = null;
                RefreshUpdateBar();
            }
            _main.Open(page);
        }

        private void ShowMonitors() { OpenPage(Pages().Item1); }
        private void ShowGameList() { OpenPage(Pages().Item2); }
        private void ShowHotkeys() { OpenPage(Pages().Item3); }
        private void ShowActions() { OpenPage(Pages().Item4); }
        private void ShowSchedule() { Pages(); OpenPage(_pageSchedule); }
        /// <summary>
        /// "Extras" unten in der Navigation des Fensters (reines Einstellungsfenster, globale Vorgabe „Navigation links“):
        /// Info, Anleitung, Protokoll, Sicherungen, dazu Konfiguration, Ordner und die Schalter; Menü rechts über dem Knopf.
        /// </summary>
        private void ShowSettingsMenu(Control anchor)
        {
            var mb = new ModernMenuBuilder(anchor);
            var menu = mb.CreateMenu();
            menu.Items.Add(mb.Item("Info über Monitor Dimmer", MenuHelper.Info, ShowInfo));
            menu.Items.Add(mb.Item("Anleitung", MenuHelper.Document, () => OpenFile(Path.Combine(_baseDir, "Liesmich.txt"), true)));
            menu.Items.Add(mb.Item("Protokoll öffnen", MenuHelper.Log, () => OpenFile(Log.FilePath, true)));
            menu.Items.Add(mb.Item("Sicherungen öffnen", MenuHelper.History, OpenBackups));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(mb.Item("Konfiguration bearbeiten", MenuHelper.Document, () => OpenFile(_configPath, true)));
            menu.Items.Add(mb.Item("Konfiguration neu laden", MenuHelper.Sync, () => ReloadConfig(false)));
            menu.Items.Add(mb.Item("Programmordner öffnen", MenuHelper.Folder, () => OpenFile(_baseDir, false)));
            menu.Items.Add(mb.Item("Datenordner öffnen", MenuHelper.Folder, () => OpenFile(_dataDir, false)));
            menu.Items.Add(new ToolStripSeparator());
            bool autoOn = Autostart.IsEnabled();
            var autostart = mb.Item("Mit Windows starten", MenuHelper.Startup, () => ToggleAutostart(autoOn));
            autostart.IsToggle = true;
            autostart.ToggleOn = autoOn;
            autostart.ShortcutKeyDisplayString = "\u2007\u2007\u2007\u2007\u2007\u2007"; // Platz für den Schalter
            menu.Items.Add(autostart);
            var smooth = mb.Item("Sanft überblenden", MenuHelper.Brightness, ToggleSmooth);
            smooth.IsToggle = true;
            smooth.ToggleOn = _config.SmoothTransition;
            smooth.ShortcutKeyDisplayString = "\u2007\u2007\u2007\u2007\u2007\u2007";
            menu.Items.Add(smooth);
            menu.Closed += (s, e) =>
            {
                var w = anchor.FindForm() as MainWindow;
                if (w != null) w.SettingsMenuClosed();
                anchor.BeginInvoke(new Action(menu.Dispose));
            };
            // rechts über dem Knopf, auf den Bildschirm des Fensters begrenzt
            MenuHelper.ShowOnScreen(menu, anchor, new Point(anchor.Width + (int)(4 * _menuScale), anchor.Height), true);
        }

        /// <summary>Info-Fenster als kleines Pop-up (über dem Hauptfenster, falls es offen ist).</summary>
        private void ShowInfo() { AboutDialog.ShowFor(_main != null && !_main.IsDisposed && _main.Visible ? _main : null); }

        private bool _updateLater; // "Später" in der Hinweisleiste: bis zum nächsten Programmstart nicht mehr zeigen

        /// <summary>Hinweisleiste im Fenster an den Update-Stand anpassen (nur bei neuer Version sichtbar).</summary>
        private void RefreshUpdateBar()
        {
            if (_main == null || _main.IsDisposed) return;
            if (_available == null || _updateLater || Demo.Active) { _main.HideUpdateBar(); return; }
            string blocker = Updater.InstallBlocker() ?? (string.IsNullOrEmpty(_available.ZipUrl) ? "Im Release ist keine ZIP-Datei angehängt." : null);
            _main.ShowUpdateBar(_available.Version, blocker == null && !_installing, blocker, InstallUpdate, OpenReleasePage, () => _updateLater = true);
        }

        private void ShowMainWindow()
        {
            if (_main != null && !_main.IsDisposed && _main.Current != null) { _main.Open(_main.Current); return; }
            OpenPage(Pages().Item2); // Spieleliste
        }

        private Tuple<PageDef, PageDef, PageDef, PageDef> Pages()
        {
            if (_pageMonitors == null) CreatePages();
            return Tuple.Create(_pageMonitors, _pageGames, _pageHotkeys, _pageActions);
        }

        // Speichern der Seiten: Rückgabe null = gespeichert, sonst die Fehlermeldung (steht dann rot im Fenster)

        private string SaveHotkeys(List<HotkeyDef> hotkeys)
        {
            _configWatcher.EnableRaisingEvents = false;
            try
            {
                AppConfig.SetHotkeys(_configPath, hotkeys);
                Log.Info("Tastenkürzel gespeichert ({0})", hotkeys.Count);
                ReloadConfig(true);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("Tastenkürzel speichern: {0}", ex.Message);
                return "Tastenkürzel konnten nicht gespeichert werden: " + ex.Message;
            }
            finally
            {
                _configWatcher.EnableRaisingEvents = true;
            }
        }

        private string SaveSchedule(ScheduleDef schedule)
        {
            _configWatcher.EnableRaisingEvents = false;
            try
            {
                AppConfig.SetSchedule(_configPath, schedule);
                Log.Info("Zeitplan gespeichert ({0})", schedule.Enabled
                    ? ScheduleDef.Format(schedule.From) + " bis " + ScheduleDef.Format(schedule.To) + ", Profil " + schedule.Profile : "aus");
                ReloadConfig(true);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("Zeitplan speichern: {0}", ex.Message);
                return "Zeitplan konnte nicht gespeichert werden: " + ex.Message;
            }
            finally
            {
                _configWatcher.EnableRaisingEvents = true;
            }
        }

        /// <summary>Einstellungen > Sanft überblenden: gilt sofort (einzelner Schalter im Menü).</summary>
        private void ToggleSmooth()
        {
            bool on = !_config.SmoothTransition;
            _configWatcher.EnableRaisingEvents = false;
            try
            {
                AppConfig.SetRootValue(_configPath, "SmoothTransition", on ? "true" : "false");
                Log.Info("Sanft überblenden: {0}", on ? "an" : "aus");
                ReloadConfig(true);
            }
            catch (Exception ex)
            {
                Log.Error("Einstellung speichern: {0}", ex.Message);
                ShowBalloon("Einstellung konnte nicht gespeichert werden:\r\n" + ex.Message, ToolTipIcon.Error);
            }
            finally
            {
                _configWatcher.EnableRaisingEvents = true;
            }
        }

        private string SaveActions(ActionsForm f)
        {
            _configWatcher.EnableRaisingEvents = false;
            try
            {
                AppConfig.SetActions(_configPath, f.Result, f.RemoveHandlers);
                Log.Info("Aktionen gespeichert");
                ReloadConfig(true);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("Aktionen speichern: {0}", ex.Message);
                return "Aktionen konnten nicht gespeichert werden: " + ex.Message;
            }
            finally
            {
                _configWatcher.EnableRaisingEvents = true;
            }
        }

        private static void AddItem(ContextMenuStrip menu, string text, Action action)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (s, e) => action();
            menu.Items.Add(item);
        }

        private void OpenBackups()
        {
            ConfigBackup.Create(_configPath);
            string dir = ConfigBackup.Folder(_configPath);
            try { Directory.CreateDirectory(dir); } catch { }
            OpenFile(dir, false);
        }

        private void OpenFile(string path, bool withNotepadFallback)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception)
            {
                if (withNotepadFallback) Process.Start("notepad.exe", "\"" + path + "\"");
            }
        }

        public void ShowBalloon(string text)
        {
            ShowBalloon(text, ToolTipIcon.Info);
        }

        public void ShowBalloon(string text, ToolTipIcon icon)
        {
            _balloonIsUpdate = false;
            try { _tray.ShowBalloonTip(3000, _config.ProductName, text, icon); }
            catch { }
        }

        private bool _balloonIsUpdate; // Klick auf den Hinweis startet das Update

        // ================================================================== Update-Prüfung

        private string _updateError; // letzte fehlgeschlagene Prüfung (nur für das Info-Fenster)

        private UpdateStatus UpdateStatusNow()
        {
            DateTime last = Updater.LastCheck;
            string when = last == DateTime.MinValue ? "" : "Zuletzt geprüft " + last.ToLocalTime().ToString("dd.MM.yyyy");
            if (_available != null) return new UpdateStatus(UpdateKind.Available, "Version " + _available.Version + " verfügbar", when);
            if (_updateError != null) return new UpdateStatus(UpdateKind.Failed, "Prüfung nicht möglich", _updateError);
            if (last == DateTime.MinValue) return new UpdateStatus(UpdateKind.Unknown, "Noch nicht geprüft", "");
            return new UpdateStatus(UpdateKind.Current, "Aktuell", when);
        }

        /// <summary>
        /// Neueste Version auf GitHub abfragen (im Hintergrund). Automatisch (manual = false) höchstens alle 20 Stunden
        /// und still; Fehler stehen dann nur im Protokoll. Das Ergebnis einer Prüfung von Hand zeigt nur das Info-Fenster.
        /// </summary>
        private System.Threading.Tasks.Task<UpdateStatus> CheckForUpdates(bool manual)
        {
            if (Demo.Active || _exiting || (!manual && (!Updater.Auto || (DateTime.UtcNow - Updater.LastCheck).TotalHours < 20)))
                return System.Threading.Tasks.Task.FromResult(UpdateStatusNow());
            var ui = System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext();
            return System.Threading.Tasks.Task.Factory.StartNew(() => Updater.GetLatest())
                .ContinueWith(t =>
                {
                    if (_exiting) return UpdateStatusNow();
                    if (t.Exception != null)
                    {
                        string msg = t.Exception.GetBaseException().Message;
                        Log.Info("Update-Prüfung nicht möglich: {0}", msg);
                        if (manual) _updateError = msg;
                        return UpdateStatusNow();
                    }
                    var release = t.Result;
                    _updateError = null;
                    Updater.LastCheck = DateTime.UtcNow;
                    if (Updater.IsNewer(release.Version))
                    {
                        _available = release;
                        RefreshUpdateBar();
                        Log.Info("Neue Version {0} auf GitHub verfügbar (installiert: {1}).", release.Version, AppInfo.Version);
                        if (release.Version != _notifiedVersion && !manual)
                        {
                            // Hinweis im Infobereich einmal je Version und Programmstart
                            _notifiedVersion = release.Version;
                            bool canInstall = Updater.InstallBlocker() == null && !string.IsNullOrEmpty(release.ZipUrl);
                            ShowBalloon("Neue Version " + release.Version + " verfügbar – installiert ist " + AppInfo.Version + ".\r\n" +
                                (canInstall ? "Klicken zum Aktualisieren." : "Download über die Release-Seite (Menü)."), ToolTipIcon.Info);
                            _balloonIsUpdate = canInstall;
                        }
                    }
                    else
                    {
                        _available = null;
                        RefreshUpdateBar();
                        if (manual) Log.Info("Update-Prüfung: Version {0} ist aktuell.", AppInfo.Version);
                    }
                    return UpdateStatusNow();
                }, ui);
        }

        private void OpenReleasePage()
        {
            OpenFile(_available != null && !string.IsNullOrEmpty(_available.PageUrl) ? _available.PageUrl : Updater.ReleasesPage, false);
        }

        /// <summary>ZIP laden, Hilfsskript starten und beenden; das Skript ersetzt EXE und Liesmich und startet neu.</summary>
        private void InstallUpdate()
        {
            if (_available == null || _installing) return;
            var release = _available;
            if (Updater.InstallBlocker() != null || string.IsNullOrEmpty(release.ZipUrl)) { OpenReleasePage(); return; } // Projektordner: nie ersetzen
            if (MessageBox.Show("Monitor Dimmer wird beendet, auf Version " + release.Version + " aktualisiert und neu gestartet. Einstellungen bleiben erhalten." +
                    "\r\n\r\nJetzt aktualisieren?",
                    "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _installing = true;
            Log.Info("Lade Version {0} ...", release.Version);
            ShowBalloon("Lade Version " + release.Version + " ...");
            var ui = System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext();
            System.Threading.Tasks.Task.Factory.StartNew(() => Updater.Install(release))
                .ContinueWith(t =>
                {
                    _installing = false;
                    if (t.Exception != null)
                    {
                        string msg = t.Exception.GetBaseException().Message;
                        Log.Error("Update fehlgeschlagen: {0}", msg);
                        MessageBox.Show(msg, "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    Log.Info("Update wird installiert - Monitor Dimmer startet gleich neu.");
                    ExitThread();
                }, ui);
        }

        /// <summary>Autostart-Aufgabe nach dem Verschieben des Programmordners im Hintergrund auf die neue exe umstellen.</summary>
        private void CheckAutostartPath()
        {
            string exe = Application.ExecutablePath;
            var ui = System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext();
            System.Threading.Tasks.Task.Factory.StartNew(() => Autostart.FixTaskPath(exe))
                .ContinueWith(t =>
                {
                    if (t.Exception == null && t.Result != null) ShowBalloon(t.Result);
                }, ui);
        }

        // ================================================================== Beenden

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            // Beim Abmelden/Herunterfahren nur die Monitore zurücksetzen, keine Skripte starten.
            Log.Info("Windows wird abgemeldet/heruntergefahren");
            _exiting = true;
            if ((_current != null && !_current.IsUndimmed) || ScheduleDimmed)
                _monitors.ApplySync(_config.Undimmed, "Windows wird beendet", 3000);
        }

        protected override void ExitThreadCore()
        {
            if (!_exiting)
            {
                _exiting = true;
                Log.Info("Programm wird beendet");
                _gameExitTimer.Stop();
                if ((_current != null && !_current.IsUndimmed) || ScheduleDimmed)
                {
                    _current = _config.Undimmed;
                    _applied = _current;
                    _monitors.ApplySync(_config.Undimmed, "Programmende", 5000);
                }
                if (_scriptProfile != null && !_scriptProfile.IsUndimmed)
                {
                    _events.Run(_scriptProfile, "OnAfterDimmingProfileUnapplied");
                    _events.Flush(3000);
                    _scriptProfile = _config.Undimmed;
                }
            }
            Cleanup();
            base.ExitThreadCore();
        }

        /// <summary>Notfall-Zurücksetzen nach einem Absturz.</summary>
        public void EmergencyRestore()
        {
            try
            {
                if (_monitors != null && _config != null) _monitors.ApplySync(_config.Undimmed, "Notfall nach Fehler", 5000);
            }
            catch { }
        }

        private void Cleanup()
        {
            try { if (_main != null && !_main.IsDisposed) _main.ForceClose(); } catch { } // ohne Rückfrage (Programmende)
            SystemEvents.SessionEnding -= OnSessionEnding;
            try { _configWatcher.EnableRaisingEvents = false; _configWatcher.Dispose(); } catch { }
            _undimTimer.Stop();
            _reapplyTimer.Stop();
            _reloadTimer.Stop();
            _gameExitTimer.Stop();
            _scheduleTimer.Stop();
            _updateTimer.Stop();
            ForgetGame();
            if (_watcher != null) _watcher.Dispose();
            if (_window != null) _window.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _events.Dispose();
            _monitors.Dispose();
            Log.Info("==== Ende ====");
        }
    }
}
