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
        public static string Blocked(ActionDef a)
        {
            if (!Active || a == null || !AdminTypes.Contains(a.Type)) return null;
            if (!Valid) return NotConfirmed;
            if (a.Type == "Command")
            {
                string file = Unprotected(ScriptFiles(a.Value, CommandDir));
                if (file != null) return UnprotectedMessage(file);
            }
            return null;
        }

        /// <summary>Altes Skript (EventHandler) mit Administratorrechten: null = darf laufen, sonst der Grund.</summary>
        public static string BlockedHandler(string exe, string args)
        {
            if (!Active) return null;
            if (!Valid) return NotConfirmed;
            var files = new List<string>();
            if (!string.IsNullOrEmpty(exe)) files.Add(exe);
            files.AddRange(ScriptFiles("\"" + exe + "\" " + args, Path.GetDirectoryName(exe)));
            string file = Unprotected(files);
            return file != null ? UnprotectedMessage(file) : null;
        }

        /// <summary>
        /// „Beendete wieder starten“: Die Liste der beendeten Dienste steht in einer Datei, die jedes Programm ändern kann.
        /// Mit Administratorrechten nur Dienste wieder starten, die eine bestätigte Aktion „Dienst beenden“ dieses Profils nennt.
        /// </summary>
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
                            foreach (string f in ScriptFiles(a.Value, CommandDir)) add("Datei " + f, FileState(f));
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
                    files.AddRange(ScriptFiles("\"" + exe + "\" " + args, SafeDir(exe)));
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
            string name = ValueName(configPath), hash = Hash(config, resources);
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
            foreach (Match m in Regex.Matches(expanded, "\"([^\"]+)\"|((?:[A-Za-z]:\\\\|\\\\\\\\)[^\\s\"]+)"))
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

        /// <summary>Datei und alle Ordner darüber nur von Administratoren änderbar? Im Zweifel (nicht lesbar) nein.</summary>
        public static bool Protected(string file)
        {
            try
            {
                if (file.StartsWith("\\\\")) return false; // Netzwerkfreigabe: Rechte auf dem Server nicht prüfbar
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
