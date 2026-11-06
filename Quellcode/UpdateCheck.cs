// Monitor Dimmer – Update-Prüfung über die GitHub-Releases und Selbst-Update
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MonitorDimmer
{
    /// <summary>Neueste Veröffentlichung auf GitHub.</summary>
    internal sealed class ReleaseInfo
    {
        public string Version, PageUrl, ZipUrl;
    }

    /// <summary>
    /// Update-Prüfung und Selbst-Update wie bei „Fensterpositionen wiederherstellen“.
    /// Fragt nur die öffentliche GitHub-Schnittstelle ab (keine Anmeldung, es werden keine Daten übertragen).
    /// </summary>
    internal static class Updater
    {
        public const string Repository = "WestMc002/Monitor-Dimmer";
        public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

        /// <summary>Arbeitsordner für Download und Hilfsskript.</summary>
        public static string Folder { get { return Path.Combine(Path.GetTempPath(), "Monitor-Dimmer-Update"); } }
        public static string LogFile { get { return Path.Combine(Folder, "Update.log"); } }

        private static void PrepareTls()
        {
            // .NET 4: TLS 1.2 (3072) für github.com einschalten
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
        }

        private static string UserAgent { get { return "Monitor-Dimmer/" + AppInfo.Version; } }

        /// <summary>Neuestes Release (blockiert, im Hintergrund aufrufen).</summary>
        public static ReleaseInfo GetLatest()
        {
            PrepareTls();
            var request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repository + "/releases/latest");
            request.UserAgent = UserAgent;
            request.Accept = "application/vnd.github+json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            string json;
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    json = reader.ReadToEnd();
            }
            catch (WebException ex)
            {
                var http = ex.Response as HttpWebResponse;
                if (http != null && http.StatusCode == HttpStatusCode.NotFound) throw new Exception("Auf GitHub ist noch keine Version veröffentlicht.");
                if (http != null && (int)http.StatusCode == 403) throw new Exception("GitHub hat die Anfrage abgelehnt (zu viele Anfragen). Später erneut versuchen.");
                throw new Exception("GitHub nicht erreichbar: " + ex.Message);
            }

            var info = new ReleaseInfo { PageUrl = ReleasesPage };
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (tag.Success) info.Version = tag.Groups[1].Value.TrimStart('v', 'V');
            // erstes "html_url" ist die Release-Seite (Autor und Anhänge folgen später)
            var page = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github\\.com/[^\"]+)\"");
            if (page.Success) info.PageUrl = page.Groups[1].Value;
            var zip = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\"", RegexOptions.IgnoreCase);
            if (zip.Success) info.ZipUrl = zip.Groups[1].Value;
            if (string.IsNullOrEmpty(info.Version)) throw new Exception("Antwort von GitHub ohne Versionsnummer.");
            return info;
        }

        /// <summary>true, wenn remote neuer ist als die laufende Version.</summary>
        public static bool IsNewer(string remote)
        {
            Version a, b;
            if (!System.Version.TryParse(remote, out a) || !System.Version.TryParse(AppInfo.Version, out b)) return false;
            return a > b;
        }

        /// <summary>Im Projektordner (mit Erstellen.bat) wird nicht automatisch ersetzt: dort entsteht die EXE beim Bauen.</summary>
        public static string InstallBlocker()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(dir, "Erstellen.bat")))
                return "Im Projektordner (mit Erstellen.bat) wird nicht automatisch aktualisiert – dort entsteht die EXE beim Erstellen.";
            return null;
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, "~update-test.tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// ZIP herunterladen (blockiert, im Hintergrund aufrufen), Hilfsskript schreiben und starten. Das Skript wartet,
        /// bis dieses Programm beendet ist, ersetzt EXE und Liesmich und startet das Programm neu.
        /// Danach muss der Aufrufer das Programm beenden.
        /// </summary>
        public static void Install(ReleaseInfo release)
        {
            if (string.IsNullOrEmpty(release.ZipUrl)) throw new Exception("Im Release ist keine ZIP-Datei angehängt.");
            string blocker = InstallBlocker();
            if (blocker != null) throw new Exception(blocker);
            PrepareTls();
            Directory.CreateDirectory(Folder);
            string zip = Path.Combine(Folder, "Monitor-Dimmer-" + release.Version + ".zip");
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                client.DownloadFile(new Uri(release.ZipUrl), zip);
            }
            if (new FileInfo(zip).Length < 1024) throw new Exception("Die heruntergeladene Datei ist unvollständig.");
            string script = Path.Combine(Folder, "Update.ps1");
            File.WriteAllText(script, Script, new UTF8Encoding(true));
            try { File.Delete(LogFile); } catch { }
            string exe = Application.ExecutablePath;
            string target = Path.GetDirectoryName(exe);
            var start = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\" -ProcessId " + Process.GetCurrentProcess().Id +
                " -Zip \"" + zip + "\" -Exe \"" + exe + "\" -Version \"" + release.Version + "\"")
            { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            // Programmordner ohne Schreibrechte (z. B. unter Programme): Hilfsskript mit Administratorrechten starten
            if (!CanWrite(target)) start.Verb = "runas";
            try { Process.Start(start); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) throw new Exception("Update abgebrochen (Administratorrechte nicht erteilt).");
                throw;
            }
        }

        /// <summary>Ergebnis des letzten Updates (nach dem Neustart mit --nach-update) für das Protokoll.</summary>
        public static string LastResult()
        {
            try { return File.Exists(LogFile) ? File.ReadAllText(LogFile, Encoding.UTF8).Trim() : null; } catch { return null; }
        }

        // ---------- Einstellungen: Configurations\Update.ini (auto = automatisch prüfen, last = letzte Prüfung) ----------

        private static string SettingsFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Configurations", "Update.ini"); } }

        private static string Read(string key)
        {
            try
            {
                if (!File.Exists(SettingsFile)) return null;
                foreach (var line in File.ReadAllLines(SettingsFile, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0 && string.Equals(line.Substring(0, i).Trim(), key, StringComparison.OrdinalIgnoreCase)) return line.Substring(i + 1).Trim();
                }
            }
            catch { }
            return null;
        }

        private static void Write(bool auto, DateTime last)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                File.WriteAllLines(SettingsFile, new[]
                {
                    "; Monitor Dimmer - Update-Prüfung über GitHub",
                    "auto=" + (auto ? "true" : "false"),
                    "last=" + (last == DateTime.MinValue ? "" : last.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))
                }, Encoding.UTF8);
            }
            catch (Exception ex) { Log.Error("Update-Einstellung konnte nicht gespeichert werden: {0}", ex.Message); }
        }

        /// <summary>Beim Start automatisch prüfen (höchstens einmal täglich). Standard: an.</summary>
        public static bool Auto
        {
            get { string v = Read("auto"); return v == null || !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase); }
            set { Write(value, LastCheck); }
        }

        /// <summary>Zeitpunkt der letzten erfolgreichen Prüfung (UTC), sonst DateTime.MinValue.</summary>
        public static DateTime LastCheck
        {
            get
            {
                DateTime last;
                string v = Read("last");
                if (!string.IsNullOrEmpty(v) && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last)) return last.ToUniversalTime();
                return DateTime.MinValue;
            }
            set { Write(Auto, value); }
        }

        private const string Script = @"# Monitor Dimmer – Update (wird vom Programm erzeugt)
