// Monitor Dimmer – Screenshots mit Beispieldaten (hell und dunkel) für GitHub
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MonitorDimmer
{
    /// <summary>
    /// Erzeugt die Screenshots für die README aus Beispieldaten (Resources\Demo.xml, in die EXE eingebettet).
    /// Aufruf: "Monitor Dimmer.exe" --screenshots [Zielordner]. Die echte Konfiguration, die Monitore,
    /// Tastenkürzel und das Protokoll werden dabei nicht angefasst.
    /// Ablage: hell.png / dunkel.png (Tray-Menü) und &lt;name&gt;-hell.png / &lt;name&gt;-dunkel.png.
    /// Jedes Bild wird einmal vor schwarzem und einmal vor weißem Hintergrund aufgenommen; daraus ergeben sich
    /// durchsichtige runde Ecken und der Windows-Schatten.
    /// </summary>
    internal static class Demo
    {
        /// <summary>true, solange die Screenshots erstellt werden (Beispieldaten gelten als vorhanden).</summary>
        public static bool Active;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        public static bool RunScreenshots(string outDir)
        {
            string temp = Path.Combine(Path.GetTempPath(), "Monitor Dimmer Screenshots");
            try
            {
                Active = true;
                Directory.CreateDirectory(outDir);
                try { File.Delete(Path.Combine(outDir, "Fehler.txt")); } catch { }
                Directory.CreateDirectory(temp);
                string configPath = Path.Combine(temp, "Demo.xml");
                using (var rs = typeof(Demo).Assembly.GetManifestResourceStream("Demo.xml"))
                {
                    if (rs == null) throw new FileNotFoundException("Demo.xml ist nicht in die EXE eingebettet.");
                    using (var fs = File.Create(configPath)) rs.CopyTo(fs);
                }
                var cfg = AppConfig.Load(configPath);
                Accent.Enabled = cfg.UseWindowsAccentColor;
                var dimmed = cfg.Profiles.FirstOrDefault(p => !p.IsUndimmed && p.AutoApply) ?? cfg.Undimmed;
                // Beispiel für die Seite "Zeitplan": abends dimmen
                cfg.Schedule = new ScheduleDef { Enabled = true, Profile = dimmed.Name, From = new TimeSpan(20, 0, 0), To = new TimeSpan(7, 0, 0) };
                Icon icon = null;
                try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

                // Maus aus dem Bild (rechts unten auf dem Hauptbildschirm); Fenster öffnen dort, wo die Maus ist
                var area = Screen.PrimaryScreen.WorkingArea;
                Cursor.Position = new Point(area.Right - 2, area.Bottom - 2);

                using (var backdrop = new Backdrop())
                using (var manager = new MonitorManager())
                {
                    // Hintergrundfläche einmal anlegen und sichtbar lassen, zwischen den Aufnahmen nur verschieben:
                    // frisch eingeblendet schloss sie das Tray-Menü bzw. wäre bei der ersten Aufnahme noch nicht fertig
                    backdrop.Cover(new Rectangle(area.Right - 2, area.Bottom - 2, 1, 1), Color.Black, IntPtr.Zero);
                    Pump(300);
                    backdrop.Park();

                    foreach (bool dark in new[] { false, true })
                    {
                        Theme.Override = dark;
                        Theme.Refresh();
                        string suffix = dark ? "dunkel" : "hell";

                        // Tray-Menü (Titelbild)
                        var candidate = new ForegroundInfo { FileName = "MoonBase.exe", FullPath = @"C:\Spiele\Moon Base\MoonBase.exe" };
                        using (var menu = TrayApp.CreateDemoMenu(cfg, dimmed, "AsteroidMiner.exe", candidate))
                        {
                            var size = menu.GetPreferredSize(Size.Empty);
                            var pos = new Point(area.Left + (area.Width - size.Width) / 2, area.Top + Math.Max(0, (area.Height - size.Height) / 2));
                            menu.Show(pos);
                            Pump(800);
                            if (!menu.Visible) { menu.Show(pos); Pump(800); } // falls Windows es wieder geschlossen hat
                            Save(Capture(backdrop, menu.Handle, menu.Bounds, Margin(menu)), Path.Combine(outDir, suffix + ".png"));
                            menu.Close();
                            Pump(200);
                        }

                        // Hauptfenster mit Navigation, je Seite ein Bild
                        var games = new PageDef { Text = "Spieleliste", Glyph = MenuHelper.Game, Create = () => new GameListForm(() => cfg, (p, l) => { }, l => { }, icon) };
                        var actions = new PageDef { Text = "Aktionen", Glyph = MenuHelper.Actions, Create = () => new ActionsForm(cfg, temp, dimmed.Name, icon) };
                        var hotkeys = new PageDef { Text = "Tastenkürzel", Glyph = MenuHelper.Keyboard, Create = () => new HotkeyForm(cfg, k => "", icon) };
                        var monitors = new PageDef { Text = "Monitore", Glyph = MenuHelper.Monitor, Create = () => new MonitorForm(manager, () => cfg, () => dimmed, icon) };
                        var schedule = new PageDef { Text = "Zeitplan", Glyph = MenuHelper.Clock, Create = () => new ScheduleForm(cfg, icon) };
                        using (var main = new MainWindow(new List<PageDef> { monitors, games, actions, schedule, hotkeys }, icon, c => { }, () => { }))
                        {
                            main.ShowInTaskbar = false;
                            main.Show();
                            main.TopMost = true;
                            Shot(backdrop, main, games, Path.Combine(outDir, "spieleliste-" + suffix + ".png"));
                            Shot(backdrop, main, actions, Path.Combine(outDir, "aktionen-" + suffix + ".png"));
                            Shot(backdrop, main, schedule, Path.Combine(outDir, "zeitplan-" + suffix + ".png"));
                            Shot(backdrop, main, hotkeys, Path.Combine(outDir, "tastenkuerzel-" + suffix + ".png"));
                            Shot(backdrop, main, monitors, Path.Combine(outDir, "monitore-" + suffix + ".png"));
                            // Info als kleines Pop-up, solange das Hauptfenster noch offen ist: sonst holt Windows das
                            // zuletzt aktive Fenster (z. B. die Konsole der Erstellen.bat) wieder nach vorn, vor das Info-Fenster
                            Shot(backdrop, () => new AboutDialog(null), Path.Combine(outDir, "info-" + suffix + ".png"));
                            main.Close();
                        }
                        Pump(300);
                    }
                }
                if (Errors.Count > 0)
                {
                    File.WriteAllLines(Path.Combine(outDir, "Fehler.txt"), Errors);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(outDir, "Fehler.txt"), ex.ToString()); } catch { }
                return false;
            }
            finally
            {
                Theme.Override = null;
                Active = false;
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        /// <summary>Beispielmonitore für das Fenster "Monitore": links und rechts gedimmt, Mitte hell.</summary>
        public static List<MonitorSnapshot> Monitors()
        {
            return new List<MonitorSnapshot>
            {
                new MonitorSnapshot { Index = 2, Description = "Generic PnP Monitor", DeviceName = @"\\.\DISPLAY3", Bounds = new Rectangle(-2560, 0, 2560, 1440), Brightness = 0, Contrast = 0 },
                new MonitorSnapshot { Index = 1, Description = "Generic PnP Monitor", DeviceName = @"\\.\DISPLAY1", Bounds = new Rectangle(0, 0, 2560, 1440), Brightness = 75, Contrast = 65 },
                new MonitorSnapshot { Index = 0, Description = "Generic PnP Monitor", DeviceName = @"\\.\DISPLAY2", Bounds = new Rectangle(2560, 0, 2560, 1440), Brightness = 0, Contrast = 0 }
            };
        }

        /// <summary>Fehler einzelner Bilder: die übrigen Bilder entstehen trotzdem, alles steht danach in Fehler.txt.</summary>
        private static readonly List<string> Errors = new List<string>();

        private static void Shot(Backdrop backdrop, Func<Form> create, string file)
        {
            try { ShotCore(backdrop, create, file); }
            catch (Exception ex) { Errors.Add(Path.GetFileName(file) + ": " + ex.Message); }
        }

        private static void ShotCore(Backdrop backdrop, Func<Form> create, string file)
        {
            using (var f = create())
            {
                f.ShowInTaskbar = false;
                f.Show();
                f.TopMost = true;
                f.Activate();
                Pump(1500);
                RECT r;
                Rectangle bounds = DwmGetWindowAttribute(f.Handle, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) == 0
                    ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
                    : f.Bounds;
                Save(Capture(backdrop, f.Handle, bounds, Margin(f)), file);
                f.Close();
            }
            Pump(300);
        }

        private static void Shot(Backdrop backdrop, MainWindow main, PageDef page, string file)
        {
            try { ShotCore(backdrop, main, page, file); }
            catch (Exception ex) { Errors.Add(Path.GetFileName(file) + ": " + ex.Message); }
        }

        private static void ShotCore(Backdrop backdrop, MainWindow main, PageDef page, string file)
        {
            main.ShowPage(page);
            main.Activate();
            Pump(1500); // Aufbau, Symbole und Listen laden
            RECT r;
            Rectangle bounds = DwmGetWindowAttribute(main.Handle, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) == 0
                ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
                : main.Bounds;
            Save(Capture(backdrop, main.Handle, bounds, Margin(main)), file);
        }

        /// <summary>Rand um das Fenster für den Schatten (24 Einheiten).</summary>
        private static int Margin(Control c)
        {
            try { using (var g = c.CreateGraphics()) return (int)Math.Round(24 * g.DpiX / 96f); }
            catch { return 24; }
        }

        /// <summary>Nimmt den Bereich vor schwarzem und vor weißem Hintergrund auf und berechnet daraus die Durchsichtigkeit.</summary>
        private static Bitmap Capture(Backdrop backdrop, IntPtr target, Rectangle bounds, int margin)
        {
            var r = Rectangle.Inflate(bounds, margin, margin);
            // Liegt das Fenster nicht vor der Hintergrundfläche, wäre das Bild leer: dann Fenster erneut nach vorn holen
            // und bis zu dreimal wiederholen; danach mit Angaben in Fehler.txt abbrechen
            for (int attempt = 1; ; attempt++)
            {
                Bitmap result;
                backdrop.Cover(r, Color.Black, target);
                Pump(300);
                using (var onBlack = Grab(r))
                {
                    backdrop.Cover(r, Color.White, target);
                    Pump(300);
                    using (var onWhite = Grab(r))
                    {
                        backdrop.Park();
                        result = Combine(onBlack, onWhite);
                    }
                }
                if (!IsEmpty(result)) return result;
                result.Dispose();
                var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                IntPtr top = GetAncestor(WindowFromPoint(center), 2); // GA_ROOT
                string info = string.Format("Aufnahme leer (Versuch {0}): Fenster {1:X} sichtbar={2}, Bereich {3}, oben liegt {4:X} ({5}), Hintergrund {6:X}",
                    attempt, target.ToInt64(), IsWindowVisible(target), bounds, top.ToInt64(), Describe(top), backdrop.Handle.ToInt64());
                if (attempt >= 3) throw new Exception(info);
                SetWindowPos(target, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // HWND_TOPMOST, NOSIZE | NOMOVE | SHOWWINDOW
                SetForegroundWindow(target);
                Pump(1000);
            }
        }

        /// <summary>true, wenn das Bild ganz durchsichtig ist (Fenster lag hinter der Hintergrundfläche).</summary>
        private static bool IsEmpty(Bitmap bmp)
        {
            for (int y = bmp.Height / 4; y < bmp.Height * 3 / 4; y += 7)
                for (int x = bmp.Width / 4; x < bmp.Width * 3 / 4; x += 7)
                    if (bmp.GetPixel(x, y).A > 0) return false;
            return true;
        }

        /// <summary>Klasse, Titel und Programm eines Fensters (für Fehler.txt).</summary>
        private static string Describe(IntPtr h)
        {
            var cls = new System.Text.StringBuilder(256);
            var title = new System.Text.StringBuilder(256);
            GetClassName(h, cls, cls.Capacity);
            GetWindowText(h, title, title.Capacity);
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            string proc = "";
            try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { }
            var c = Control.FromHandle(h);
            return "Klasse " + cls + ", Titel \"" + title + "\", Programm " + proc + (c != null ? ", " + c.GetType().Name : "");
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private static Bitmap Grab(Rectangle r)
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            return bmp;
        }

        private static Bitmap Combine(Bitmap onBlack, Bitmap onWhite)
        {
            int w = onBlack.Width, h = onBlack.Height;
            var rect = new Rectangle(0, 0, w, h);
            var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = onBlack.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var wd = onWhite.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var od = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int n = bd.Stride * h;
                var b = new byte[n];
                var wh = new byte[n];
                var o = new byte[n];
                Marshal.Copy(bd.Scan0, b, 0, n);
                Marshal.Copy(wd.Scan0, wh, 0, n);
                for (int i = 0; i + 3 < n; i += 4)
                {
                    int diff = ((wh[i] - b[i]) + (wh[i + 1] - b[i + 1]) + (wh[i + 2] - b[i + 2])) / 3;
                    int a = Math.Max(0, Math.Min(255, 255 - diff));
                    if (a == 0) continue; // ganz durchsichtig
                    for (int c = 0; c < 3; c++) o[i + c] = (byte)Math.Min(255, b[i + c] * 255 / a);
                    o[i + 3] = (byte)a;
                }
                Marshal.Copy(o, 0, od.Scan0, n);
            }
            finally
            {
                onBlack.UnlockBits(bd);
                onWhite.UnlockBits(wd);
                result.UnlockBits(od);
            }
            return result;
        }

        private static void Save(Bitmap bmp, string file)
        {
            using (bmp) bmp.Save(file, ImageFormat.Png);
        }

        /// <summary>Wartet und verarbeitet dabei die Fensternachrichten (Zeichnen, Laden).</summary>
        private static void Pump(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        /// <summary>Einfarbige Fläche hinter dem aufgenommenen Bereich.</summary>
        private sealed class Backdrop : Form
        {
            public Backdrop()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW
                    return cp;
                }
            }

            public void Cover(Rectangle r, Color color, IntPtr target)
            {
                BackColor = color;
                Bounds = r;
                if (!Visible) Show();
                // vor alle anderen Fenster legen, danach das aufgenommene Fenster bzw. Menü wieder davor
                SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, NoMoveSizeActivate);
                SetWindowPos(target, HwndTopmost, 0, 0, 0, 0, NoMoveSizeActivate);
                Refresh();
            }

            /// <summary>Aus dem Bild schieben (bleibt sichtbar, wird also nicht neu eingeblendet).</summary>
            public void Park()
            {
                Bounds = new Rectangle(-32000, -32000, 1, 1);
            }

            private static readonly IntPtr HwndTopmost = new IntPtr(-1);
            private const uint NoMoveSizeActivate = 0x0001 | 0x0002 | 0x0010; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE

            [DllImport("user32.dll")]
            private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        }
    }
}
