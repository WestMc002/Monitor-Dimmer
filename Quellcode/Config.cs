// Monitor Dimmer – Konfiguration laden, speichern und sichern
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MonitorDimmer
{
    internal sealed class MonitorValues
    {
        public int? Brightness;
        public int? Contrast;
    }

    internal sealed class EventHandlerDef
    {
        public string Type;
        public bool Invisible;
        public string Executable;
        public string Arguments;
    }

    internal sealed class ProcessPattern
    {
        public string Raw;
        private readonly Regex _regex;
        private readonly bool _fileNameOnly;
        public bool HasWildcards;
        public string Expanded;

        public ProcessPattern(string raw)
        {
            Raw = raw;
            Expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
            HasWildcards = Expanded.IndexOfAny(new[] { '*', '?' }) >= 0;
            _fileNameOnly = Expanded.IndexOf('\\') < 0 && Expanded.IndexOf('/') < 0;
            string rx = "^" + Regex.Escape(Expanded.Replace('/', '\\')).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            _regex = new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public bool Matches(string fullPath, string fileName)
        {
            if (_fileNameOnly)
                return fileName != null && _regex.IsMatch(fileName);
            return fullPath != null && _regex.IsMatch(fullPath);
        }

        /// <summary>Trifft das Muster nur über den Dateinamen zu (Fallback, wenn der volle Pfad nicht lesbar ist).</summary>
        public bool MatchesFileNameOnly(string fileName)
        {
            if (fileName == null) return false;
            if (_fileNameOnly) return _regex.IsMatch(fileName);
            string own = Path.GetFileName(Expanded);
            string rx = "^" + Regex.Escape(own).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(fileName, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    internal sealed class DimmingProfile
    {
        public string Name;
        public string DisplayName;
        public bool IsUndimmed;
        public bool AutoApply;
        public bool ForegroundTracker;
        public bool MouseTracker;
        public Dictionary<int, MonitorValues> Monitors = new Dictionary<int, MonitorValues>();
        public List<ProcessPattern> Patterns = new List<ProcessPattern>();
        public List<EventHandlerDef> Handlers = new List<EventHandlerDef>();
        /// <summary>Aktionen beim Aktivieren bzw. Deaktivieren (Abschnitt &lt;Actions&gt;).</summary>
        public List<ActionDef> OnApplied = new List<ActionDef>();
        public List<ActionDef> OnUnapplied = new List<ActionDef>();

        public bool IsAutomatic { get { return AutoApply && ForegroundTracker && !IsUndimmed; } }

        public string Title { get { return string.IsNullOrEmpty(DisplayName) ? Name : DisplayName; } }

        public ProcessPattern FindMatch(string fullPath, string fileName, bool fileNameOnly)
        {
            foreach (var p in Patterns)
            {
                if (fileNameOnly ? p.MatchesFileNameOnly(fileName) : p.Matches(fullPath, fileName))
                    return p;
            }
            return null;
        }
    }

    internal sealed class HotkeyDef
    {
        public string Keys;
        public string Action;
        public string Profile;
    }

    internal sealed class AppConfig
    {
        public string FilePath;
        public long TraceLogFileMaxSize;
        public string TrayIconFile;
        public string ProductName = "Monitor Dimmer";
        public int UndimDelaySeconds = 3;
        public int ReapplyDelaySeconds = 3;
        /// <summary>Skripte beim Entdimmen erst ausführen, wenn das Spiel beendet ist (nicht schon beim Wegklicken).</summary>
        public bool UndimScriptsOnGameExit = true;
        /// <summary>Knöpfe und Hervorhebungen in der Windows-Akzentfarbe statt im festen Blau.</summary>
        public bool UseWindowsAccentColor = true;
        public List<DimmingProfile> Profiles = new List<DimmingProfile>();
        public List<HotkeyDef> Hotkeys = new List<HotkeyDef>();
        public List<string> GameSearchFolders = new List<string>();
        public List<string> IgnoredGameFolders = new List<string>();

        public DimmingProfile Undimmed
        {
            get { return Profiles.First(p => p.IsUndimmed); }
        }

        public DimmingProfile Find(string name)
        {
            return Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Legt beim ersten Start die Konfiguration aus der eingebetteten Startkonfiguration (Resources\Default.xml) an.
        /// Eine vorhandene Datei wird nie überschrieben.
        /// </summary>
        public static void EnsureExists(string path)
        {
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var rs = typeof(AppConfig).Assembly.GetManifestResourceStream("Default.xml"))
            {
                if (rs == null) throw new FileNotFoundException("Konfiguration nicht gefunden und keine Startkonfiguration eingebettet.", path);
                using (var fs = File.Create(path)) rs.CopyTo(fs);
            }
        }

        public static AppConfig Load(string path)
        {
            var doc = XDocument.Load(path, LoadOptions.None);
            var root = doc.Root;
            var cfg = new AppConfig();
            cfg.FilePath = path;

            cfg.TraceLogFileMaxSize = ParseLong(Child(root, "TraceLogFileMaxSize"), 0);
            cfg.TrayIconFile = Text(Child(root, "CustomTrayIconFilePath"));
            string product = Text(Child(root, "CustomProductName"));
            if (!string.IsNullOrWhiteSpace(product)) cfg.ProductName = product.Trim();
            cfg.UndimDelaySeconds = (int)ParseLong(Child(root, "UndimDelaySeconds"), 3);
            cfg.ReapplyDelaySeconds = (int)ParseLong(Child(root, "ReapplyDelaySeconds"), 3);
            cfg.UndimScriptsOnGameExit = ParseBool(Text(Child(root, "UndimScriptsOnGameExit")), true);
            cfg.UseWindowsAccentColor = ParseBool(Text(Child(root, "UseWindowsAccentColor")), true);

            var profilesEl = Child(root, "DimmingProfiles");
            if (profilesEl != null)
            {
                foreach (var pe in Children(profilesEl, "DimmingProfile"))
                {
                    var p = new DimmingProfile();
                    p.Name = Attr(pe, "Name") ?? ("Profil" + (cfg.Profiles.Count + 1));
                    p.DisplayName = Text(Child(pe, "DisplayName"));
                    p.IsUndimmed = ParseBool(Attr(pe, "IsDefiningUndimmedState"), false);
                    p.AutoApply = ParseBool(Attr(pe, "AutoApplicationEnabled"), false);
                    p.ForegroundTracker = ParseBool(Attr(pe, "ForegroundWindowTrackerEnabled"), true);
                    p.MouseTracker = ParseBool(Attr(pe, "MousePointerTrackerEnabled"), false);

                    var pmps = Child(pe, "PhysicalMonitorProfiles");
                    if (pmps != null)
                    {
                        foreach (var m in Children(pmps, "PhysicalMonitorProfile"))
                        {
                            int handle = (int)ParseLong(Attr(m, "Handle"), -1);
                            if (handle < 0) continue;
                            var v = new MonitorValues();
                            var b = Child(m, "BrightnessValue");
                            var c = Child(m, "ContrastValue");
                            if (b != null && !string.IsNullOrWhiteSpace(b.Value)) v.Brightness = (int)ParseLong(b, 0);
                            if (c != null && !string.IsNullOrWhiteSpace(c.Value)) v.Contrast = (int)ParseLong(c, 0);
                            p.Monitors[handle] = v;
                        }
                    }

                    var paths = Child(pe, "ProcessExecutableFilePaths");
                    if (paths != null)
                    {
                        foreach (var x in Children(paths, "ProcessExecutableFilePath"))
                        {
                            if (string.IsNullOrWhiteSpace(x.Value)) continue;
                            p.Patterns.Add(new ProcessPattern(x.Value));
                        }
                    }

                    var handlers = Child(pe, "EventHandlers");
                    if (handlers != null)
                    {
                        foreach (var h in Children(handlers, "EventHandler"))
                        {
                            var eh = new EventHandlerDef();
                            eh.Type = Attr(h, "Type") ?? "";
                            eh.Invisible = ParseBool(Attr(h, "InvisibleExecutionEnabled"), false);
                            eh.Executable = Text(Child(h, "ExecutableFilePath"));
                            eh.Arguments = Text(Child(h, "CommandLineParameters")) ?? "";
                            if (!string.IsNullOrWhiteSpace(eh.Executable)) p.Handlers.Add(eh);
                        }
                    }
                    var actions = Child(pe, "Actions");
                    if (actions != null)
                    {
                        ReadActions(Child(actions, "OnApplied"), p.OnApplied);
                        ReadActions(Child(actions, "OnUnapplied"), p.OnUnapplied);
                    }
                    cfg.Profiles.Add(p);
                }
            }

            if (!cfg.Profiles.Any(p => p.IsUndimmed))
            {
                var u = new DimmingProfile { Name = "Undimmed", DisplayName = "Ungedimmt", IsUndimmed = true };
                cfg.Profiles.Insert(0, u);
                Log.Warn("Kein Profil mit IsDefiningUndimmedState gefunden - leeres Ungedimmt-Profil angelegt");
            }

            var sf = Child(root, "GameSearchFolders");
            if (sf != null)
            {
                foreach (var f in Children(sf, "Folder"))
                    if (!string.IsNullOrWhiteSpace(f.Value)) cfg.GameSearchFolders.Add(f.Value.Trim());
            }

            var ig = Child(root, "IgnoredGameFolders");
            if (ig != null)
            {
                foreach (var f in Children(ig, "Folder"))
                    if (!string.IsNullOrWhiteSpace(f.Value)) cfg.IgnoredGameFolders.Add(f.Value.Trim());
            }

            var hk = Child(root, "Hotkeys");
            if (hk != null)
            {
                foreach (var h in Children(hk, "Hotkey"))
                {
                    cfg.Hotkeys.Add(new HotkeyDef { Keys = Attr(h, "Keys"), Action = Attr(h, "Action"), Profile = Attr(h, "Profile") });
                }
            }

            foreach (var p in cfg.Profiles)
            {
                if (p.MouseTracker)
                    Log.Warn("Profil \"{0}\": MousePointerTrackerEnabled wird nicht unterstützt und ignoriert", p.Name);
            }
            return cfg;
        }

        /// <summary>Liest die Programmpfade eines Profils direkt aus der Datei.</summary>
        public static List<string> ReadProcessPaths(string configPath, string profileName)
        {
            var doc = XDocument.Load(configPath, LoadOptions.None);
            var pe = FindProfileElement(doc, profileName);
            var list = Child(pe, "ProcessExecutableFilePaths");
            if (list == null) return new List<string>();
            return Children(list, "ProcessExecutableFilePath").Select(x => x.Value.Trim()).Where(x => x.Length > 0).ToList();
        }

        /// <summary>Fügt einen Programmpfad hinzu (sortiert, ohne Doppelte).</summary>
        public static void AddProcessPath(string configPath, string profileName, string exePath)
        {
            var paths = ReadProcessPaths(configPath, profileName);
            paths.Add(exePath);
            SetProcessPaths(configPath, profileName, paths);
        }

        /// <summary>
        /// Schreibt die Programmliste eines Profils neu: alphabetisch sortiert, ohne Doppelte, einheitlich eingerückt.
        /// Auskommentierte Einträge bleiben am Ende der Liste erhalten. Vorher wird eine Sicherung angelegt (Configurations\Sicherungen).
        /// </summary>
        public static void SetProcessPaths(string configPath, string profileName, IEnumerable<string> paths)
        {
            var doc = XDocument.Load(configPath, LoadOptions.PreserveWhitespace);
            var pe = FindProfileElement(doc, profileName);
            XNamespace ns = pe.Name.Namespace;
            string pIndent = GetIndent(pe);
            string lIndent = pIndent + "  ";
            string iIndent = lIndent + "  ";

            var clean = paths.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            var list = Child(pe, "ProcessExecutableFilePaths");
            var comments = list == null ? new List<XComment>() : list.Nodes().OfType<XComment>().ToList();

            var newList = new XElement(ns + "ProcessExecutableFilePaths");
            foreach (var p in clean) newList.Add(new XText("\n" + iIndent), new XElement(ns + "ProcessExecutableFilePath", p));
            foreach (var c in comments) newList.Add(new XText("\n" + iIndent), new XComment(c.Value));
            newList.Add(new XText("\n" + lIndent));

            if (list != null)
            {
                list.ReplaceWith(newList);
            }
            else
            {
                var before = Child(pe, "EventHandlers");
                if (before != null)
                {
                    before.AddBeforeSelf(newList, new XText("\n" + lIndent));
                }
                else
                {
                    var last = pe.Nodes().LastOrDefault() as XText;
                    if (last != null) last.AddBeforeSelf(new XText("\n" + lIndent), newList);
                    else pe.Add(new XText("\n" + lIndent), newList, new XText("\n" + pIndent));
                }
            }
            Save(doc, configPath);
        }

        /// <summary>Schreibt den Abschnitt &lt;Hotkeys&gt; neu (vorher Sicherung in Configurations\Sicherungen).</summary>
        public static void SetHotkeys(string configPath, IEnumerable<HotkeyDef> hotkeys)
        {
            var doc = XDocument.Load(configPath, LoadOptions.PreserveWhitespace);
            var root = doc.Root;
            XNamespace ns = root.Name.Namespace;
            var newList = new XElement(ns + "Hotkeys");
            foreach (var h in hotkeys)
            {
                if (string.IsNullOrWhiteSpace(h.Keys) || string.IsNullOrWhiteSpace(h.Action)) continue;
                var el = new XElement(ns + "Hotkey", new XAttribute("Keys", h.Keys.Trim()), new XAttribute("Action", h.Action.Trim()));
                if (!string.IsNullOrWhiteSpace(h.Profile)) el.Add(new XAttribute("Profile", h.Profile.Trim()));
                newList.Add(new XText("\n    "), el);
            }
            newList.Add(new XText("\n  "));
            var list = Child(root, "Hotkeys");
            if (list != null)
            {
                list.ReplaceWith(newList);
            }
            else
            {
                var profiles = Child(root, "DimmingProfiles");
                if (profiles != null) profiles.AddBeforeSelf(newList, new XText("\n\n  "));
                else root.Add(new XText("  "), newList, new XText("\n"));
            }
            Save(doc, configPath);
        }

        private static void ReadActions(XElement list, List<ActionDef> target)
        {
            if (list == null) return;
            foreach (var a in Children(list, "Action"))
            {
                string type = Attr(a, "Type");
                if (string.IsNullOrWhiteSpace(type)) continue;
                target.Add(new ActionDef { Type = type.Trim(), Value = Attr(a, "Value") ?? "", Arguments = Attr(a, "Arguments") ?? "" });
            }
        }

        /// <summary>
        /// Schreibt die Aktionen je Profil (&lt;Actions&gt; im &lt;DimmingProfile&gt;).
        /// removeHandlers: bei diesen Profilen werden die alten &lt;EventHandlers&gt; (Skripte) entfernt.
        /// </summary>
        public static void SetActions(string configPath, IEnumerable<DimmingProfile> profiles, ICollection<string> removeHandlers)
        {
            var doc = XDocument.Load(configPath, LoadOptions.PreserveWhitespace);
            var root = doc.Root;
            XNamespace ns = root.Name.Namespace;
            var profilesEl = Child(root, "DimmingProfiles");
            if (profilesEl == null) throw new InvalidOperationException("Abschnitt DimmingProfiles fehlt.");
            foreach (var p in profiles)
            {
                var pe = Children(profilesEl, "DimmingProfile")
                    .FirstOrDefault(x => string.Equals(Attr(x, "Name"), p.Name, StringComparison.OrdinalIgnoreCase));
                if (pe == null) continue;
                RemoveWithIndent(Child(pe, "Actions"));
                if (removeHandlers != null && removeHandlers.Contains(p.Name)) RemoveWithIndent(Child(pe, "EventHandlers"));
                if (p.OnApplied.Count == 0 && p.OnUnapplied.Count == 0) continue;

                var el = new XElement(ns + "Actions");
                el.Add(new XText("\n        "), ActionList(ns, "OnApplied", p.OnApplied));
                el.Add(new XText("\n        "), ActionList(ns, "OnUnapplied", p.OnUnapplied));
                el.Add(new XText("\n      "));
                var last = pe.Nodes().LastOrDefault();
                var lastText = last as XText;
                if (lastText != null) lastText.AddBeforeSelf(new XText("\n      "), el);
                else pe.Add(new XText("\n      "), el, new XText("\n    "));
            }
            Save(doc, configPath);
        }

        private static XElement ActionList(XNamespace ns, string name, List<ActionDef> list)
        {
            var el = new XElement(ns + name);
            foreach (var a in list)
            {
                var x = new XElement(ns + "Action", new XAttribute("Type", a.Type), new XAttribute("Value", a.Value ?? ""));
                if (!string.IsNullOrEmpty(a.Arguments)) x.Add(new XAttribute("Arguments", a.Arguments));
                el.Add(new XText("\n          "), x);
            }
            el.Add(new XText(list.Count > 0 ? "\n        " : ""));
            return el;
        }

        private static void RemoveWithIndent(XElement el)
        {
            if (el == null) return;
            var prev = el.PreviousNode as XText;
            if (prev != null && string.IsNullOrWhiteSpace(prev.Value)) prev.Remove();
            el.Remove();
        }

        /// <summary>Merkt Spielordner, die bei "Neue suchen" nicht mehr vorgeschlagen werden (vor DimmingProfiles).</summary>
        public static void AddIgnoredFolders(string configPath, IEnumerable<string> folders)
        {
            var doc = XDocument.Load(configPath, LoadOptions.PreserveWhitespace);
            var root = doc.Root;
            XNamespace ns = root.Name.Namespace;
            var list = Child(root, "IgnoredGameFolders");
            var existing = list == null ? new List<string>() : Children(list, "Folder").Select(x => x.Value.Trim()).ToList();
            var all = existing.Concat(folders).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            var newList = new XElement(ns + "IgnoredGameFolders");
            foreach (var f in all) newList.Add(new XText("\n    "), new XElement(ns + "Folder", f));
            newList.Add(new XText("\n  "));
            if (list != null)
            {
                list.ReplaceWith(newList);
            }
            else
            {
                var profiles = Child(root, "DimmingProfiles");
                var comment = new XComment(" Spielordner, die bei \"Neue / fehlende suchen\" nicht vorgeschlagen werden ");
                if (profiles != null) profiles.AddBeforeSelf(comment, new XText("\n  "), newList, new XText("\n\n  "));
                else root.Add(new XText("  "), comment, new XText("\n  "), newList, new XText("\n"));
            }
            Save(doc, configPath);
        }

        private static XElement FindProfileElement(XDocument doc, string profileName)
        {
            var profilesEl = Child(doc.Root, "DimmingProfiles");
            var pe = profilesEl == null ? null : Children(profilesEl, "DimmingProfile")
                .FirstOrDefault(x => string.Equals(Attr(x, "Name"), profileName, StringComparison.OrdinalIgnoreCase));
            if (pe == null) throw new InvalidOperationException("Profil \"" + profileName + "\" nicht gefunden.");
            return pe;
        }

        private static void Save(XDocument doc, string configPath)
        {
            ConfigBackup.Create(configPath);
            // Erst in eine Zwischendatei schreiben, dann austauschen: Default.xml ist nie halb geschrieben
            string temp = configPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(true), OmitXmlDeclaration = false };
                using (var w = XmlWriter.Create(temp, settings))
                {
                    doc.Save(w);
                }
                if (File.Exists(configPath)) File.Replace(temp, configPath, null);
                else File.Move(temp, configPath);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static string GetIndent(XElement el)
        {
            var prev = el.PreviousNode as XText;
            if (prev == null) return "      ";
            string t = prev.Value;
            int nl = t.LastIndexOf('\n');
            return nl >= 0 ? t.Substring(nl + 1) : t;
        }

        // ---------- Hilfsfunktionen (Namensraum-unabhängig) ----------

        private static XElement Child(XElement parent, string localName)
        {
            if (parent == null) return null;
            return parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
        }

        private static IEnumerable<XElement> Children(XElement parent, string localName)
        {
            return parent.Elements().Where(e => e.Name.LocalName == localName);
        }

        private static string Attr(XElement el, string name)
        {
            var a = el.Attribute(name);
            return a == null ? null : a.Value;
        }

        private static string Text(XElement el)
        {
            return el == null ? null : el.Value.Trim();
        }

        private static long ParseLong(XElement el, long def)
        {
            return el == null ? def : ParseLong(el.Value, def);
        }

        private static long ParseLong(string s, long def)
        {
            long v;
            if (s != null && long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return def;
        }

        private static bool ParseBool(string s, bool def)
        {
            if (string.IsNullOrWhiteSpace(s)) return def;
            s = s.Trim();
            if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return def;
        }
    }

    /// <summary>
    /// Versionierte Sicherungen der Konfiguration in Configurations\Sicherungen,
    /// z. B. Default_2026-10-29_153012.xml. Die neuesten <see cref="Keep"/> bleiben erhalten.
    /// </summary>
    internal static class ConfigBackup
    {
        public const int Keep = 50;

        public static string Folder(string configPath)
        {
            return Path.Combine(Path.GetDirectoryName(configPath), "Sicherungen");
        }

        /// <summary>Sichert den aktuellen Stand, sofern er sich von der neuesten Sicherung unterscheidet.</summary>
        public static string Create(string configPath)
        {
            try
            {
                if (!File.Exists(configPath)) return null;
                string dir = Folder(configPath);
                Directory.CreateDirectory(dir);
                MigrateOldBak(configPath, dir);

                byte[] current = File.ReadAllBytes(configPath);
                string newest = List(configPath).FirstOrDefault();
                if (newest != null && SameContent(newest, current)) return newest;

                string name = Path.GetFileNameWithoutExtension(configPath);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
                string dest = Path.Combine(dir, name + "_" + stamp + ".xml");
                for (int i = 2; File.Exists(dest); i++)
                    dest = Path.Combine(dir, name + "_" + stamp + "-" + i + ".xml");
                File.WriteAllBytes(dest, current);
                Log.Info("Sicherung angelegt: {0}", Path.GetFileName(dest));
                Prune(configPath);
                return dest;
            }
            catch (Exception ex)
            {
                Log.Error("Sicherung der Konfiguration fehlgeschlagen: {0}", ex.Message);
                return null;
            }
        }

        /// <summary>Alle Sicherungen dieser Konfiguration, neueste zuerst.</summary>
        public static List<string> List(string configPath)
        {
            string dir = Folder(configPath);
            if (!Directory.Exists(dir)) return new List<string>();
            string name = Path.GetFileNameWithoutExtension(configPath);
            return Directory.GetFiles(dir, name + "_*.xml")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void Prune(string configPath)
        {
            foreach (var old in List(configPath).Skip(Keep))
            {
                try { File.Delete(old); } catch { }
            }
        }

        private static bool SameContent(string file, byte[] data)
        {
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length != data.Length) return false;
                return File.ReadAllBytes(file).SequenceEqual(data);
            }
            catch { return false; }
        }

        /// <summary>Die frühere Einzelsicherung Default.xml.bak wird einmalig in den Sicherungsordner übernommen.</summary>
        private static void MigrateOldBak(string configPath, string dir)
        {
            string bak = configPath + ".bak";
            if (!File.Exists(bak)) return;
            try
            {
                string name = Path.GetFileNameWithoutExtension(configPath);
                string stamp = File.GetLastWriteTime(bak).ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
                string dest = Path.Combine(dir, name + "_" + stamp + ".xml");
                if (File.Exists(dest)) File.Delete(bak);
                else File.Move(bak, dest);
            }
            catch { }
        }
    }
}