param([int]$ProcessId,[string]$Zip,[string]$Exe,[string]$Version)
$ErrorActionPreference = 'Stop'
$log = Join-Path $PSScriptRoot 'Update.log'
$name = Split-Path $Exe -Leaf
$target = Split-Path $Exe -Parent
try {
  try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch {}
  Start-Sleep -Milliseconds 500
  $dir = Join-Path $PSScriptRoot 'Neu'
  if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  Expand-Archive -Path $Zip -DestinationPath $dir -Force
  $new = Get-ChildItem $dir -Recurse -File | Where-Object { $_.Name -eq $name } | Select-Object -First 1
  if (-not $new) { throw ('Im ZIP fehlt ' + $name) }
  $old = $Exe + '.alt'
  Copy-Item $Exe $old -Force
  try {
    Copy-Item $new.FullName $Exe -Force
    Get-ChildItem $new.DirectoryName -File | Where-Object { $_.Name -ne $name } | Copy-Item -Destination $target -Force
  } catch {
    Copy-Item $old $Exe -Force
    throw
  }
  Remove-Item $old -Force
  Remove-Item $dir -Recurse -Force
  Remove-Item $Zip -Force
  ('OK ' + $Version) | Out-File $log -Encoding utf8
} catch {
  ('FEHLER ' + $_.Exception.Message) | Out-File $log -Encoding utf8
}
Start-Process -FilePath $Exe -ArgumentList '--nach-update'
";
    }

    internal enum UpdateKind { Unknown, Current, Available, Failed, Checking }

    /// <summary>Stand der Update-Prüfung für das Info-Fenster.</summary>
    internal sealed class UpdateStatus
    {
        public readonly UpdateKind Kind;
        public readonly string Title, Detail;
        public UpdateStatus(UpdateKind kind, string title, string detail) { Kind = kind; Title = title; Detail = detail ?? ""; }
    }

    /// <summary>Verbindung des Info-Fensters zur Update-Prüfung im Tray-Programm (wie Wake on LAN).</summary>
    internal sealed class UpdateHooks
    {
        public Func<UpdateStatus> Status;
        public Func<bool> Auto;
        public Action<bool> SetAuto;
        public Func<Task<UpdateStatus>> CheckNow;
        public Func<bool> CanInstall;
        public Action Install;
        public Action OpenPage;
    }

    /// <summary>Symbol des Update-Stands (Segoe Fluent Icons 11 pt, nach Pixeln zentriert).</summary>
    internal sealed class StatusGlyph : Control
    {
        private readonly float k;
        private char _glyph;
        private Color _color;

        public StatusGlyph(float scale)
        {
            k = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        }

        public void Set(char glyph, Color color)
        {
            _glyph = glyph;
            _color = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Ui.Card);
            if (_glyph == '\0') return;
            int gs = (int)Math.Round(11f * 96f / 72f * k);
            var img = MenuHelper.Glyph(_glyph, _color, gs);
            if (img == null) return;
            e.Graphics.DrawImage(img, (Width - gs) / 2, (Height - gs) / 2, gs, gs);
            img.Dispose();
        }
    }

    /// <summary>Hinweisleiste "Neue Version verfügbar" oben im Fenster (Referenz Wake on LAN).</summary>
    internal sealed class UpdateBar : Control
    {
        private readonly float k;
        public readonly ModernButton Install, Page, Later;

        public UpdateBar(float scale)
        {
            k = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Back;
            Font = new Font("Segoe UI Semibold", 9.5f);
            Install = new ModernButton("Jetzt aktualisieren", MenuHelper.Download, true, k) { Margin = Padding.Empty };
            Page = new ModernButton("Release-Seite", MenuHelper.Link, false, k) { Margin = Padding.Empty };
            Later = new ModernButton("Später", '\0', false, k) { Margin = Padding.Empty };
            foreach (var b in new[] { Install, Page, Later })
            {
                b.HeightUnits = 32;
                b.UpdateSize();
                Controls.Add(b);
            }
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Install == null || Page == null || Later == null) return; // schon beim Setzen der Schrift im Konstruktor
            int x = Width - (int)(12 * k);
            foreach (var b in new[] { Later, Page, Install })
            {
                x -= b.Width;
                b.Location = new Point(x, (Height - b.Height) / 2);
                x -= (int)(8 * k);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color back = Parent != null ? Parent.BackColor : Ui.Back;
            g.Clear(back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = MonitorForm.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8 * k))
            using (var b = new SolidBrush(Accent.Mix(Ui.Primary, back, 0.14)))
            using (var pen = new Pen(Accent.Mix(Ui.Primary, back, 0.45)))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            int x = (int)(16 * k), gs = (int)Math.Round(11f * 96f / 72f * k);
            var img = MenuHelper.Glyph(MenuHelper.Download, Ui.AccentText, gs);
            if (img != null)
            {
                g.DrawImage(img, x, (Height - gs) / 2, gs, gs);
                img.Dispose();
            }
            x += (int)(34 * k);
            int right = (Install != null ? Install.Left : Width) - (int)(12 * k);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Math.Max(10, right - x), Height), Ui.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
