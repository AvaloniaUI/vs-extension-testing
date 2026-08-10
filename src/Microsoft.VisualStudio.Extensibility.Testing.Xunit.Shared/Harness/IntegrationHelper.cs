// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for more information.

namespace Xunit.Harness
{
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading.Tasks;
    using Windows.Win32;
    using DTE = EnvDTE.DTE;
    using IMoniker = Windows.Win32.System.Com.IMoniker;

    /// <summary>
    /// Provides some helper functions used by the other classes in the project.
    /// </summary>
    internal static class IntegrationHelper
    {
        /// <summary>
        /// Interval between readiness probes in <see cref="WaitForNotNullAsync{T}"/>. Long enough
        /// that polling doesn't compete with the process being waited on for CPU, short enough not
        /// to add meaningful latency to a handshake measured in seconds.
        /// </summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Kills the specified process if it is not <see langword="null"/> and has not already exited.
        /// </summary>
        public static void KillProcess(Process process)
        {
            if (process != null && !process.HasExited)
            {
                process.Kill();
            }
        }

        /// <summary>
        /// Kills all processes matching the specified name.
        /// </summary>
        public static void KillProcess(string processName)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                KillProcess(process);
            }
        }

        /// <summary>Locates the DTE object for the specified process.</summary>
        public static DTE? TryLocateDteForProcess(Process process)
        {
            object? dte = null;
            var monikers = new IMoniker?[1];

            PInvoke.GetRunningObjectTable(0, out var runningObjectTable);
            runningObjectTable.EnumRunning(out var enumMoniker);
            PInvoke.CreateBindCtx(0, out var bindContext);

            do
            {
                monikers[0] = null;

                uint monikersFetched;
                unsafe
                {
                    enumMoniker.Next(1, monikers, &monikersFetched);
                }

                if (monikersFetched == 0)
                {
                    // There's nothing further to enumerate, so fail
                    return null;
                }

                var moniker = monikers[0]!;
                moniker.GetDisplayName(bindContext, null, out var fullDisplayName);

                // FullDisplayName will look something like: <ProgID>:<ProccessId>
                var displayNameParts = fullDisplayName.ToString().Split(':');
                if (!int.TryParse(displayNameParts.Last(), out var displayNameProcessId))
                {
                    continue;
                }

                if (displayNameParts[0].StartsWith("!VisualStudio.DTE", StringComparison.OrdinalIgnoreCase) &&
                    displayNameProcessId == process.Id)
                {
                    runningObjectTable.GetObject(moniker, out dte);
                }
            }
            while (dte == null);

            return (DTE)dte;
        }

        /// <summary>
        /// Polls <paramref name="action"/> until it returns a value, giving up after
        /// <paramref name="timeout"/>.
        /// </summary>
        /// <typeparam name="T">The type of value being waited for.</typeparam>
        /// <param name="action">The probe to run. Expected to be cheap but not free — it is
        /// throttled by <see cref="PollInterval"/> rather than run in a tight loop.</param>
        /// <param name="timeout">Upper bound on the total wait.</param>
        /// <param name="liveness">The process whose readiness is being awaited. Checked on every
        /// iteration so a host that dies (or never starts) fails immediately instead of being
        /// waited on for the full <paramref name="timeout"/>.</param>
        /// <param name="description">Human-readable description of what is being awaited, used in
        /// the failure messages.</param>
        /// <returns>The first non-<see langword="null"/> result.</returns>
        /// <exception cref="TimeoutException">No value was produced within <paramref name="timeout"/>.</exception>
        /// <exception cref="InvalidOperationException"><paramref name="liveness"/> exited while waiting.</exception>
        /// <remarks>
        /// This used to be an unbounded <c>while (result == null) await Task.Yield();</c> loop. That
        /// had two failure modes on CI: a devenv that never registered its DTE (crashed at startup,
        /// blocked on a modal dialog, wedged loading packages) parked the harness forever with no
        /// test event, which vstest's blame collector reports as a 12-minute inactivity hang; and
        /// the yield-only loop saturated a core enumerating the running object table over COM,
        /// starving the very process it was waiting for on a small CI runner.
        /// </remarks>
        public static async Task<T> WaitForNotNullAsync<T>(Func<T?> action, TimeSpan timeout, Process? liveness = null, string? description = null)
            where T : class
        {
            var label = description ?? typeof(T).Name;
            var stopwatch = Stopwatch.StartNew();

            while (true)
            {
                var result = action();
                if (result != null)
                {
                    return result;
                }

                if (liveness?.HasExited == true)
                {
                    throw new InvalidOperationException(
                        $"The process hosting '{label}' (PID {liveness.Id}) exited with code {liveness.ExitCode} after {stopwatch.Elapsed:mm\\:ss} without ever becoming available.");
                }

                if (stopwatch.Elapsed >= timeout)
                {
                    throw new TimeoutException(
                        $"'{label}' did not become available within {timeout.TotalMinutes:0.#} minute(s).");
                }

                await Task.Delay(PollInterval).ConfigureAwait(false);
            }
        }
    }
}
