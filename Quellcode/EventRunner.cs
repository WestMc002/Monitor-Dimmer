// Monitor Dimmer – Aktionen beim Wechsel des Profils nacheinander ausführen
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace MonitorDimmer
{
    /// <summary>
    /// Führt die Ereignis-Skripte nacheinander aus, damit sich "Applied" und "Unapplied"
    /// nicht überschneiden (z. B. TeamViewer beenden und gleichzeitig wieder starten).
    /// </summary>
    internal sealed class EventRunner : IDisposable
    {
        private const int MaxWaitMs = 120000;

        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private readonly string _resourcesFolder;

        public EventRunner(string resourcesFolder)
        {
            _resourcesFolder = resourcesFolder;
            _thread = new Thread(() =>
            {
                foreach (var a in _queue.GetConsumingEnumerable())
                {
                    try { a(); }
                    catch (Exception ex) { Log.Error("Ereignis-Skript: {0}", ex.Message); }
                }
            }) { IsBackground = true, Name = "Ereignisse" };
            _thread.Start();
        }

        public void Run(DimmingProfile profile, string type)
        {
            // Aktionen (GUI) zuerst, danach evtl. noch vorhandene Skripte
            var actions = string.Equals(type, "OnAfterDimmingProfileApplied", StringComparison.OrdinalIgnoreCase) ? profile.OnApplied
                : string.Equals(type, "OnAfterDimmingProfileUnapplied", StringComparison.OrdinalIgnoreCase) ? profile.OnUnapplied : null;
            if (actions != null)
            {
                string pn = profile.Name;
                foreach (var a in actions.ToList())
                {
                    var act = a;
                    if (!_queue.IsAddingCompleted)
                        _queue.Add(() => Log.Info("Aktion ({0}): {1} -> {2}", pn, ActionTypes.Describe(act), ActionExecutor.Execute(act, pn)));
                }
            }

            foreach (var h in profile.Handlers)
            {
                if (!string.Equals(h.Type, type, StringComparison.OrdinalIgnoreCase)) continue;
                var handler = h;
                string profileName = profile.Name;
                if (!_queue.IsAddingCompleted) _queue.Add(() => Execute(profileName, handler));
            }
        }

        private void Execute(string profileName, EventHandlerDef h)
        {
            string exe = Environment.ExpandEnvironmentVariables(h.Executable.Replace("{ResourcesFolderPath}", _resourcesFolder));
            string args = Environment.ExpandEnvironmentVariables(h.Arguments.Replace("{ResourcesFolderPath}", _resourcesFolder));
            Log.Info("Skript ({0}, {1}): \"{2}\" {3}", profileName, h.Type, exe, args);

            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = h.Invisible,
                WindowStyle = h.Invisible ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                WorkingDirectory = _resourcesFolder
            };
            var sw = Stopwatch.StartNew();
            using (var p = Process.Start(psi))
            {
                if (p == null) return;
                if (p.WaitForExit(MaxWaitMs))
                    Log.Info("Skript beendet nach {0:0.0} s (Exitcode {1})", sw.Elapsed.TotalSeconds, p.ExitCode);
                else
                    Log.Warn("Skript läuft nach {0} s noch - es wird nicht weiter darauf gewartet", MaxWaitMs / 1000);
            }
        }

        /// <summary>Wartet, bis alle anstehenden Skripte gestartet/beendet sind (höchstens timeoutMs).</summary>
        public void Flush(int timeoutMs)
        {
            using (var done = new ManualResetEvent(false))
            {
                if (_queue.IsAddingCompleted) return;
                _queue.Add(() => { try { done.Set(); } catch (ObjectDisposedException) { } });
                done.WaitOne(timeoutMs);
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
        }
    }
}
