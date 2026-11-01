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
    /// Autostart über die Aufgabenplanung mit höchsten Rechten. So laufen auch die Skripte
    /// (net stop TeamViewer usw.) mit Adminrechten, ohne bei jeder Anmeldung nachzufragen.
    /// </summary>
    internal static class Autostart
    {
        public const string TaskName = "Monitor Dimmer";

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
                var psi = new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + TaskName + "\" /XML")
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
            return Enable(exePath) ? "Autostart wurde auf den neuen Programmordner angepasst." : "Autostart konnte nicht angepasst werden.";
        }

        public static bool IsEnabled()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + TaskName + "\"")
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
            string tmp = Path.Combine(Path.GetTempPath(), "MonitorDimmerTask.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try
            {
                return RunSchtasks("/Create /F /TN \"" + TaskName + "\" /XML \"" + tmp + "\"");
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        public static bool Disable()
        {
            return RunSchtasks("/Delete /F /TN \"" + TaskName + "\"");
        }

        private static bool RunSchtasks(string args)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
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
