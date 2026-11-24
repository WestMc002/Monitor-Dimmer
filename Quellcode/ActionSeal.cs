// Monitor Dimmer – Prüfwert für Aktionen, die mit Administratorrechten laufen
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MonitorDimmer
{
    /// <summary>
    /// Die Konfiguration liegt in %LOCALAPPDATA% und lässt sich von jedem Programm unter dem eigenen Konto ändern – auch ohne
    /// Administratorrechte. Monitor Dimmer läuft aber mit Administratorrechten (Autostart über die Aufgabenplanung), damit
    /// „Dienst beenden“ und ähnliche Aktionen ohne Rückfrage gehen. Ein fremdes Programm könnte so über die Aktionen zu
    /// Administratorrechten kommen. Deshalb (Abweichung wie Sync Pilot, Globale Richtlinien › Kein Autostart mit
    /// Administratorrechten):
    /// - Beim Speichern der Aktionen in Monitor Dimmer wird ein SHA-256-Prüfwert über alle Aktionen und alten Skripte samt
    ///   Inhalt der aufgerufenen Skriptdateien unter HKLM abgelegt (schreiben nur mit Administratorrechten).
    /// - Mit Administratorrechten laufen Aktionen, die Adminrechte nutzen können (Programme beenden, Dienste, Aufgaben,
    ///   Befehle, alte Skripte), nur, wenn der Prüfwert passt.
    /// - Befehle und Skripte nur aus geschützten Ordnern (Besitzer und Schreibrechte nur Administratoren, SYSTEM,
    ///   TrustedInstaller), damit eine geprüfte Datei nicht zwischen Prüfung und Start ausgetauscht werden kann.
    /// - „Programm starten“ startet immer ohne Adminrechte (wie aus dem Startmenü).
    /// </summary>
    internal static class ActionSeal
    {
        private const string KeyPath = @"SOFTWARE\Monitor Dimmer\Pruefwerte";

        /// <summary>Aktionsarten, die mit Administratorrechten mehr dürfen als ohne – nur mit gültigem Prüfwert.</summary>
        private static readonly HashSet<string> AdminTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "KillProcess", "StopService", "StartService", "RestoreStopped", "TaskRun", "TaskEnable", "TaskDisable", "Command"
        };

        private static readonly object Sync = new object();
        private static AppConfig _config;
        private static string _configPath, _resources;
        private static bool _valid;

        /// <summary>
        /// Arbeitsordner für „Befehl ausführen“ (Programmordner, wie beim Start über die Autostart-Aufgabe). Fest, damit
        /// relative Befehle beim Prüfen und beim Ausführen gleich aufgelöst werden (Dateidialoge ändern den aktuellen Ordner).
        /// </summary>
        public static string CommandDir { get { return System.Windows.Forms.Application.StartupPath; } }

        /// <summary>Mit Administratorrechten (und nicht in der Vorführung) gelten die Regeln dieser Klasse.</summary>
        public static bool Active { get { return Autostart.IsElevated && !Demo.Active; } }

        /// <summary>Stand der geladenen Konfiguration: Prüfwert gültig?</summary>
        public static bool Valid { get { lock (Sync) return _valid; } }

        /// <summary>
        /// Nach jedem Laden der Konfiguration: Stand merken und Prüfwert vergleichen. Die Aktionen laufen aus genau diesen
        /// Objekten im Speicher, also aus dem geprüften Stand. Rückgabe: gültig?
        /// </summary>
        public static bool SetConfig(AppConfig config, string configPath, string resources)
        {
            bool valid = false;
            try
            {
                string stored = Stored(configPath);
                valid = stored != null && string.Equals(stored, Hash(config, resources), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { Log.Warn("Prüfwert der Aktionen nicht lesbar: {0}", ex.Message); }
            lock (Sync)
            {
                _config = config;
                _configPath = configPath;
                _resources = resources;
                _valid = valid;
            }
            return valid;
        }

        /// <summary>Gibt es Aktionen oder alte Skripte, die nur mit gültigem Prüfwert laufen?</summary>
        public static bool HasAdminActions(AppConfig config)
        {
            foreach (var p in config.Profiles)
            {
                if (p.Handlers.Count > 0) return true;
                if (p.OnApplied.Concat(p.OnUnapplied).Any(a => AdminTypes.Contains(a.Type))) return true;
            }
            return false;
        }

        /// <summary>
        /// Muss der Stand bestätigt werden? Ja, wenn Monitor Dimmer mit Administratorrechten läuft oder so startet
        /// (Autostart) und Aktionen mit Adminrechten ohne gültigen Prüfwert vorhanden sind.
        /// </summary>
        public static bool NeedsConfirmation(AppConfig config, string configPath, string resources)
        {
            if (Demo.Active || !HasAdminActions(config)) return false;
            if (!Autostart.IsElevated && !Autostart.IsEnabled()) return false;
            try
            {
                string stored = Stored(configPath);
                return stored == null || !string.Equals(stored, Hash(config, resources), StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        public const string NotConfirmed = "mit Adminrechten nicht bestätigt (Aktionen öffnen, prüfen und speichern)";

        /// <summary>
        /// Vor einer Aktion aus dem Profilwechsel: null = darf laufen, sonst der Grund für „übersprungen“.
        /// Ohne Administratorrechte gibt es nichts zu schützen.
        /// </summary>
        public static string Blocked(ActionDef a) { return Blocked(a, true); }

        /// <summary>
        /// requireSeal = false nur für „Jetzt testen“ (von Hand, nach Einzelbestätigung): Dann zählt der Prüfwert nicht,
        /// die übrigen Schutzprüfungen (PowerShell, geschützte Dateien) gelten trotzdem.
        /// </summary>
        public static string Blocked(ActionDef a, bool requireSeal)
        {
            // Zeitlimit der Musterprüfungen (Shell-Aufrufe, Dateipfade): nicht eindeutig prüfbar = nicht ausführen
            try { return BlockedCore(a, requireSeal); }
            catch (RegexMatchTimeoutException) { return RxTimeoutReason; }
        }

        private static string BlockedCore(ActionDef a, bool requireSeal)
        {
            if (!Active || a == null || !AdminTypes.Contains(a.Type)) return null;
            if (requireSeal && !Valid) return NotConfirmed;
            if (a.Type == "Command")
            {
                string shell = ProfileShell(a.Value);
                if (shell != null) return shell;
                var files = AllFiles(a.Value, CommandDir);
                string file = Unprotected(files);
                if (file != null) return UnprotectedMessage(file);
                string script = ScriptShells(files);
                if (script != null) return script;
            }
            return null;
        }

        /// <summary>Altes Skript (EventHandler) mit Administratorrechten: null = darf laufen, sonst der Grund.</summary>
        public static string BlockedHandler(string exe, string args)
        {
            try { return BlockedHandlerCore(exe, args); }
            catch (RegexMatchTimeoutException) { return RxTimeoutReason; }
        }

        private static string BlockedHandlerCore(string exe, string args)
        {
            if (!Active) return null;
            if (!Valid) return NotConfirmed;
            var files = new List<string>();
            if (!string.IsNullOrEmpty(exe)) files.Add(exe);
            files.AddRange(AllFiles("\"" + exe + "\" " + args, SafeDir(exe)));
            string shell = ProfileShell("\"" + exe + "\" " + args);
            if (shell != null) return shell;
            string file = Unprotected(files);
            if (file != null) return UnprotectedMessage(file);
            return ScriptShells(files);
        }

        /// <summary>
        /// „Beendete wieder starten“: Die Liste der beendeten Dienste steht in einer Datei, die jedes Programm ändern kann.
        /// Mit Administratorrechten nur Dienste wieder starten, die eine bestätigte Aktion „Dienst beenden“ dieses Profils nennt.
        /// </summary>
        /// <summary>Braucht diese Aktion mit Administratorrechten eine Freigabe (Prüfwert bzw. Einzelbestätigung im Test)?</summary>
        public static bool IsAdminAction(ActionDef a) { return a != null && AdminTypes.Contains(a.Type); }

        public static bool ServiceAllowed(string profile, string service)
        {
            if (!Active) return true;
            AppConfig cfg;
            lock (Sync) { if (!_valid) return false; cfg = _config; }
            var p = cfg == null ? null : cfg.Find(profile);
            if (p == null) return false;
            return p.OnApplied.Concat(p.OnUnapplied).Any(a => a.Type == "StopService" &&
                string.Equals(Environment.ExpandEnvironmentVariables((a.Value ?? "").Trim()), service, StringComparison.OrdinalIgnoreCase));
        }

        // ------------------------------------------------------------------ Prüfwert

        /// <summary>Prüfwert über alle Aktionen und alten Skripte aller Profile und den Inhalt der Dateien, die sie aufrufen.</summary>
        public static string Hash(AppConfig config, string resources)
        {
            var b = new StringBuilder("v1\n");
            Action<string, string> add = (k, v) =>
            {
                v = v ?? "";
                b.Append(k).Append('=').Append(v.Length).Append(':').Append(v).Append('\n');
            };
            foreach (var p in config.Profiles.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                add("Profil", p.Name);
                foreach (var list in new[] { Tuple.Create("+", p.OnApplied), Tuple.Create("-", p.OnUnapplied) })
                    foreach (var a in list.Item2)
                    {
                        add("Aktion" + list.Item1, a.Type);
                        add("Wert", a.Value);
                        add("Argumente", a.Arguments);
                        if (a.Type == "Command")
                            foreach (string f in AllFiles(a.Value, CommandDir)) add("Datei " + f, FileState(f));
                    }
                foreach (var h in p.Handlers)
                {
                    add("Skript", h.Type);
                    add("Programm", h.Executable);
                    add("Argumente", h.Arguments);
                    add("Unsichtbar", h.Invisible ? "1" : "0");
                    string exe = Environment.ExpandEnvironmentVariables((h.Executable ?? "").Replace("{ResourcesFolderPath}", resources));
                    string args = Environment.ExpandEnvironmentVariables((h.Arguments ?? "").Replace("{ResourcesFolderPath}", resources));
                    var files = new List<string> { exe };
                    files.AddRange(AllFiles("\"" + exe + "\" " + args, SafeDir(exe)));
                    foreach (string f in files.Distinct(StringComparer.OrdinalIgnoreCase)) add("Datei " + f, FileState(f));
                }
            }
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(b.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        private static string SafeDir(string path)
        {
            try { return Path.GetDirectoryName(path); } catch { return null; }
        }

        private static string FileState(string f)
        {
            if (f.StartsWith("?")) return "nicht gefunden";
            return SystemFile(f) ? "Windows/Programme" : FileHash(f);
        }

        /// <summary>Name des Werts unter HKLM: je Konfigurationsdatei (mehrere Benutzer, mehrere Konfigurationen).</summary>
        private static string ValueName(string configPath)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(configPath).ToUpperInvariant()))).Replace("-", "").Substring(0, 32);
        }

        private static RegistryKey Base() { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); }

        private static string Stored(string configPath)
        {
            using (var root = Base())
            using (var k = root.OpenSubKey(KeyPath, false))
                return k == null ? null : k.GetValue(ValueName(configPath)) as string;
        }

        /// <summary>
        /// Prüfwert für den gespeicherten Stand schreiben: mit eigenen Administratorrechten direkt, sonst über ein kurzes
        /// Skript mit Administratorrechten (Windows fragt einmal nach). Nur nötig, wenn Monitor Dimmer mit Adminrechten
        /// läuft oder so startet. Rückgabe null = gespeichert bzw. nicht nötig, sonst eine Meldung.
        /// </summary>
        public static string Save(AppConfig config, string configPath, string resources)
        {
            if (Demo.Active || !HasAdminActions(config)) return null;
            bool elevated = Autostart.IsElevated;
            if (!elevated && !Autostart.IsEnabled()) return null;
            string name = ValueName(configPath), hash;
            // Läuft beim Berechnen eine Musterprüfung ins Zeitlimit, ist der Stand nicht eindeutig prüfbar: keine Freigabe
            try { hash = Hash(config, resources); }
            catch (RegexMatchTimeoutException) { return "Der Prüfwert wurde nicht gespeichert: Ein Befehl oder Skript ist nicht eindeutig prüfbar (die Prüfung hat das Zeitlimit überschritten). Bitte den Befehl vereinfachen."; }
            catch (Exception ex) { return "Der Prüfwert konnte nicht berechnet werden: " + ex.Message; }
            if (elevated)
            {
                try
                {
                    using (var root = Base())
                    using (var k = root.CreateSubKey(KeyPath))
                        k.SetValue(name, hash);
                    return null;
                }
                catch (Exception ex) { return "Der Prüfwert konnte nicht gespeichert werden: " + ex.Message; }
            }
            string ps = "$ErrorActionPreference = 'Stop'\r\ntry {\r\n" +
                        "$r = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine','Registry64')\r\n" +
                        "$k = $r.CreateSubKey(" + Ps(KeyPath) + ")\r\n" +
                        "$k.SetValue(" + Ps(name) + ", " + Ps(hash) + ")\r\n" +
                        "$k.Close(); $r.Close()\r\nexit 0\r\n} catch { exit 1 }";
            int rc = RunElevated(ps);
            if (rc == 0) return null;
            return rc == -1 ? "Die Bestätigung von Windows (Administratorrechte) fehlt." : "Der Prüfwert konnte nicht gespeichert werden (Fehler " + rc + ").";
        }

        /// <summary>Text als PowerShell-Zeichenkette in einfachen Anführungszeichen (auch typografische verdoppeln).</summary>
        public static string Ps(string text)
        {
            var b = new StringBuilder("'");
            foreach (char c in text ?? "")
            {
                if (c == '\'' || c == '‘' || c == '’' || c == '‚' || c == '‛') b.Append(c);
                b.Append(c);
            }
            return b.Append('\'').ToString();
        }

        /// <summary>PowerShell über den festen Pfad (nie über den Suchpfad).</summary>
        public static string PowerShellExe
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"); }
        }

        /// <summary>
        /// Skript mit Administratorrechten ausführen, direkt in der Befehlszeile (-EncodedCommand) statt als Datei – zwischen
        /// Schreiben und Start gibt es so nichts, was sich austauschen ließe. Läuft Monitor Dimmer schon als Administrator,
        /// ohne Rückfrage. Rückgabewert oder -1 (abgelehnt/Fehler), -2 (PowerShell fehlt).
        /// </summary>
        public static int RunElevated(string script)
        {
            if (!File.Exists(PowerShellExe)) return -2;
            try
            {
                var psi = new ProcessStartInfo(PowerShellExe, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
                };
                if (Autostart.IsElevated) { psi.UseShellExecute = false; psi.CreateNoWindow = true; }
                else { psi.UseShellExecute = true; psi.Verb = "runas"; }
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        // ------------------------------------------------------------------ Aufgerufene Dateien

        private static readonly string[] ScriptExt = { ".bat", ".cmd", ".ps1", ".exe", ".com", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".py", ".msi" };

        /// <summary>
        /// Dateien, die ein Befehl ausführt (Referenz Sync Pilot, JobSeal.ScriptFiles): absolute Pfade mit bekannter Endung
        /// und der aufgerufene Befehl jedes Teilbefehls, so aufgelöst wie cmd.exe (Arbeitsordner, dann PATH mit PATHEXT).
        /// Nicht gefundene bzw. eingebaute Befehle mit „?“ davor.
        /// </summary>
        public static List<string> ScriptFiles(string cmd, string workDir)
        {
            var r = new List<string>();
            if (string.IsNullOrEmpty(cmd)) return r;
            string expanded = Environment.ExpandEnvironmentVariables(cmd.Trim());
            foreach (Match m in Regex.Matches(expanded, "\"([^\"]+)\"|((?:[A-Za-z]:\\\\|\\\\\\\\)[^\\s\"]+)", RegexOptions.None, RxTimeout))
            {
                string p = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                string ext;
                try { ext = Path.GetExtension(p).ToLowerInvariant(); } catch { continue; }
                if (Array.IndexOf(ScriptExt, ext) < 0) continue;
                if (!(p.Length > 2 && p[1] == ':') && !p.StartsWith("\\\\")) continue;
                if (!r.Contains(p)) r.Add(p);
            }
            bool commandNext = true;
            foreach (string raw in Words(expanded))
            {
                string word = raw;
                if (word == "&" || word == "|" || word == "(" || word == ")") { commandNext = true; continue; }
                if (word == "<" || word == ">") { commandNext = false; continue; }
                if (!commandNext) continue;
                if (word.StartsWith("@")) word = word.Substring(1);
                if (word.Length == 0) continue;
                if (word.Equals("call", StringComparison.OrdinalIgnoreCase) || word.Equals("start", StringComparison.OrdinalIgnoreCase) ||
                    word == "\"\"" || word.StartsWith("/")) continue; // Befehl folgt erst danach
                commandNext = false;
                string found = Resolve(word.Trim('"'), workDir);
                if (found != null) { if (!r.Contains(found)) r.Add(found); }
                else if (!r.Contains("?" + word)) r.Add("?" + word);
            }
            return r;
        }

        /// <summary>
        /// Alle Dateien, die ein Befehl nennt: die aufgerufenen Befehle und Skripte (ScriptFiles) und dazu jedes Wort, das
        /// eine vorhandene Datei ist (absolut oder relativ zum Arbeitsordner, auch hinter „=“ bzw. „/x:“), gleich welche
        /// Endung – z. B. eine Datei, die ein Programm als Argument lädt. Mit Administratorrechten müssen sie alle
        /// geschützt liegen, und ihr Inhalt zählt zum Prüfwert.
        /// </summary>
        public static List<string> AllFiles(string cmd, string workDir)
        {
            var r = ScriptFiles(cmd, workDir);
            if (string.IsNullOrEmpty(cmd)) return r;
            foreach (string raw in Words(Environment.ExpandEnvironmentVariables(cmd.Trim())))
            {
                string w = raw.Trim('"');
                foreach (char sep in new[] { '=', ':' })
                {
                    int at = w.IndexOf(sep);
                    // „/datei:C:\x“ bzw. „-file=x“: Teil hinter dem Trenner (nicht das „:“ eines Laufwerks)
                    if (at > 0 && !(sep == ':' && at == 1)) w = w.Substring(at + 1).Trim('"');
                }
                if (w.Length < 2 || w.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
                try
                {
                    string full = Path.IsPathRooted(w) ? w : (string.IsNullOrEmpty(workDir) ? null : Path.Combine(workDir, w));
                    if (full == null || !File.Exists(full)) continue;
                    full = Path.GetFullPath(full);
                    if (!r.Contains(full, StringComparer.OrdinalIgnoreCase)) r.Add(full);
                }
                catch { }
            }
            return r;
        }

        /// <summary>PowerShell-Schalter mit einem Wert dahinter (der Wert ist noch kein Skript).</summary>
        private static readonly HashSet<string> ValueSwitches = new HashSet<string>(StringComparer.Ordinal)
        {
            "-executionpolicy", "-ep", "-ex", "-exec", "-windowstyle", "-w", "-win", "-inputformat", "-if", "-outputformat", "-of",
            "-o", "-version", "-v", "-psconsolefile", "-configurationname", "-workingdirectory", "-wd", "-settingsfile",
            "/executionpolicy", "/ep", "/windowstyle", "/w", "/version"
        };

        /// <summary>
        /// PowerShell lädt ohne -NoProfile die Profilskripte des Benutzers (Dokumente\WindowsPowerShell\…), die jedes
        /// Programm ändern kann. Mit Administratorrechten deshalb nur mit -NoProfile – und zwar bei jedem einzelnen Aufruf:
        /// Der Befehl wird in Teilbefehle zerlegt (&amp;, |, Klammern); ruft ein Teilbefehl PowerShell auf (auch nach
        /// call/start), muss -NoProfile bzw. -nop als Schalter dieses Aufrufs vor dem Skript (-Command, -File, …) stehen.
        /// Ebenso cmd.exe: Ohne /d führt jede weitere Eingabeaufforderung den AutoRun-Eintrag des Benutzers aus (den jedes
        /// Programm ändern kann). Innere cmd-Aufrufe brauchen deshalb /d vor /c bzw. /k; „start“ mit einer .bat/.cmd-Datei
        /// (öffnet eine neue Eingabeaufforderung ohne /d) ist mit Adminrechten nicht erlaubt.
        /// null = in Ordnung, sonst der Grund.
        /// </summary>
        /// <summary>Zeitlimit für die Musterprüfungen: lieber „nicht eindeutig prüfbar“ als eine hängende Prüfung.</summary>
        private static readonly TimeSpan RxTimeout = TimeSpan.FromMilliseconds(250);

        private const string RxTimeoutReason = "Aufruf nicht eindeutig prüfbar (die Prüfung hat das Zeitlimit überschritten) – läuft deshalb nicht mit Adminrechten";

        private static string ProfileShell(string cmd)
        {
            try { return ProfileShellCore(cmd); }
            catch (RegexMatchTimeoutException) { return RxTimeoutReason; }
        }

        private static string ProfileShellCore(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            const string Reason = "PowerShell läuft mit Adminrechten nur mit -NoProfile bei jedem Aufruf (sonst lädt es Profilskripte, die jedes Programm ändern kann)";
            var words = Words(Environment.ExpandEnvironmentVariables(cmd));
            bool commandNext = true, inShell = false, noProfile = false, scriptStarted = false, skipValue = false, afterStart = false;
            int shells = 0, cmds = 0; // geprüfte PowerShell- bzw. cmd-Aufrufe
            bool nestedCmd = false, cmdHasD = false;
            int ifCond = 0;        // if-Bedingung: 1 = Anfang, 2 = linker Wert gelesen, 3 = noch ein Wert, dann Befehl
            bool forLoop = false;  // for … do: nach „do“ folgt ein Befehl
            const string CmdReason = "Eine weitere Eingabeaufforderung (cmd) läuft mit Adminrechten nur mit /d vor /c (sonst führt sie den AutoRun-Eintrag aus, den jedes Programm ändern kann)";
            Func<bool> fail = () => inShell && !noProfile;
            foreach (string raw in words)
            {
                if (raw == "&" || raw == "|" || raw == "(" || raw == ")")
                {
                    if (fail()) return Reason;
                    if (nestedCmd && !cmdHasD) return CmdReason;
                    commandNext = true; inShell = false; noProfile = false; scriptStarted = false; skipValue = false; afterStart = false; nestedCmd = false; cmdHasD = false;
                    ifCond = 0; forLoop = false;
                    continue;
                }
                string w = raw.Trim('"').TrimStart('@');
                if (ifCond > 0)
                {
                    // if [/i] [not] exist|defined|errorlevel|cmdextversion X befehl, if a==b befehl, if a equ b befehl
                    string c = w.ToLowerInvariant();
                    if (ifCond == 1)
                    {
                        if (c == "/i" || c == "not") continue;
                        if (c == "exist" || c == "defined" || c == "errorlevel" || c == "cmdextversion") { ifCond = 3; continue; }
                        if (raw.Contains("=="))
                        {
                            if (raw.EndsWith("==")) ifCond = 3; else { ifCond = 0; commandNext = true; }
                            continue;
                        }
                        ifCond = 2;
                        continue;
                    }
                    if (ifCond == 2)
                    {
                        if (c == "==" || c == "equ" || c == "neq" || c == "lss" || c == "leq" || c == "gtr" || c == "geq") { ifCond = 3; continue; }
                        if (raw.StartsWith("==")) { ifCond = raw.Length == 2 ? 3 : 0; commandNext = ifCond == 0; continue; }
                        ifCond = 0; commandNext = true; // unbekannte Form: dieses Wort als Befehl behandeln
                    }
                    else { ifCond = 0; commandNext = true; continue; }
                }
                if (forLoop && !commandNext)
                {
                    if (w.Equals("do", StringComparison.OrdinalIgnoreCase)) { forLoop = false; commandNext = true; }
                    continue;
                }
                if (commandNext)
                {
                    w = w.TrimStart('(');
                    if (w.Equals("if", StringComparison.OrdinalIgnoreCase)) { ifCond = 1; commandNext = false; continue; }
                    if (w.Equals("else", StringComparison.OrdinalIgnoreCase)) continue;
                    if (w.Equals("for", StringComparison.OrdinalIgnoreCase)) { forLoop = true; commandNext = false; continue; }
                    if (w.Equals("start", StringComparison.OrdinalIgnoreCase)) { afterStart = true; continue; }
                    // start "Titel" programm: der erste Text in Anführungszeichen ist der Fenstertitel
                    if (afterStart && raw.StartsWith("\"")) { afterStart = false; continue; }
                    if (w.Length == 0 || w.Equals("call", StringComparison.OrdinalIgnoreCase) || w.StartsWith("/")) continue;
                    commandNext = false;
                    string name = Path.GetFileName(w.TrimEnd(')').Replace('/', '\\')).ToLowerInvariant();
                    // start x.bat öffnet eine neue Eingabeaufforderung ohne /d
                    if (afterStart && (name.EndsWith(".bat") || name.EndsWith(".cmd"))) return CmdReason;
                    afterStart = false;
                    inShell = name == "powershell" || name == "powershell.exe" || name == "pwsh" || name == "pwsh.exe";
                    if (inShell) shells++;
                    // cmd /c bzw. /k: danach folgt wieder ein Befehl
                    if (name == "cmd" || name == "cmd.exe") { nestedCmd = true; cmdHasD = false; cmds++; }
                    continue;
                }
                if (nestedCmd)
                {
                    string c = w.ToLowerInvariant();
                    if (c == "/d") { cmdHasD = true; continue; }
                    if (c == "/c" || c == "/k" || c == "/r")
                    {
                        if (!cmdHasD) return CmdReason;
                        nestedCmd = false;
                        commandNext = true;
                    }
                    continue;
                }
                if (!inShell || scriptStarted) continue;
                string l = w.ToLowerInvariant();
                if (skipValue) { skipValue = false; continue; } // Wert eines Schalters (z. B. -ExecutionPolicy Bypass)
                if (ValueSwitches.Contains(l)) { skipValue = true; continue; }
                if (l == "-noprofile" || l == "-nop" || l == "/noprofile" || l == "/nop") { noProfile = true; continue; }
                // ab hier gehört der Rest zum Skript (Schalter danach gelten nicht mehr für PowerShell selbst)
                if (!(l.StartsWith("-") || l.StartsWith("/")) || l == "-c" || l == "-command" || l == "-f" || l == "-file" ||
                    l == "-e" || l == "-ec" || l == "-encodedcommand" || l.StartsWith("-command") || l.StartsWith("-file"))
                {
                    if (!noProfile) return Reason;
                    scriptStarted = true;
                }
            }
            if (fail()) return Reason;
            if (nestedCmd && !cmdHasD) return CmdReason;
            // Sicherheitsnetz: Steht eine Shell öfter an einer Befehlsstelle, als Aufrufe erkannt und geprüft wurden (z. B.
            // verschachtelt in Anführungszeichen, nach for/if oder mit Escape-Zeichen), lieber nicht mit Adminrechten
            // ausführen. Gezählt werden nur Nennungen an Befehlsstellen (Anfang, nach & | ( ; ' { sowie nach call, start,
            // do, /c, /k, -Command …), nicht gewöhnliche Argumentwerte wie „findstr cmd liste.txt“.
            string lower = Environment.ExpandEnvironmentVariables(cmd).ToLowerInvariant().Replace("^", "");
            int cmdMore = CommandPositions(lower, @"cmd") - cmds, psMore = CommandPositions(lower, @"(?:powershell|pwsh)") - shells;
            if (cmdMore <= 0 && psMore <= 0) return null;
            string which = psMore > 0 ? "PowerShell" : "cmd";
            // for /f … in ('befehl') bzw. (`befehl`): cmd startet den Befehl intern – die Optionen lassen sich nicht zuordnen.
            // Eigene Meldung nur, wenn alle nicht zugeordneten Nennungen genau in einem solchen in-Block liegen.
            var blocks = Regex.Matches(lower, @"(?<![a-z0-9_])for\s+/f(?![a-z0-9_])[^(]*?(?<![a-z0-9_])in\s*\(\s*(['`])(.*?)\1\s*\)", RegexOptions.None, RxTimeout);
            if (blocks.Count > 0)
            {
                var outside = new StringBuilder(lower);
                string inside = "";
                foreach (Match b in blocks)
                {
                    var g = b.Groups[2];
                    inside += " " + g.Value;
                    for (int i = g.Index; i < g.Index + g.Length; i++) outside[i] = ' ';
                }
                string rest = outside.ToString();
                bool restOk = CommandPositions(rest, @"cmd") <= cmds && CommandPositions(rest, @"(?:powershell|pwsh)") <= shells;
                if (restOk)
                {
                    string inner = CommandPositions(inside, @"(?:powershell|pwsh)") > 0 ? "PowerShell" : "cmd";
                    return "Verschachtelter Aufruf von " + inner + " in „for /f … in ('…')“ ist nicht eindeutig prüfbar und läuft deshalb nicht mit Adminrechten (auch mit " + (inner == "cmd" ? "/d" : "-NoProfile") + "). Bitte das Ergebnis über eine eigene Zeile bzw. eine Datei weitergeben";
                }
            }
            return "Aufruf von " + which + " an einer Stelle, die die Prüfung nicht eindeutig zuordnen kann (z. B. verschachtelt in Anführungszeichen) – nicht eindeutig prüfbar und deshalb nicht mit Adminrechten (auch mit " + (which == "cmd" ? "/d" : "-NoProfile") + ")";
        }

        /// <summary>Wie oft steht die Shell (Name, auch mit Pfad und .exe) an einer Stelle, an der ein Befehl beginnt?</summary>
        private static int CommandPositions(string lower, string shell)
        {
            const string Lead = @"(?:^|[&|(;'`{]|(?<![a-z0-9_])(?:call|start|do|else|/c|/k|/r|-c|-command|-e|-ec|-encodedcommand|-f|-file)(?![a-z0-9_])" +
                // if-Bedingung: danach beginnt ein Befehl
                @"|(?<![a-z0-9_])if\s+(?:/i\s+)?(?:not\s+)?(?:(?:exist|defined|errorlevel|cmdextversion)\s+(?:""[^""]*""|\S+)|\S+\s*==\s*\S+|\S+\s+(?:equ|neq|lss|leq|gtr|geq)\s+\S+)\s)";
            // nach „start“: Schalter (/b, /min …) und ein Fenstertitel in Anführungszeichen dürfen folgen. Jede Alternative
            // beginnt mit einem anderen Zeichen (Leerraum/@/' – / – "), damit es nur eine Zerlegung gibt (kein Aufschaukeln
            // bei langen Eingaben); ein einzelnes " direkt vor dem Namen erlaubt "? unten.
            const string Gap = @"(?:[\s@']|/[a-z0-9]+\s|""[^""]*""\s)*";
            // optional ein Pfad davor (auch in Anführungszeichen mit Leerzeichen)
            const string Dir = @"(?:[^\s""&|()]*[\\/]|""[^""]*[\\/])?";
            return Regex.Matches(lower, Lead + Gap + Dir + @"""?" + shell + @"(?:\.exe)?(?![a-z0-9_.\-\\/])", RegexOptions.None, RxTimeout).Count;
        }

        /// <summary>
        /// Skripte, die eine freigegebene Aktion aufruft (.bat, .cmd, .ps1), laufen mit Adminrechten. Geprüft wird jeder
        /// erkannte Shell-Start einzeln, der Schutzschalter muss bei genau diesem Aufruf als Startoption wirken: PowerShell
        /// mit -NoProfile vor dem Skriptargument (-Command, -File …), cmd mit /d vor /c. .bat/.cmd zeilenweise (Zeilen mit
        /// ^ am Ende zusammengesetzt), .ps1 mit dem PowerShell-Parser von Windows als Ganzes (vollständige Anweisungen auch
        /// über mehrere Zeilen). Was zur Laufzeit zu einem Shell-Start werden kann, ohne dass die Prüfung es sicher
        /// einordnen kann, gilt als ungeklärt und wird abgelehnt. Grenze: was Programme (.exe) oder nachgeladene Dateien
        /// intern starten, lässt sich so nicht prüfen (siehe Liesmich). null = in Ordnung, sonst der Grund.
        /// </summary>
        private static string ScriptShells(IEnumerable<string> files)
        {
            foreach (string f in files)
            {
                string ext = (Path.GetExtension(f) ?? "").ToLowerInvariant();
                if (ext != ".bat" && ext != ".cmd" && ext != ".ps1") continue;
                string name = Path.GetFileName(f);
                try
                {
                    if (!File.Exists(f)) continue;
                    if (new FileInfo(f).Length > 1024 * 1024) return "Skript " + name + " ist zu groß für die Prüfung (höchstens 1 MB)";
                    if (ext == ".ps1")
                    {
                        string r = Ps1Script(File.ReadAllText(f));
                        if (r != null) return "Skript " + name + ", " + r;
                        continue;
                    }
                    int n = 0, first = 0;
                    var logical = new StringBuilder();
                    foreach (string line in File.ReadAllLines(f))
                    {
                        n++;
                        if (logical.Length == 0) first = n;
                        string t = line.TrimEnd();
                        // ^ am Zeilenende: die Anweisung geht in der nächsten Zeile weiter
                        if (t.EndsWith("^") && !t.EndsWith("^^")) { logical.Append(t, 0, t.Length - 1); continue; }
                        logical.Append(line);
                        string r = BatLine(logical.ToString());
                        logical.Clear();
                        if (r != null) return "Skript " + name + ", Zeile " + first + ": " + r;
                    }
                    if (logical.Length > 0)
                    {
                        string r = BatLine(logical.ToString());
                        if (r != null) return "Skript " + name + ", Zeile " + first + ": " + r;
                    }
                }
                catch (RegexMatchTimeoutException) { return "Skript " + name + ": " + RxTimeoutReason; }
                catch (Exception ex) { return "Skript " + name + " nicht prüfbar (" + ex.Message + ")"; }
            }
            return null;
        }

        private static readonly Regex ShellName = new Regex(@"(?<![a-z0-9_$.\-:])(powershell|pwsh|cmd)(\.exe)?(?![a-z0-9_.\-\\/])", RegexOptions.None, RxTimeout);

        /// <summary>Zeile einer .bat/.cmd: Aufrufe strukturiert wie im Aktionstext prüfen.</summary>
        private static string BatLine(string line)
        {
            string l = line.Trim().ToLowerInvariant().Replace("^", "");
            string first = l.TrimStart('@', ' ', '\t');
            if (first == "rem" || first.StartsWith("rem ") || first.StartsWith("::") || first.StartsWith(":")) return null;
            string r = ProfileShell(line);
            if (r != null) return r;
            // Eine Variable mit Shell-Namen kann später als Befehl laufen (%x% -Command …) – das sieht die Prüfung nicht
            if (Regex.IsMatch(first, @"^set(\s|/)", RegexOptions.None, RxTimeout) && (ShellName.IsMatch(first) || first.Contains("comspec")))
                return "nennt eine Shell in einer Variablen – ein späterer Aufruf darüber ist nicht eindeutig prüfbar (Aufruf bitte direkt mit -NoProfile bzw. cmd /d schreiben)";
            return null;
        }

        // ---------------- PowerShell-Skripte (.ps1) über den Parser von Windows ----------------

        /// <summary>Cmdlets, deren Argumente nur gelesen oder ausgegeben werden: eine Shell-Nennung darin startet nichts.</summary>
        private static readonly HashSet<string> HarmlessCmdlets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get-process", "stop-process", "wait-process", "debug-process",
            "write-host", "write-verbose", "write-warning", "write-error", "write-debug", "write-information", "write-progress"
        };

        /// <summary>Schalter von Start-Process ohne Wert (alle anderen benannten Parameter nehmen einen Wert).</summary>
        private static readonly HashSet<string> StartProcessSwitches = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wait", "nonewwindow", "passthru", "loaduserprofile", "useshellexecute", "usenewenvironment", "whatif", "confirm"
        };

        private static bool AnyAst(object ast) { return true; }

        private static object Prop(object o, string name)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(name);
            return p != null ? p.GetValue(o, null) : null;
        }

        private static string Kind(object ast) { return ast != null ? ast.GetType().Name : ""; }

        private static string AstText(object ast) { return (Prop(Prop(ast, "Extent"), "Text") as string) ?? ""; }

        private static int AstLine(object ast)
        {
            object v = Prop(Prop(ast, "Extent"), "StartLineNumber");
            return v is int ? (int)v : 0;
        }

        /// <summary>Wert einer Konstante (Bareword oder Text ohne Variablen); null = zur Laufzeit berechnet.</summary>
        private static string Constant(object ast)
        {
            string k = Kind(ast);
            if (k == "StringConstantExpressionAst") return Prop(ast, "Value") as string;
            if (k == "ConstantExpressionAst") { object v = Prop(ast, "Value"); return v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null; }
            return null;
        }

        /// <summary>powershell, pwsh oder cmd – auch mit Pfad und .exe; null = keine Shell.</summary>
        private static string ShellOf(string command)
        {
            if (string.IsNullOrEmpty(command)) return null;
            string n;
            try { n = Path.GetFileName(command.Trim().Trim('"', '\'').Replace('/', '\\')).ToLowerInvariant(); }
            catch { return null; }
            if (n.EndsWith(".exe")) n = n.Substring(0, n.Length - 4);
            return n == "powershell" || n == "pwsh" || n == "cmd" ? n : null;
        }

        private static string Ps1Script(string text)
        {
            System.Reflection.Assembly sma;
            try { sma = System.Reflection.Assembly.Load("System.Management.Automation, Version=3.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35"); }
            catch (Exception ex) { return "nicht prüfbar: PowerShell-Parser nicht verfügbar (" + ex.Message + ")"; }
            Type parser = sma.GetType("System.Management.Automation.Language.Parser", true);
            Type astType = sma.GetType("System.Management.Automation.Language.Ast", true);
            var parse = parser.GetMethods().First(m => m.Name == "ParseInput" && m.GetParameters().Length == 3);
            var args = new object[] { text, null, null };
            object root = parse.Invoke(null, args);
            var errors = args[2] as Array;
            if (errors != null && errors.Length > 0)
                return "Zeile " + AstLine(errors.GetValue(0)) + ": Skript ist fehlerhaft und deshalb nicht eindeutig prüfbar (" + (Prop(errors.GetValue(0), "Message") ?? "") + ")";

            Type funcType = typeof(Func<,>).MakeGenericType(astType, typeof(bool));
            Delegate predicate = Delegate.CreateDelegate(funcType, typeof(ActionSeal).GetMethod("AnyAst", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
            var findAll = astType.GetMethod("FindAll", new[] { funcType, typeof(bool) });
            var all = ((System.Collections.IEnumerable)findAll.Invoke(root, new object[] { predicate, true })).Cast<object>().ToList();

            var accounted = new HashSet<object>(); // Namen geprüfter Shell-Aufrufe und FilePath von Start-Process
            foreach (object c in all.Where(x => Kind(x) == "CommandAst"))
            {
                var elements = ((System.Collections.IEnumerable)Prop(c, "CommandElements")).Cast<object>().ToList();
                if (elements.Count == 0) continue;
                string cmdName = Constant(elements[0]);
                if (cmdName == null && Kind(elements[0]) == "ExpandableStringExpressionAst") cmdName = Prop(elements[0], "Value") as string; // "$PSHOME\powershell.exe"
                string shell = ShellOf(cmdName);
                int line = AstLine(c);
                if (shell != null)
                {
                    accounted.Add(elements[0]);
                    var list = ElementArgs(elements.Skip(1));
                    string r = shell == "cmd" ? CmdArgs(list) : PsArgs(list);
                    if (r != null) return "Zeile " + line + ": " + (shell == "cmd" ? "cmd" : "PowerShell") + "-Aufruf nicht in Ordnung – " + r;
                    continue;
                }
                string lower = (cmdName ?? "").ToLowerInvariant();
                if (lower == "start-process" || lower == "saps" || lower == "start")
                {
                    string r = StartProcess(elements, accounted);
                    if (r != null) return "Zeile " + line + ": Start-Process – " + r;
                }
            }

            // Übrige Nennungen: nur harmlos, wenn sie Argument eines rein lesenden oder ausgebenden Cmdlets bzw. von throw sind
            foreach (object a in all)
            {
                string k = Kind(a);
                string value;
                if (k == "StringConstantExpressionAst" || k == "ExpandableStringExpressionAst") value = (Prop(a, "Value") as string ?? "").ToLowerInvariant();
                else if (k == "VariableExpressionAst") value = AstText(a).ToLowerInvariant();
                else continue;
                bool mentions = ShellName.IsMatch(value) || value.Contains("comspec") || (k == "VariableExpressionAst" && value.Contains("pshome"));
                if (!mentions || InsideAccounted(a, accounted)) continue;
                if (Harmless(a)) continue;
                return "Zeile " + AstLine(a) + ": nennt eine Shell („" + Shorten(AstText(a)) + "“), ohne dass ein prüfbarer Aufruf erkennbar ist (z. B. Variable, Invoke-Expression, [Diagnostics.Process] oder Inhalt eines Shell-Befehls) – nicht eindeutig prüfbar. Aufruf bitte direkt bzw. mit Start-Process und festen Argumenten schreiben.";
            }
            return null;
        }

        private static string Shorten(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            return s.Length > 60 ? s.Substring(0, 57) + "..." : s;
        }

        /// <summary>Ist die Nennung (oder ein Ausdruck, in dem sie steht) Name eines geprüften Shell-Aufrufs bzw. FilePath?</summary>
        private static bool InsideAccounted(object ast, HashSet<object> accounted)
        {
            for (object x = ast; x != null; x = Prop(x, "Parent"))
            {
                if (accounted.Contains(x)) return true;
                string k = Kind(x);
                if (k == "CommandAst" || k.EndsWith("StatementAst")) return false;
            }
            return false;
        }

        /// <summary>Gehört die Nennung zu einem harmlosen Cmdlet (nicht als dessen Name) oder zu throw?</summary>
        private static bool Harmless(object ast)
        {
            object p = Prop(ast, "Parent");
            object child = ast;
            while (p != null)
            {
                string k = Kind(p);
                if (k == "ThrowStatementAst") return true;
                if (k == "CommandAst")
                {
                    var elements = ((System.Collections.IEnumerable)Prop(p, "CommandElements")).Cast<object>().ToList();
                    if (elements.Count == 0 || ReferenceEquals(elements[0], child)) return false;
                    string name = Constant(elements[0]);
                    return name != null && HarmlessCmdlets.Contains(name);
                }
                if (k.EndsWith("StatementAst") || k == "ScriptBlockAst" || k == "NamedBlockAst") return false;
                child = p;
                p = Prop(p, "Parent");
            }
            return false;
        }

        /// <summary>
        /// Argumente eines Aufrufs als Texte in Reihenfolge; null-Einträge = zur Laufzeit berechnet (Variable, Ausdruck).
        /// Ein Skriptblock zählt als Skriptargument (sein Inhalt wird als eigener Aufruf geprüft).
        /// </summary>
        private static List<string> ElementArgs(IEnumerable<object> elements)
        {
            var list = new List<string>();
            foreach (object e in elements)
            {
                if (Kind(e) == "CommandParameterAst")
                {
                    list.Add("-" + ((Prop(e, "ParameterName") as string) ?? ""));
                    object arg = Prop(e, "Argument"); // -Schalter:Wert
                    if (arg != null) list.Add(Constant(arg));
                    continue;
                }
                if (Kind(e) == "ScriptBlockExpressionAst") { list.Add("{Skriptblock}"); continue; }
                string c = Constant(e);
                if (c == "--%") { list.Add(null); break; } // Stop-Parsing: der Rest wird roh übergeben
                list.Add(c);
            }
            return list;
        }

        /// <summary>Wirksame Startoptionen von powershell/pwsh: -NoProfile muss vor dem Skriptargument stehen.</summary>
        private static string PsArgs(List<string> args)
        {
            bool noProfile = false;
            for (int i = 0; i < args.Count; i++)
            {
                // berechneter Wert: erst nach -NoProfile unbedenklich (dann ist er Skriptargument oder dessen Teil)
                if (args[i] == null) return noProfile ? null : "Startoptionen werden zur Laufzeit berechnet – -NoProfile muss davor fest im Skript stehen";
                string l = args[i].ToLowerInvariant();
                if (l.StartsWith("/")) l = "-" + l.Substring(1);
                if (ValueSwitches.Contains(l)) { i++; continue; }
                if (l.Length >= 4 && l.StartsWith("-") && "noprofile".StartsWith(l.Substring(1))) { noProfile = true; continue; }
                bool scriptStart = !l.StartsWith("-") || l == "-c" || l == "-e" || l == "-ec" || l == "-f" ||
                    (l.Length >= 3 && ("-command".StartsWith(l) || "-file".StartsWith(l) || "-encodedcommand".StartsWith(l)));
                if (scriptStart) return noProfile ? null : "-NoProfile fehlt vor dem Skriptargument (\"" + Shorten(args[i]) + "\")";
            }
            return noProfile ? null : "-NoProfile fehlt (interaktive PowerShell)";
        }

        /// <summary>cmd: /d muss vor /c, /k bzw. /r stehen.</summary>
        private static string CmdArgs(List<string> args)
        {
            bool d = false;
            foreach (string a in args)
            {
                if (a == null) return d ? null : "Optionen werden zur Laufzeit berechnet – /d muss davor fest im Skript stehen";
                string l = a.ToLowerInvariant();
                if (l == "/d") { d = true; continue; }
                if (l == "/c" || l == "/k" || l == "/r") return d ? null : "/d fehlt vor " + a;
                if (!l.StartsWith("/")) break;
            }
            return d ? null : "/d fehlt";
        }

        /// <summary>
        /// Start-Process: Programm aus -FilePath (oder erstem Positionsargument), Startoptionen nur aus der zugehörigen
        /// -ArgumentList (oder zweitem Positionsargument). Ist das Programm eine Shell, müssen beide als feste Werte im
        /// Skript stehen; berechnete Werte gelten als ungeklärt. null = in Ordnung.
        /// </summary>
        private static string StartProcess(List<object> elements, HashSet<object> accounted)
        {
            object filePath = null, argumentList = null;
            int positional = 0;
            for (int i = 1; i < elements.Count; i++)
            {
                object e = elements[i];
                if (Kind(e) == "CommandParameterAst")
                {
                    string p = ((Prop(e, "ParameterName") as string) ?? "").ToLowerInvariant();
                    object value = Prop(e, "Argument");
                    if (StartProcessSwitches.Contains(p)) continue;
                    if (value == null && i + 1 < elements.Count) value = elements[++i];
                    if (p.Length > 0 && ("filepath".StartsWith(p) || p == "pspath" || p == "path")) filePath = value;
                    else if (p.Length > 0 && ("argumentlist".StartsWith(p) || p == "args")) argumentList = value;
                    continue;
                }
                if (positional == 0) filePath = e; else if (positional == 1) argumentList = e;
                positional++;
            }
            if (filePath == null) return null;
            string program = Constant(filePath);
            if (program == null && Kind(filePath) == "ExpandableStringExpressionAst") program = Prop(filePath, "Value") as string;
            string shell = ShellOf(program);
            if (shell == null) return null; // kein Shell-Start (berechnete Programme mit Shell-Namen fallen bei den Nennungen auf)
            accounted.Add(filePath);
            List<string> args = new List<string>();
            if (argumentList != null)
            {
                var parts = new List<object>();
                if (Kind(argumentList) == "ArrayLiteralAst") parts.AddRange(((System.Collections.IEnumerable)Prop(argumentList, "Elements")).Cast<object>());
                else parts.Add(argumentList);
                foreach (object part in parts)
                {
                    string v = Constant(part);
                    if (v == null) { args.Add(null); continue; } // berechnet: zählt nur, wenn -NoProfile bzw. /d nicht schon davor steht
                    foreach (string w in Words(v)) args.Add(w.Trim('"'));
                }
            }
            return shell == "cmd" ? CmdArgs(args) : PsArgs(args);
        }

        private static List<string> Words(string s)
        {
            var w = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length) break;
                int start = i;
                if (s[i] == '"') { i = s.IndexOf('"', i + 1); i = i < 0 ? s.Length : i + 1; }
                else while (i < s.Length && !char.IsWhiteSpace(s[i]) && "&|<>".IndexOf(s[i]) < 0) i++;
                if (i == start) i++; // Sonderzeichen
                w.Add(s.Substring(start, i - start));
                if (w.Count > 200) break;
            }
            return w;
        }

        /// <summary>Befehl wie cmd.exe auflösen (Arbeitsordner, dann PATH; ohne Endung mit PATHEXT). null = nicht gefunden bzw. eingebaut.</summary>
        private static string Resolve(string name, string workDir)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            var exts = new List<string>();
            if (Path.HasExtension(name)) exts.Add("");
            foreach (string e in (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';'))
                if (e.Length > 0) exts.Add(e);
            var dirs = new List<string>();
            if (Path.IsPathRooted(name)) dirs.Add("");
            else
            {
                if (!string.IsNullOrEmpty(workDir)) dirs.Add(workDir);
                foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                    if (d.Trim().Length > 0) dirs.Add(Environment.ExpandEnvironmentVariables(d.Trim().Trim('"')));
            }
            foreach (string d in dirs)
                foreach (string e in exts)
                {
                    try
                    {
                        string p = d.Length == 0 ? name + e : Path.Combine(d, name + e);
                        if (File.Exists(p)) return Path.GetFullPath(p);
                    }
                    catch { }
                }
            return null;
        }

        /// <summary>Datei unter Windows bzw. „Programme“: nur der Pfad zählt, nicht der Inhalt (sonst nach jedem Windows-Update ungültig).</summary>
        private static bool SystemFile(string path)
        {
            foreach (var f in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string d = Environment.GetFolderPath(f);
                if (d.Length > 3 && path.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string FileHash(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return "fehlt";
                using (var sha = SHA256.Create())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
            }
            catch (Exception ex) { return "unlesbar:" + ex.GetType().Name; }
        }

        // ------------------------------------------------------------------ Geschützte Ordner

        private static readonly HashSet<string> TrustedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "S-1-5-32-544",  // Administratoren
            "S-1-5-18",      // SYSTEM
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", // TrustedInstaller
            "S-1-3-0"        // ERSTELLER-BESITZER (gilt nur für Neues, der Besitzer wird separat geprüft)
        };

        private const FileSystemRights FileWrite = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
                                                   FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        private const FileSystemRights DirWrite = FileSystemRights.CreateFiles | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
                                                  FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        private const FileSystemRights RootWrite = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        /// <summary>Erste Datei, die in einem ungeschützten Ordner liegt (eingebaute bzw. nicht gefundene Befehle zählen nicht), sonst null.</summary>
        private static string Unprotected(IEnumerable<string> files)
        {
            foreach (string f in files)
            {
                if (string.IsNullOrEmpty(f) || f.StartsWith("?")) continue;
                if (!File.Exists(f)) continue; // nicht vorhanden: nichts auszutauschen, der Start scheitert ohnehin
                if (!Protected(f)) return f;
            }
            return null;
        }

        public static string UnprotectedMessage(string file)
        {
            string dir = Path.GetDirectoryName(file) ?? file;
            return "„" + file + "“ liegt in einem Ordner, in den auch Programme ohne Administratorrechte schreiben dürfen. " +
                   "Mit Administratorrechten startet Monitor Dimmer nur Skripte und Programme aus geschützten Ordnern " +
                   "(z. B. unter „Programme“). Ordner schützen (Eingabeaufforderung als Administrator): " +
                   "icacls \"" + dir + "\" /setowner *S-1-5-32-544 /T  und  icacls \"" + dir +
                   "\" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-11:(OI)(CI)RX";
        }

        /// <summary>
        /// Netzwerkpfad (fail-closed, globale Vorgabe „Netzwerkpfade erkennen“): \\Server\…, //Server/…, Gerätepfade außer
        /// \\?\X:\ und verbundene Netzlaufwerke. Scheitert die Laufwerksabfrage oder ist der Typ Unknown bzw.
        /// NoRootDirectory, gilt der Pfad ebenfalls als Netzwerkpfad.
        /// </summary>
        public static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            string p = Environment.ExpandEnvironmentVariables(path.Trim()).Replace('/', '\\');
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
            if (p.StartsWith(@"\\?\") && p.Length >= 7 && p[5] == ':') p = p.Substring(4); // \\?\X:\ → X:\
            if (p.StartsWith(@"\\")) return true; // UNC und alle übrigen Gerätepfade
            if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                string root = Path.GetPathRoot(p);
                if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') return true;
                var t = new DriveInfo(root).DriveType;
                return t == DriveType.Network || t == DriveType.Unknown || t == DriveType.NoRootDirectory;
            }
            catch { return true; }
        }

        /// <summary>Datei und alle Ordner darüber nur von Administratoren änderbar? Im Zweifel (nicht lesbar) nein.</summary>
        public static bool Protected(string file)
        {
            try
            {
                if (file.StartsWith("\\\\")) return false; // Netzwerkfreigabe: Rechte auf dem Server nicht prüfbar
                if (IsNetworkPath(file)) return false; // auch verbundene Netzlaufwerke; im Zweifel wie Netzwerk
                if (!Safe(File.GetAccessControl(file), FileWrite)) return false;
                var d = new FileInfo(file).Directory;
                while (d != null)
                {
                    bool root = d.Parent == null;
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) return false; // über Verknüpfung: Ziel kann wechseln
                    if (!Safe(Directory.GetAccessControl(d.FullName), root ? RootWrite : DirWrite)) return false;
                    d = d.Parent;
                }
                return true;
            }
            catch { return false; }
        }

        private static bool Safe(FileSystemSecurity sec, FileSystemRights dangerous)
        {
            // Der Besitzer darf die Rechte immer ändern: muss also selbst vertrauenswürdig sein
            var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null || !TrustedSids.Contains(owner.Value)) return false;
            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue; // gilt nur für Unterobjekte
                if ((rule.FileSystemRights & dangerous) == 0) continue;
                if (!TrustedSids.Contains(rule.IdentityReference.Value)) return false;
            }
            return true;
        }
    }
}
