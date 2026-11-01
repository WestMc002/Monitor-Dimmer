// Monitor Dimmer – Programmstart
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Monitor Dimmer")]
[assembly: System.Reflection.AssemblyProduct("Monitor Dimmer")]
// Version (Haupt.Neben.Jahr.MonatTag) erzeugt Erstellen.bat in VersionInfo.cs (AppInfo.Version)
[assembly: System.Reflection.AssemblyDescription("Monitor Dimmer")]
[assembly: System.Reflection.AssemblyCopyright("© 2026 Günther Schoder")]

namespace MonitorDimmer
{
    internal static class Program
    {
        private static TrayApp _app;
        private static string _baseDir;

        [STAThread]
        private static void Main(string[] args)
        {
            _baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

            // Screenshots mit Beispieldaten (hell und dunkel) für GitHub, z. B. aus der Erstellen.bat
            if (args.Length > 0 && string.Equals(args[0], "--screenshots", StringComparison.OrdinalIgnoreCase))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string outDir = args.Length > 1 ? args[1] : Path.Combine(_baseDir, "Screenshots");
                Environment.ExitCode = Demo.RunScreenshots(outDir) ? 0 : 1;
                return;
            }
            string configName = "Default";
            foreach (var a in args)
            {
                if (a.StartsWith("/config:", StringComparison.OrdinalIgnoreCase)) configName = a.Substring(8);
            }

            bool created;
            using (var mutex = new Mutex(true, "Local\\MonitorDimmer_" + configName, out created))
            {
                if (!created)
                {
                    MessageBox.Show("Der Monitor Dimmer läuft bereits (Symbol im Infobereich der Taskleiste).",
                        "Monitor Dimmer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var migrated = MigrateLayout(_baseDir);
                Log.Init(Path.Combine(_baseDir, "TraceLogs", configName + ".log"));
                foreach (var m in migrated) Log.Info(m);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => HandleCrash(e.Exception, false);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => HandleCrash(e.ExceptionObject as Exception, true);

                try
                {
                    _app = new TrayApp(_baseDir, configName);
                }
                catch (Exception ex)
                {
                    Log.Error("Start fehlgeschlagen: {0}", ex);
                    WriteCrashFile(ex);
                    MessageBox.Show("Der Monitor Dimmer konnte nicht starten:\r\n\r\n" + ex.Message,
                        "Monitor Dimmer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                Application.Run(_app);
            }
        }

        /// <summary>
        /// Zieht die Dateien aus der alten Ordnerstruktur (z. B. Configurations\MonitorDimmer) eine Ebene höher
        /// und entfernt die leeren Unterordner. Vorhandene Dateien am neuen Ort werden nie überschrieben.
        /// </summary>
        private static List<string> MigrateLayout(string baseDir)
        {
            var messages = new List<string>();
            foreach (var top in new[] { "Configurations", "Resources", "TraceLogs", "UnhandledExceptions" })
            {
                string oldDir = Path.Combine(baseDir, top, "MonitorDimmer");
                if (!Directory.Exists(oldDir)) continue;
                string newDir = Path.Combine(baseDir, top);
                int moved = 0, skipped = 0;
                foreach (var file in Directory.GetFiles(oldDir))
                {
                    string target = Path.Combine(newDir, Path.GetFileName(file));
                    try
                    {
                        if (File.Exists(target)) { skipped++; continue; }
                        File.Move(file, target);
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        messages.Add("Umzug fehlgeschlagen: " + file + " (" + ex.Message + ")");
                    }
                }
                try
                {
                    if (Directory.GetFileSystemEntries(oldDir).Length == 0) Directory.Delete(oldDir);
                }
                catch { }
                messages.Add(string.Format("Ordnerstruktur: {0} Dateien von {1}\\MonitorDimmer nach {1} verschoben{2}",
                    moved, top, skipped > 0 ? ", " + skipped + " bleiben dort (Datei existiert schon am neuen Ort)" : ""));
            }
            return messages;
        }

        private static void HandleCrash(Exception ex, bool fatal)
        {
            Log.Error("Unerwarteter Fehler{0}: {1}", fatal ? " (Programm wird beendet)" : "", ex);
            WriteCrashFile(ex);
            if (fatal && _app != null) _app.EmergencyRestore();
        }

        private static void WriteCrashFile(Exception ex)
        {
            try
            {
                string dir = Path.Combine(_baseDir, "UnhandledExceptions");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt"),
                    ex == null ? "Unbekannter Fehler" : ex.ToString());
            }
            catch { }
        }
    }

    /// <summary>Einfaches Fenster zur Anzeige von Text (Monitorinfo, Spieleliste).</summary>
    internal static class TextWindow
    {
        public static void Show(string title, string text)
        {
            var form = new Form
            {
                Text = "Monitor Dimmer - " + title,
                Width = 900,
                Height = 600,
                StartPosition = FormStartPosition.CenterScreen,
                ShowIcon = false
            };
            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10f),
                Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n")
            };
            form.Controls.Add(box);
            Theme.Apply(form);
            form.Shown += (s, e) => { box.SelectionStart = 0; box.SelectionLength = 0; form.Activate(); };
            form.Show();
        }
    }
}
