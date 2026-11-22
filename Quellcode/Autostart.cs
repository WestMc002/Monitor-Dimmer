// Monitor Dimmer – Autostart mit Adminrechten über die Aufgabenplanung
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace MonitorDimmer
{
    /// <summary>
    /// Autostart über die Aufgabenplanung mit höchsten Rechten. So laufen auch Aktionen wie „Dienst beenden“
    /// (net stop TeamViewer usw.) mit Adminrechten, ohne bei jeder Anmeldung nachzufragen.
    /// Abweichung von den globalen Vorgaben („Kein Autostart mit Administratorrechten“, entschieden 22.11.2026, wie Sync
    /// Pilot): Aktionen mit Adminrechten nur mit Prüfwert (ActionSeal), Programme starten ohne Adminrechte, die Aufgabe
    /// nur für eine EXE in einem geschützten Ordner, eingetragen über PowerShell -EncodedCommand (keine Datei, die ein
    /// anderes Programm zwischen Schreiben und Eintragen austauschen könnte).
    /// </summary>
    internal static class Autostart
    {
        public const string TaskName = "Monitor Dimmer";

        /// <summary>schtasks.exe nur über den festen Systempfad (nie über den Suchpfad).</summary>
        private static string SchTasks { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"); } }

        /// <summary>Projektordner (Erstellen.bat neben der EXE): beim Entwickeln nicht sperren.</summary>
        private static bool InProjectFolder(string exePath)
        {
            try { return File.Exists(Path.Combine(Path.GetDirectoryName(exePath), "Erstellen.bat")); } catch { return false; }
        }

        /// <summary>
        /// Darf die Aufgabe diese EXE mit Adminrechten starten? Nur aus einem geschützten Ordner (sonst könnte ein Programm
        /// ohne Adminrechte die EXE austauschen). null = ja, sonst der Grund.
        /// </summary>
        public static string ExeBlocker(string exePath)
        {
            if (InProjectFolder(exePath) || ActionSeal.Protected(exePath)) return null;
            return "Monitor Dimmer liegt in einem Ordner, in den auch Programme ohne Administratorrechte schreiben dürfen (" +
                   Path.GetDirectoryName(exePath) + "). Mit Adminrechten startet Windows nur eine EXE aus einem geschützten Ordner, " +
                   "z. B. unter „Programme“. Bitte Monitor Dimmer dorthin verschieben und den Autostart dann einrichten.";
        }

        public static bool IsElevated
        {
            get
            {
                using (var id = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
        }

        /// <summary>Programmpfad, den die Autostart-Aufgabe startet (null, wenn keine Aufgabe existiert).</summary>
        public static string GetTaskCommand()
        {
            try
            {
                var psi = new ProcessStartInfo(SchTasks, "/Query /TN \"" + TaskName + "\" /XML")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    string xml = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);
                    if (p.ExitCode != 0) return null;
                    int a = xml.IndexOf("<Command>", StringComparison.OrdinalIgnoreCase);
                    int b = xml.IndexOf("</Command>", StringComparison.OrdinalIgnoreCase);
                    if (a < 0 || b <= a) return null;
                    string cmd = xml.Substring(a + 9, b - a - 9).Trim().Trim('"');
                    return System.Net.WebUtility.HtmlDecode(cmd);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Wurde der Programmordner verschoben, zeigt die Autostart-Aufgabe noch auf den alten Ort.
        /// Dann wird sie auf die aktuelle exe umgestellt. Liefert eine Meldung oder null.
        /// </summary>
        public static string FixTaskPath(string exePath)
        {
            string cmd = GetTaskCommand();
            if (cmd == null) return null; // kein Autostart eingerichtet
            if (string.Equals(Path.GetFullPath(cmd), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase)) return null;
            Log.Info("Autostart zeigt auf \"{0}\" statt \"{1}\" - wird angepasst", cmd, exePath);
            if (!IsElevated)
                return "Der Programmordner wurde verschoben. Bitte im Menü \u201eMit Windows starten\u201c einmal aus- und wieder einschalten.";
            string blocker = ExeBlocker(exePath);
            if (blocker != null)
            {
                Log.Warn("Autostart nicht angepasst: {0}", blocker);
                return "Der Programmordner wurde verschoben. Der Autostart mit Adminrechten wurde nicht angepasst, weil der neue Ordner nicht geschützt ist.";
            }
            return Enable(exePath) ? "Autostart wurde auf den neuen Programmordner angepasst." : "Autostart konnte nicht angepasst werden.";
        }

        public static bool IsEnabled()
        {
            try
            {
                var psi = new ProcessStartInfo(SchTasks, "/Query /TN \"" + TaskName + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);
                    return p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool Enable(string exePath)
        {
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>Monitor Dimmer bei der Anmeldung starten</Description></RegistrationInfo>\r\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId><Delay>PT15S</Delay></LogonTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>7</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>\"" + SecurityElement.Escape(exePath) + "\"</Command>" +
                "<WorkingDirectory>" + SecurityElement.Escape(Path.GetDirectoryName(exePath)) + "</WorkingDirectory></Exec></Actions>\r\n" +
                "</Task>\r\n";
            if (ExeBlocker(exePath) != null) return false;
            // Aufgabenbeschreibung steht im Skript selbst (-EncodedCommand); das Skript schreibt sie unter zufälligem Namen
            // in %SystemRoot%\Temp (dort schreiben nur Administratoren), trägt sie ein und löscht sie wieder
            string ps =
                "$ErrorActionPreference = 'Stop'\r\n" +
                "$xml = " + ActionSeal.Ps(xml) + "\r\n" +
                "$sch = Join-Path ([Environment]::SystemDirectory) 'schtasks.exe'\r\n" +
                "try {\r\n" +
                "  $file = Join-Path (Join-Path $env:SystemRoot 'Temp') ('MonitorDimmer_' + [Guid]::NewGuid().ToString('N') + '.xml')\r\n" +
                "  $fs = New-Object IO.FileStream($file, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)\r\n" +
                "  try { $w = New-Object IO.StreamWriter($fs, [Text.Encoding]::Unicode); $w.Write($xml); $w.Flush() } finally { $fs.Dispose() }\r\n" +
                "  try { & $sch /Create /F /TN " + ActionSeal.Ps(TaskName) + " /XML $file | Out-Null; $rc = $LASTEXITCODE } finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }\r\n" +
                "  exit $rc\r\n" +
                "} catch { exit 1 }";
            int rc = ActionSeal.RunElevated(ps);
            Log.Info("Autostart eintragen -> Exitcode {0}", rc);
            if (rc == -1) Log.Warn("Adminrechte für die Aufgabenplanung wurden abgelehnt");
            return rc == 0;
        }

        public static bool Disable()
        {
            return RunSchtasks("/Delete /F /TN \"" + TaskName + "\"");
        }

        private static bool RunSchtasks(string args)
        {
            var psi = new ProcessStartInfo(SchTasks, args)
            {
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (IsElevated)
            {
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
            }
            else
            {
                psi.UseShellExecute = true;
                psi.Verb = "runas"; // Windows fragt nach Adminrechten
            }
            try
            {
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(15000);
                    Log.Info("schtasks {0} -> Exitcode {1}", args, p.ExitCode);
                    return p.ExitCode == 0;
                }
            }
            catch (Win32Exception)
            {
                Log.Warn("Adminrechte für die Aufgabenplanung wurden abgelehnt");
                return false;
            }
        }
    }
}
