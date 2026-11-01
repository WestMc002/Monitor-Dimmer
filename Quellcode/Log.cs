// Monitor Dimmer – Protokoll (TraceLogs)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Text;
using System.Threading;

namespace MonitorDimmer
{
    /// <summary>Einfaches Protokoll mit Größenbegrenzung (rotiert in *.old.log).</summary>
    internal static class Log
    {
        private static readonly object Sync = new object();
        private static string _path;
        private static long _maxSize = 5L * 1024 * 1024;

        public static string FilePath { get { return _path; } }

        public static void Init(string path)
        {
            _path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
        }

        /// <summary>0 oder kleiner = Standard (5 MB).</summary>
        public static void SetMaxSize(long bytes)
        {
            _maxSize = bytes > 0 ? bytes : 5L * 1024 * 1024;
        }

        public static void Info(string format, params object[] args)
        {
            Write("INFO ", format, args);
        }

        public static void Warn(string format, params object[] args)
        {
            Write("WARN ", format, args);
        }

        public static void Error(string format, params object[] args)
        {
            Write("FEHLER", format, args);
        }

        private static void Write(string level, string format, object[] args)
        {
            if (_path == null) return;
            string msg = args != null && args.Length > 0 ? string.Format(format, args) : format;
            string line = string.Format("({0:yyyy-MM-ddTHH:mm:ss})\t({1:D2})\t{2}\t{3}\r\n",
                DateTime.Now, Thread.CurrentThread.ManagedThreadId, level, msg);
            lock (Sync)
            {
                try
                {
                    var fi = new FileInfo(_path);
                    if (fi.Exists && fi.Length > _maxSize)
                    {
                        string old = Path.Combine(fi.DirectoryName, Path.GetFileNameWithoutExtension(_path) + ".old.log");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(_path, old);
                    }
                    File.AppendAllText(_path, line, Encoding.UTF8);
                }
                catch
                {
                    // Protokollfehler dürfen das Programm nicht stören.
                }
            }
        }
    }
}
