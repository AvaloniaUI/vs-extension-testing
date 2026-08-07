// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for more information.

namespace Xunit.Harness
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Runtime.Remoting.Channels;
    using System.Runtime.Remoting.Channels.Ipc;
    using System.Runtime.Serialization.Formatters;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.IntegrationTestService;
    using Windows.Win32;
    using Windows.Win32.Foundation;
    using Windows.Win32.System.Threading;
    using Xunit.InProcess;
    using Xunit.OutOfProcess;
    using DTE = EnvDTE.DTE;

    internal class VisualStudioInstance
    {
        /// <summary>
        /// Upper bound on any single remoting call made while shutting an instance down. The calls
        /// marshal onto the host's UI thread with no timeout of their own, so a devenv wedged at
        /// shutdown would otherwise block <see cref="Close"/> forever and keep the
        /// <see cref="CloseHostProcess"/> kill fallback from ever running.
        /// </summary>
        private const int ShutdownRemotingCallTimeoutMilliseconds = 10000;

        private readonly IntegrationService _integrationService;
        private readonly IpcChannel _integrationServiceChannel;
        private readonly VisualStudio_InProc _inProc;

        public VisualStudioInstance(Process hostProcess, DTE dte, Version version, ImmutableHashSet<string> supportedPackageIds, string installationPath)
        {
            HostProcess = hostProcess;
            Dte = dte;
            Version = version;
            SupportedPackageIds = supportedPackageIds;
            InstallationPath = installationPath;

            if (Debugger.IsAttached)
            {
                // If a Visual Studio debugger is attached to the test process, attach it to the instance running
                // integration tests as well.
                var debuggerHostDte = GetDebuggerHostDte();
                int targetProcessId = Process.GetCurrentProcess().Id;
                var localProcess = debuggerHostDte?.Debugger.LocalProcesses.OfType<EnvDTE80.Process2>().FirstOrDefault(p => p.ProcessID == hostProcess.Id);
                localProcess?.Attach2(VSConstants.DebugEnginesGuids.ManagedOnly_string);
            }

            StartRemoteIntegrationService(dte);

            string portName = $"IPC channel client for {HostProcess.Id}";
            _integrationServiceChannel = new IpcChannel(
                new Hashtable
                {
                    { "name", portName },
                    { "portName", portName },
                },
                new BinaryClientFormatterSinkProvider(),
                new BinaryServerFormatterSinkProvider { TypeFilterLevel = TypeFilterLevel.Full });

            ChannelServices.RegisterChannel(_integrationServiceChannel, ensureSecurity: false);

            // Connect to a 'well defined, shouldn't conflict' IPC channel
            _integrationService = IntegrationService.GetInstanceFromHostProcess(hostProcess);

            // Create marshal-by-ref object that runs in host-process.
            _inProc = ExecuteInHostProcess<VisualStudio_InProc>(
                type: typeof(VisualStudio_InProc),
                methodName: nameof(VisualStudio_InProc.Create));

            // There is a lot of VS initialization code that goes on, so we want to wait for that to 'settle' before
            // we start executing any actual code.
            _inProc.WaitForSystemIdle();

            TestInvoker = new TestInvoker_OutOfProc(this);

            // Ensure we are in a known 'good' state by cleaning up anything changed by the previous instance
            CleanUp();
        }

        internal DTE Dte
        {
            get;
        }

        internal Process HostProcess
        {
            get;
        }

        public Version Version
        {
            get;
        }

        /// <summary>
        /// Gets the set of Visual Studio packages that are installed into this instance.
        /// </summary>
        public ImmutableHashSet<string> SupportedPackageIds
        {
            get;
        }

        /// <summary>
        /// Gets the path to the root of this installed version of Visual Studio. This is the folder that contains
        /// Common7\IDE.
        /// </summary>
        public string InstallationPath
        {
            get;
        }

        public TestInvoker_OutOfProc TestInvoker
        {
            get;
        }

        public bool IsRunning => !HostProcess.HasExited;

        private static DTE? GetDebuggerHostDte()
        {
            var currentProcessId = Process.GetCurrentProcess().Id;
            var checkedProcesses = new List<int>();

            // Check ancestor processes first to avoid checking the DTE for unrelated processes when possible
            foreach (var process in GetAncestorProcesses(Process.GetCurrentProcess()))
            {
                if (process.ProcessName != "devenv")
                {
                    continue;
                }

                if (TryGetDteForAttachedDebugger(process, currentProcessId) is { } dte)
                {
                    return dte;
                }

                checkedProcesses.Add(process.Id);
            }

            foreach (var process in Process.GetProcessesByName("devenv"))
            {
                if (checkedProcesses.Contains(process.Id))
                {
                    continue;
                }

                if (TryGetDteForAttachedDebugger(process, currentProcessId) is { } dte)
                {
                    return dte;
                }
            }

            return null;

            static DTE? TryGetDteForAttachedDebugger(Process process, int currentProcessId)
            {
                var dte = IntegrationHelper.TryLocateDteForProcess(process);
                if (dte?.Debugger?.DebuggedProcesses?.OfType<EnvDTE.Process>().Any(p => p.ProcessID == currentProcessId) ?? false)
                {
                    return dte;
                }

                return null;
            }

            static IEnumerable<Process> GetAncestorProcesses(Process process)
            {
                for (var current = TryGetParentProcess(process); current is not null; current = TryGetParentProcess(current))
                {
                    yield return current;
                }

                static unsafe Process? TryGetParentProcess(Process process)
                {
                    PROCESS_BASIC_INFORMATION pbi = default;
                    var returnLength = 0U;
                    var status = PInvoke.NtQueryInformationProcess((HANDLE)process.Handle, PROCESSINFOCLASS.ProcessBasicInformation, &pbi, (uint)Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), &returnLength);
                    if (status != 0)
                    {
                        return null;
                    }

                    if (pbi.InheritedFromUniqueProcessId == IntPtr.Zero)
                    {
                        return null;
                    }

                    try
                    {
                        return Process.GetProcessById(pbi.InheritedFromUniqueProcessId.ToInt32());
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
        }

        public T ExecuteInHostProcess<T>(Type type, string methodName)
        {
            var objectUri = _integrationService.Execute(type.Assembly.Location, type.FullName, methodName) ?? throw new InvalidOperationException("The specified call was expected to return a value.");
            return (T)Activator.GetObject(typeof(T), $"{_integrationService.BaseUri}/{objectUri}");
        }

        public void AddCodeBaseDirectory(string directory)
            => _inProc.AddCodeBaseDirectory(directory);

        public void CleanUp()
        {
        }

        public void Close(bool exitHostProcess = true)
        {
            if (!IsRunning)
            {
                return;
            }

            CleanUp();

            try
            {
                CloseRemotingService();
            }
            finally
            {
                if (exitHostProcess)
                {
                    CloseHostProcess();
                }
            }
        }

        private void CloseHostProcess()
        {
            // Quit only posts the Exit command, but the remoting round-trip itself can hang if the
            // host's UI thread is wedged; bound it so the kill fallback below stays reachable.
            TryRemotingCallWithTimeout(_inProc.Quit, ShutdownRemotingCallTimeoutMilliseconds);
            if (!HostProcess.WaitForExit(milliseconds: 10000))
            {
                IntegrationHelper.KillProcess(HostProcess);
            }
        }

        private void CloseRemotingService()
        {
            try
            {
                TryRemotingCallWithTimeout(StopRemoteIntegrationService, ShutdownRemotingCallTimeoutMilliseconds);
            }
            finally
            {
                if (_integrationServiceChannel != null
                    && ChannelServices.RegisteredChannels.Contains(_integrationServiceChannel))
                {
                    ChannelServices.UnregisterChannel(_integrationServiceChannel);
                }
            }
        }

        /// <summary>
        /// Runs a synchronous remoting call into the host process, giving up after
        /// <paramref name="timeoutMilliseconds"/>. On timeout the blocked call is abandoned (its
        /// thread pool thread stays blocked until the test process exits) and the caller proceeds,
        /// ultimately to <see cref="IntegrationHelper.KillProcess(Process)"/>. A call that faults
        /// because the channel or host is already dead is treated the same way.
        /// </summary>
        private static bool TryRemotingCallWithTimeout(Action remotingCall, int timeoutMilliseconds)
        {
            var task = Task.Run(remotingCall);

            // Observe any late/faulted result so an abandoned call can't surface as an unobserved
            // task exception after this method has already given up on it.
            _ = task.ContinueWith(
                t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

            try
            {
                // This is client-side code in the test process with no JoinableTaskFactory and no UI
                // thread to deadlock with; a bounded synchronous wait is the point of this helper.
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
                return task.Wait(timeoutMilliseconds);
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
            }
            catch (AggregateException)
            {
                return false;
            }
        }

        private void StartRemoteIntegrationService(DTE dte)
        {
            // We use DTE over RPC to start the integration service. All other DTE calls should happen in the host process.
            if (dte.Commands.Item(WellKnownCommandNames.IntegrationTestServiceStart).IsAvailable)
            {
                dte.ExecuteCommand(WellKnownCommandNames.IntegrationTestServiceStart);
            }
        }

        private void StopRemoteIntegrationService()
        {
            if (_inProc.IsCommandAvailable(WellKnownCommandNames.IntegrationTestServiceStop))
            {
                _inProc.ExecuteCommand(WellKnownCommandNames.IntegrationTestServiceStop);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            internal IntPtr Reserved1;
            internal IntPtr PebBaseAddress;
            internal IntPtr Reserved2First;
            internal IntPtr Reserved2Second;
            internal IntPtr UniqueProcessId;
            internal IntPtr InheritedFromUniqueProcessId;
        }
    }
}
