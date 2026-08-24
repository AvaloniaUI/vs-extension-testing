// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for more information.

namespace Xunit.Harness
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Threading;
    using Xunit.Abstractions;
    using Xunit.Sdk;
    using Xunit.Threading;

    internal class IdeTestAssemblyRunner : XunitTestAssemblyRunner
    {
        /// <summary>
        /// A long timeout used to avoid hangs in tests, where a test failure manifests as an operation never occurring.
        /// </summary>
        private static readonly TimeSpan HangMitigatingTimeout = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Ceiling for an <see cref="IDisposable.Dispose"/> call on a cross-process (.NET Remoting) proxy.
        /// If the proxy's underlying IPC channel is alive-but-unresponsive the call can park indefinitely;
        /// 30 seconds is comfortably above any well-behaved teardown but well below the surrounding test
        /// host's hang watchdog (10 minutes for this repo).
        /// </summary>
        private static readonly TimeSpan RemoteDisposeTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Longest a test collection may run without a single test event before the IDE hosting it
        /// is treated as wedged rather than busy.
        /// </summary>
        /// <remarks>
        /// <para><c>RunTestCollection</c> is a synchronous .NET Remoting call
        /// that marshals onto devenv's UI thread. If that thread wedges the call never returns, and
        /// no in-IDE guard can help — a per-test timeout enforced inside the IDE is wedged along
        /// with everything else there. The run then goes silent until vstest's blame collector
        /// aborts the whole test host, discarding the remaining tests and naming nothing.</para>
        /// <para>Sized against the in-IDE per-sub-test ceiling, which is 5 minutes in this repo.
        /// That guard is the one that produces a *named* failing test, so it must always win the
        /// race: a sub-test that wedges is aborted at 5 minutes and the collection resumes
        /// reporting, whereas this watchdog can only kill the IDE and force a whole retry. 6
        /// minutes leaves the guard its full window plus a minute of slack, and stays below the
        /// 12-minute <c>--blame-hang-timeout</c> the consuming build passes so the harness still
        /// acts before vstest aborts the run anonymously.</para>
        /// <para>The slack is genuinely sufficient: measured over a clean run (32741020461), the
        /// slowest single test case is 35 seconds — the largest grouped scenario, which reports as
        /// one case and so is the longest legitimate silence there is. The cost of waiting longer
        /// is paid twice over on a bad run: each wedge burns this timeout before recovery even
        /// begins. If the in-IDE ceiling rises, this must rise with it.</para>
        /// </remarks>
        private static readonly TimeSpan StalledCollectionTimeout = TimeSpan.FromMinutes(6);

        /// <summary>How often the stall watchdog re-checks. Cheap; it only reads a timestamp.</summary>
        private static readonly TimeSpan StalledCollectionPollInterval = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Ceiling on writing the pre-kill hang dump. Generous enough for a stacks-only dump of
        /// devenv, small enough that a stuck capture only briefly delays releasing the blocked call.
        /// </summary>
        private static readonly TimeSpan HangDumpTimeout = TimeSpan.FromSeconds(90);

        private HashSet<VisualStudioInstanceKey>? _ideInstancesInTests;

        public IdeTestAssemblyRunner(ITestAssembly testAssembly, IEnumerable<IXunitTestCase> testCases, IMessageSink diagnosticMessageSink, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
            : base(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
        {
        }

        protected override async Task AfterTestAssemblyStartingAsync()
        {
            await base.AfterTestAssemblyStartingAsync().ConfigureAwait(false);
            TestCollectionOrderer = new TestCollectionOrdererWrapper(TestCollectionOrderer);
            _ideInstancesInTests = new HashSet<VisualStudioInstanceKey>();
        }

        protected override async Task BeforeTestAssemblyFinishedAsync()
        {
            _ideInstancesInTests = null;
            TestCollectionOrderer = ((TestCollectionOrdererWrapper)TestCollectionOrderer).Underlying;
            await base.BeforeTestAssemblyFinishedAsync();
        }

        protected override async Task<RunSummary> RunTestCollectionAsync(IMessageBus messageBus, ITestCollection testCollection, IEnumerable<IXunitTestCase> testCases, CancellationTokenSource cancellationTokenSource)
        {
            var result = new RunSummary();
            var completedTestCaseIds = new HashSet<string>();

            // Tests known to have crashed the VS instance while they were in-flight on a prior attempt.
            // On retry we push these to the end of the batch so a crash doesn't take down tests queued behind them.
            var suspectCrashingTestCaseIds = new HashSet<string>();
            try
            {
                // Handle [Fact], and also handle IdeSkippedDataRowTestCase that doesn't run inside Visual Studio
                var nonIdeTestCases = testCases.Where(testCase => testCase is not IdeTestCaseBase).ToArray();
                if (nonIdeTestCases.Any())
                {
                    var summary = await RunTestCollectionForUnspecifiedVersionAsync(completedTestCaseIds, messageBus, testCollection, nonIdeTestCases, cancellationTokenSource);
                    result.Aggregate(summary);
                }

                var ideTestCases = testCases.OfType<IdeTestCaseBase>().Where(testCase => testCase is not IdeInstanceTestCase).ToArray();
                foreach (var testCasesByTargetVersion in ideTestCases.GroupBy(GetVisualStudioVersionForTestCase))
                {
                    _ideInstancesInTests!.Add(testCasesByTargetVersion.Key);

                    var currentInstance = testCasesByTargetVersion.Key;
                    var currentTests = testCasesByTargetVersion.ToArray();

                    // Tests whose skip is already decided on the harness side (Skip= on the fact
                    // attribute, or the target VS version not being installed) never need the IDE.
                    // Report them from here and drop them from the batch, so a group consisting only
                    // of skipped tests doesn't pay a multi-minute Visual Studio launch just to have
                    // the in-IDE runner announce the skips. The group's key stays registered above so
                    // the end-of-run IdeInstanceTestCase doesn't launch an instance for it either.
                    var skippedTests = currentTests.Where(test => !string.IsNullOrEmpty(test.SkipReason)).ToArray();
                    if (skippedTests.Length > 0)
                    {
                        result.Aggregate(ReportStaticallySkippedTests(skippedTests, completedTestCaseIds));
                        currentTests = currentTests.Where(test => string.IsNullOrEmpty(test.SkipReason)).ToArray();
                        if (currentTests.Length == 0)
                        {
                            continue;
                        }
                    }

                    for (var currentAttempt = 0; currentAttempt < testCasesByTargetVersion.Key.MaxAttempts; currentAttempt++)
                    {
                        // On retry attempts, exclude tests known to have crashed VS on a prior attempt - xUnit's
                        // default class-level test ordering is alphabetical, so an IEnumerable reorder is not enough.
                        // Suspects are run in isolation (one VS instance each) after the main retry loop completes.
                        var testsForThisAttempt = currentAttempt > 0 && suspectCrashingTestCaseIds.Count > 0
                            ? currentTests.Where(test => !suspectCrashingTestCaseIds.Contains(test.UniqueID)).ToArray()
                            : currentTests;

                        if (testsForThisAttempt.Length > 0)
                        {
                            using var marshalledObjects = new MarshalledObjects();
                            using var visualStudioInstanceFactory = new VisualStudioInstanceFactory();

                            marshalledObjects.Add(visualStudioInstanceFactory);
                            var summary = await RunTestCollectionForVersionAsync(visualStudioInstanceFactory, currentAttempt, currentInstance, completedTestCaseIds, suspectCrashingTestCaseIds, messageBus, testCollection, testsForThisAttempt, cancellationTokenSource);
                            result.Aggregate(summary);
                        }

                        currentTests = currentTests.Where(test => !completedTestCaseIds.Contains(test.UniqueID)).ToArray();
                        if (currentTests.Length == 0 || currentTests.All(test => suspectCrashingTestCaseIds.Contains(test.UniqueID)))
                        {
                            break;
                        }
                    }

                    // Anything still uncompleted is a suspected VS-crasher. Run each in isolation in its own fresh
                    // VS instance with finalAttempt semantics so a crash only affects that single test.
                    foreach (var suspect in currentTests.Where(test => !completedTestCaseIds.Contains(test.UniqueID)))
                    {
                        using var marshalledObjects = new MarshalledObjects();
                        using var visualStudioInstanceFactory = new VisualStudioInstanceFactory();

                        marshalledObjects.Add(visualStudioInstanceFactory);
                        var summary = await RunTestCollectionForVersionAsync(visualStudioInstanceFactory, currentAttempt: currentInstance.MaxAttempts - 1, currentInstance, completedTestCaseIds, suspectCrashingTestCaseIds, messageBus, testCollection, new[] { suspect }, cancellationTokenSource);
                        result.Aggregate(summary);
                    }
                }

                foreach (var ideInstanceTestCase in testCases.OfType<IdeInstanceTestCase>())
                {
                    if (_ideInstancesInTests!.Contains(ideInstanceTestCase.VisualStudioInstanceKey))
                    {
                        // Already had at least one test run in this version, so no need to launch it separately.
                        // Report it as passed and continue.
                        ExecutionMessageSink.OnMessage(new TestClassStarting(new[] { ideInstanceTestCase }, ideInstanceTestCase.TestMethod.TestClass));
                        ExecutionMessageSink.OnMessage(new TestMethodStarting(new[] { ideInstanceTestCase }, ideInstanceTestCase.TestMethod));
                        ExecutionMessageSink.OnMessage(new TestCaseStarting(ideInstanceTestCase));

                        var test = new XunitTest(ideInstanceTestCase, ideInstanceTestCase.DisplayName);
                        ExecutionMessageSink.OnMessage(new TestStarting(test));
                        ExecutionMessageSink.OnMessage(new TestPassed(test, 0, output: null));
                        ExecutionMessageSink.OnMessage(new TestFinished(test, 0, output: null));

                        ExecutionMessageSink.OnMessage(new TestCaseFinished(ideInstanceTestCase, 0, 1, 0, 0));
                        ExecutionMessageSink.OnMessage(new TestMethodFinished(new[] { ideInstanceTestCase }, ideInstanceTestCase.TestMethod, 0, 1, 0, 0));
                        ExecutionMessageSink.OnMessage(new TestClassFinished(new[] { ideInstanceTestCase }, ideInstanceTestCase.TestMethod.TestClass, 0, 1, 0, 0));

                        continue;
                    }

                    using var marshalledObjects = new MarshalledObjects();
                    using (var visualStudioInstanceFactory = new VisualStudioInstanceFactory(leaveRunning: true))
                    {
                        marshalledObjects.Add(visualStudioInstanceFactory);
                        var summary = await RunTestCollectionForVersionAsync(visualStudioInstanceFactory, currentAttempt: 0, ideInstanceTestCase.VisualStudioInstanceKey, completedTestCaseIds, suspectCrashingTestCaseIds, messageBus, testCollection, new[] { ideInstanceTestCase }, cancellationTokenSource);
                        result.Aggregate(summary);
                    }
                }
            }
            catch (Exception ex)
            {
                var completedTestCases = testCases.Where(testCase => completedTestCaseIds.Contains(testCase.UniqueID));
                var remainingTestCases = testCases.Except(completedTestCases);
                foreach (var casesByTestClass in remainingTestCases.GroupBy(testCase => testCase.TestMethod.TestClass))
                {
                    ExecutionMessageSink.OnMessage(new TestClassStarting(casesByTestClass.ToArray(), casesByTestClass.Key));

                    foreach (var casesByTestMethod in casesByTestClass.GroupBy(testCase => testCase.TestMethod))
                    {
                        ExecutionMessageSink.OnMessage(new TestMethodStarting(casesByTestMethod.ToArray(), casesByTestMethod.Key));

                        foreach (var testCase in casesByTestMethod)
                        {
                            ExecutionMessageSink.OnMessage(new TestCaseStarting(testCase));

                            var test = new XunitTest(testCase, testCase.DisplayName);
                            ExecutionMessageSink.OnMessage(new TestStarting(test));
                            ExecutionMessageSink.OnMessage(new TestFailed(test, 0, null, new InvalidOperationException("Test did not run due to a harness failure.", ex)));
                            result.Failed++;
                            ExecutionMessageSink.OnMessage(new TestFinished(test, 0, null));

                            ExecutionMessageSink.OnMessage(new TestCaseFinished(testCase, 0, 1, 1, 0));
                        }

                        ExecutionMessageSink.OnMessage(new TestMethodFinished(casesByTestMethod.ToArray(), casesByTestMethod.Key, 0, casesByTestMethod.Count(), casesByTestMethod.Count(), 0));
                    }

                    ExecutionMessageSink.OnMessage(new TestClassFinished(casesByTestClass.ToArray(), casesByTestClass.Key, 0, casesByTestClass.Count(), casesByTestClass.Count(), 0));
                }
            }

            return result;
        }

        /// <summary>
        /// Reports test cases whose <see cref="XunitTestCase.SkipReason"/> is already known on the
        /// harness side without dispatching them to a Visual Studio instance. Only test-case-level
        /// messages are emitted: class/method envelopes for the same classes are announced by the
        /// VS-side run of their runnable siblings, and re-emitting those triggers "Key already
        /// exists" failures in the VSTest adapter.
        /// </summary>
        private RunSummary ReportStaticallySkippedTests(IReadOnlyList<IdeTestCaseBase> testCases, HashSet<string> completedTestCaseIds)
        {
            var summary = new RunSummary { Total = testCases.Count, Skipped = testCases.Count };
            foreach (var testCase in testCases)
            {
                var test = new XunitTest(testCase, testCase.DisplayName);
                ExecutionMessageSink.OnMessage(new TestCaseStarting(testCase));
                ExecutionMessageSink.OnMessage(new TestStarting(test));
                ExecutionMessageSink.OnMessage(new TestSkipped(test, testCase.SkipReason));
                ExecutionMessageSink.OnMessage(new TestFinished(test, 0, output: null));
                ExecutionMessageSink.OnMessage(new TestCaseFinished(testCase, 0, testsRun: 1, testsFailed: 0, testsSkipped: 1));
                completedTestCaseIds.Add(testCase.UniqueID);
            }

            return summary;
        }

        /// <param name="currentAttempt">The 0-based attempt number. If this value is
        /// <c><see cref="VisualStudioInstanceKey.MaxAttempts"/> - 1</c>, a failed test will not be retried.</param>
        protected virtual Task<RunSummary> RunTestCollectionForVersionAsync(VisualStudioInstanceFactory visualStudioInstanceFactory, int currentAttempt, VisualStudioInstanceKey visualStudioInstanceKey, HashSet<string> completedTestCaseIds, HashSet<string> suspectCrashingTestCaseIds, IMessageBus messageBus, ITestCollection testCollection, IEnumerable<IXunitTestCase> testCases, CancellationTokenSource cancellationTokenSource)
        {
            if (visualStudioInstanceKey.Version == VisualStudioVersion.Unspecified
                || !IdeTestCaseBase.IsInstalled(visualStudioInstanceKey.Version))
            {
                return RunTestCollectionForUnspecifiedVersionAsync(completedTestCaseIds, messageBus, testCollection, testCases, cancellationTokenSource);
            }

            DispatcherSynchronizationContext? synchronizationContext = null;
            Dispatcher? dispatcher = null;
            Thread staThread;
            using (var staThreadStartedEvent = new ManualResetEventSlim(initialState: false))
            {
                staThread = new Thread((ThreadStart)(() =>
                {
                    // All WPF Tests need a DispatcherSynchronizationContext and we don't want to block pending keyboard
                    // or mouse input from the user. So use background priority which is a single level below user input.
                    synchronizationContext = new DispatcherSynchronizationContext();
                    dispatcher = Dispatcher.CurrentDispatcher;

                    // xUnit creates its own synchronization context and wraps any existing context so that messages are
                    // still pumped as necessary. So we are safe setting it here, where we are not safe setting it in test.
                    SynchronizationContext.SetSynchronizationContext(synchronizationContext);

                    staThreadStartedEvent.Set();

                    Dispatcher.Run();
                }));

                staThread.Name = $"{nameof(IdeTestAssemblyRunner)}";
                staThread.SetApartmentState(ApartmentState.STA);
                staThread.Start();

                staThreadStartedEvent.Wait();
#pragma warning disable CA1508 // Avoid dead conditional code
                Debug.Assert(synchronizationContext != null, "Assertion failed: synchronizationContext != null");
#pragma warning restore CA1508 // Avoid dead conditional code
            }

            var taskScheduler = new SynchronizationContextTaskScheduler(synchronizationContext!);
            var task = Task.Factory.StartNew(
                async () =>
                {
                    Debug.Assert(SynchronizationContext.Current is DispatcherSynchronizationContext, "Assertion failed: SynchronizationContext.Current is DispatcherSynchronizationContext");

                    using (await WpfTestSharedData.Instance.TestSerializationGate.DisposableWaitAsync(CancellationToken.None))
                    {
                        // Just call back into the normal xUnit dispatch process now that we are on an STA Thread with no synchronization context.
                        var invoker = CreateTestCollectionInvoker(visualStudioInstanceFactory, currentAttempt, visualStudioInstanceKey, completedTestCaseIds, suspectCrashingTestCaseIds, messageBus, testCollection, testCases, cancellationTokenSource);
                        return await invoker().ConfigureAwait(true);
                    }
                },
                cancellationTokenSource.Token,
                TaskCreationOptions.None,
                taskScheduler).Unwrap();

            return Task.Run(
                async () =>
                {
                    try
                    {
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
                        return await task.ConfigureAwait(false);
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
                    }
                    finally
                    {
                        // Make sure to shut down the dispatcher. Certain framework types listed for the dispatcher
                        // shutdown to perform cleanup actions. In the absence of an explicit shutdown, these actions
                        // are delayed and run during AppDomain or process shutdown, where they can lead to crashes of
                        // the test process.
                        dispatcher!.InvokeShutdown();

                        // Join the STA thread, which ensures shutdown is complete.
                        staThread.Join(HangMitigatingTimeout);
                    }
                });
        }

        private async Task<RunSummary> RunTestCollectionForUnspecifiedVersionAsync(HashSet<string> completedTestCaseIds, IMessageBus messageBus, ITestCollection testCollection, IEnumerable<IXunitTestCase> testCases, CancellationTokenSource cancellationTokenSource, bool finalAttempt = true)
        {
            // These tests just run in the current process, but we still need to hook the assembly and collection events
            // to work correctly in mixed-testing scenarios.
            using var marshalledObjects = new MarshalledObjects();
            var executionMessageSinkFilter = new IpcMessageSink(ExecutionMessageSink, testCases.ToDictionary<IXunitTestCase, string, ITestCase>(testCase => testCase.UniqueID, testCase => testCase), finalAttempt, completedTestCaseIds, cancellationTokenSource.Token);
            marshalledObjects.Add(executionMessageSinkFilter);
            using (var runner = new XunitTestAssemblyRunner(TestAssembly, testCases, DiagnosticMessageSink, executionMessageSinkFilter, ExecutionOptions))
            {
                var runSummary = await runner.RunAsync();
                return runSummary;
            }
        }

        /// <param name="currentAttempt">The 0-based attempt number. If this value is
        /// <c><see cref="VisualStudioInstanceKey.MaxAttempts"/> - 1</c>, a failed test will not be retried.</param>
        private Func<Task<RunSummary>> CreateTestCollectionInvoker(VisualStudioInstanceFactory visualStudioInstanceFactory, int currentAttempt, VisualStudioInstanceKey visualStudioInstanceKey, HashSet<string> completedTestCaseIds, HashSet<string> suspectCrashingTestCaseIds, IMessageBus messageBus, ITestCollection testCollection, IEnumerable<IXunitTestCase> testCases, CancellationTokenSource cancellationTokenSource)
        {
            return async () =>
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());

                using var marshalledObjects = new MarshalledObjects();

                IpcMessageSink? executionMessageSinkFilter = null;

                try
                {
                    var finalAttempt = currentAttempt == visualStudioInstanceKey.MaxAttempts - 1;
                    var knownTestCasesByUniqueId = testCases.ToDictionary<IXunitTestCase, string, ITestCase>(testCase => testCase.UniqueID, testCase => testCase);
                    executionMessageSinkFilter = new IpcMessageSink(ExecutionMessageSink, knownTestCasesByUniqueId, finalAttempt, completedTestCaseIds, cancellationTokenSource.Token);
                    marshalledObjects.Add(executionMessageSinkFilter);

                    // Use SetItems instead of ToImmutableDictionary to avoid exceptions in the case of value conflicts
                    var environmentVariables = ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase).SetItems(
                        visualStudioInstanceKey.EnvironmentVariables.Select(
                            variable => variable.IndexOf('=') is var index && index > 0
                                ? new KeyValuePair<string, string>(variable.Substring(0, index), variable.Substring(index + 1))
                                : new KeyValuePair<string, string>(variable, string.Empty)));

                    // Install a COM message filter to handle retry operations when the first attempt fails.
                    // messageFilter is in-process COM; its Dispose can stay synchronous.
                    // visualStudioContext and runner are remoting proxies — if the VS process has gone
                    // away their Dispose() will throw fast, but if the channel is alive-but-unresponsive
                    // Dispose can park indefinitely. Wrap those with a bounded helper so the test host
                    // can never wedge on a dead-but-not-faulted IPC channel.
                    using (var messageFilter = new MessageFilter())
                    {
                        var visualStudioContext = await visualStudioInstanceFactory.GetNewOrUsedInstanceAsync(GetVersion(visualStudioInstanceKey.Version), visualStudioInstanceKey.RootSuffix, environmentVariables, GetExtensionFiles(testCases), ImmutableHashSet.Create<string>()).ConfigureAwait(true);
                        try
                        {
                            var runner = visualStudioContext.Instance.TestInvoker.CreateTestAssemblyRunner(new IpcTestAssembly(TestAssembly), testCases.ToArray(), new IpcMessageSink(DiagnosticMessageSink, knownTestCasesByUniqueId, finalAttempt, new HashSet<string>(), cancellationTokenSource.Token), executionMessageSinkFilter, new IpcTestFrameworkExecutionOptions(ExecutionOptions));
                            try
                            {
                                marshalledObjects.Add(runner);

                                var ipcMessageBus = new IpcMessageBus(messageBus);
                                marshalledObjects.Add(ipcMessageBus);

                                // RunTestCollection blocks this thread until the IDE has run every
                                // test in the collection. Watch for the IDE going silent while it
                                // does; see StartCollectionStallWatchdog.
                                using (StartCollectionStallWatchdog(executionMessageSinkFilter, visualStudioContext.Instance.HostProcess))
                                {
                                    var result = runner.RunTestCollection(ipcMessageBus, testCollection, testCases.ToArray());
                                    var runSummary = new RunSummary
                                    {
                                        Total = result.Item1,
                                        Failed = result.Item2,
                                        Skipped = result.Item3,
                                        Time = result.Item4,
                                    };

                                    return runSummary;
                                }
                            }
                            finally
                            {
                                BoundedDispose(runner, RemoteDisposeTimeout, nameof(runner));
                            }
                        }
                        finally
                        {
                            BoundedDispose(visualStudioContext, RemoteDisposeTimeout, nameof(visualStudioContext));
                        }
                    }
                }
                catch (Exception e)
                {
                    // Since this exception occurred in the harness communication, we can't assume it was logged by the
                    // in-process data collection service. We need to log it separately here.
                    DataCollectionService.CaptureFailureState(executionMessageSinkFilter?.CurrentTestCase ?? "Unknown", e);

                    // Remember which test was mid-flight at the moment of the crash so the outer retry loop can push
                    // it to the end of the batch, giving queued-but-unstarted tests a chance to actually run on retry.
                    if (executionMessageSinkFilter?.CurrentTestCaseId is { } inFlightId)
                    {
                        suspectCrashingTestCaseIds.Add(inFlightId);
                    }

                    // Only report failure for tests that didn't already complete successfully. Tests recorded in
                    // completedTestCaseIds have already been reported to the runner with their real results; re-running
                    // them through the error runner would flip earlier passes to failures.
                    var remainingTestCases = testCases.Where(tc => !completedTestCaseIds.Contains(tc.UniqueID)).ToArray();
                    if (remainingTestCases.Length == 0)
                    {
                        return new RunSummary();
                    }

                    var finalAttempt = currentAttempt == visualStudioInstanceKey.MaxAttempts - 1;
                    var inFlightCaseId = executionMessageSinkFilter?.CurrentTestCaseId;

                    // An isolation run passes a single test through a fresh VS on the final attempt. If
                    // that single test was the in-flight crasher, the crash is definitive and we report
                    // it as failed. In any other shape we emit skipped-will-retry so the test gets another
                    // chance via the isolation loop.
                    var isIsolatedFinalCrash = finalAttempt && remainingTestCases.Length == 1 && remainingTestCases[0].UniqueID == inFlightCaseId;
                    var runSummary = new RunSummary { Total = remainingTestCases.Length };

                    var retrySkipMessage = BuildHarnessFailureSkipMessage(e, currentAttempt, visualStudioInstanceKey.MaxAttempts, inFlightCaseId);

                    // Emit test-case-level messages directly. We deliberately avoid re-running via
                    // XunitTestAssemblyRunner (as previous versions did) because that would re-emit
                    // TestAssemblyStarting / TestCollectionStarting / TestClassStarting / TestMethodStarting
                    // for entities the VS-side run already announced, triggering "Key already exists in
                    // the message metadata cache" fatal errors in the VSTest adapter.
                    foreach (var testCase in remainingTestCases)
                    {
                        var isInFlight = testCase.UniqueID == inFlightCaseId;
                        var test = new XunitTest(testCase, testCase.DisplayName);

                        // The in-flight test already had TestCaseStarting and TestStarting emitted by the
                        // VS-side run before the crash. Don't re-emit them, or the adapter throws duplicate-key.
                        if (!isInFlight)
                        {
                            ExecutionMessageSink.OnMessage(new TestCaseStarting(testCase));
                            ExecutionMessageSink.OnMessage(new TestStarting(test));
                        }

                        if (isIsolatedFinalCrash && isInFlight)
                        {
                            var harnessFailure = new InvalidOperationException(retrySkipMessage, e);
                            ExecutionMessageSink.OnMessage(new TestFailed(test, 0, output: null, harnessFailure));
                            ExecutionMessageSink.OnMessage(new TestFinished(test, 0, output: null));
                            ExecutionMessageSink.OnMessage(new TestCaseFinished(testCase, 0, testsRun: 1, testsFailed: 1, testsSkipped: 0));
                            runSummary.Failed++;
                            completedTestCaseIds.Add(testCase.UniqueID);
                        }
                        else
                        {
                            ExecutionMessageSink.OnMessage(new TestSkipped(test, retrySkipMessage));
                            ExecutionMessageSink.OnMessage(new TestFinished(test, 0, output: null));
                            ExecutionMessageSink.OnMessage(new TestCaseFinished(testCase, 0, testsRun: 1, testsFailed: 0, testsSkipped: 1));
                            runSummary.Skipped++;
                        }
                    }

                    return runSummary;
                }
            };
        }

        /// <summary>
        /// Watches for the IDE going silent during a test collection and, if it does, kills the IDE
        /// so the blocked <c>RunTestCollection</c> call is released.
        /// </summary>
        /// <param name="progress">The execution message sink; every message from the IDE resets its
        /// stall clock, making it the only reliable liveness signal for work happening in another
        /// process.</param>
        /// <param name="hostProcess">The IDE process to kill if the collection stalls.</param>
        /// <returns>A handle that stops the watchdog when disposed.</returns>
        /// <remarks>
        /// Killing the host is the only lever available: the call is a synchronous remoting call
        /// that cannot be cancelled, so the channel has to be broken for it to return. Once it
        /// does, the call throws, the surrounding handler captures failure state, records the
        /// in-flight test as a suspected crasher, and the attempt loop retries it on a fresh
        /// instance — a named, retryable failure instead of a silent test-host abort.
        /// </remarks>
        private static IDisposable StartCollectionStallWatchdog(IpcMessageSink progress, Process hostProcess)
        {
            progress.MarkActivity();

            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        while (true)
                        {
                            await Task.Delay(StalledCollectionPollInterval, cancellationToken).ConfigureAwait(false);

                            if (hostProcess.HasExited)
                            {
                                // The channel is already broken, so the call is unblocking on its own.
                                return;
                            }

                            var stalledFor = progress.TimeSinceLastMessage;
                            if (stalledFor < StalledCollectionTimeout)
                            {
                                continue;
                            }

                            var inFlight = progress.CurrentTestCase ?? "(between tests)";
                            DataCollectionService.RecordHarnessPhase(
                                $"run-test-collection: STALLED — no test activity for {stalledFor:hh\\:mm\\:ss} " +
                                $"(limit {StalledCollectionTimeout.TotalMinutes:0.#}m), in-flight test '{inFlight}'. " +
                                $"Killing devenv (PID {hostProcess.Id}) to release the blocked call.");
                            var captureName = $"run-test-collection-stall-{hostProcess.Id}";
                            DataCollectionService.TryCaptureScreenshot(captureName);

                            // Capture the thread stacks before killing: they are the only record of
                            // what the UI thread was blocked on, and the kill destroys them. Bounded
                            // on its own thread because writing a dump of devenv takes tens of
                            // seconds and must never become the reason the blocked call isn't
                            // released — on timeout we abandon the dump and kill anyway.
                            var dumpThread = new Thread(() => DataCollectionService.TryCaptureHangDump(hostProcess, captureName))
                            {
                                IsBackground = true,
                                Name = "Stall hang dump",
                            };
                            dumpThread.Start();
                            dumpThread.Join(HangDumpTimeout);

                            IntegrationHelper.KillProcess(hostProcess);
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal shutdown: the collection finished before the watchdog fired.
                    }
                    catch (Exception e)
                    {
                        DataCollectionService.RecordHarnessPhase(
                            $"run-test-collection: watchdog stopped after {e.GetType().Name}: {e.Message}");
                    }
                },
                CancellationToken.None);

            return new CancelOnDispose(cancellationTokenSource);
        }

        /// <summary>
        /// Dispose <paramref name="target"/> on a background thread with a hard <paramref name="timeout"/>.
        /// If the call doesn't return in time, the wait is abandoned and the worker thread is left to
        /// finish (or be reaped with the test host). Use this for remoting proxies where the underlying
        /// IPC channel may be alive-but-unresponsive — synchronous Dispose on those can park indefinitely.
        /// A <see cref="System.Runtime.Remoting.RemotingException"/> from the proxy is swallowed here:
        /// the IDE is being torn down anyway, and surfacing it would mask the original failure.
        /// </summary>
        private static void BoundedDispose(IDisposable? target, TimeSpan timeout, string label)
        {
            if (target is null)
            {
                return;
            }

            using var done = new ManualResetEventSlim(false);
            var disposer = new Thread(() =>
            {
                try
                {
                    target.Dispose();
                }
                catch
                {
                    // Suppress: see method summary. The original test failure must not be masked.
                }
                finally
                {
                    done.Set();
                }
            })
            {
                IsBackground = true,
                Name = "Bounded Dispose: " + label,
            };
            disposer.Start();
            done.Wait(timeout);

            // Whether the wait returned true (Dispose completed) or false (we're abandoning), we
            // return synchronously here. Abandoned workers are harmless background threads.
        }

        /// <summary>
        /// Builds a multi-line message describing a harness-side failure that occurred while running a batch of
        /// tests inside the Visual Studio instance. Includes the exception type/message, attempt counters, the
        /// in-flight test case (if any) and the directory where the full exception, screenshots, activity log
        /// and IDE state were captured by <see cref="DataCollectionService.CaptureFailureState"/>.
        /// </summary>
        private static string BuildHarnessFailureSkipMessage(Exception ex, int currentAttempt, int maxAttempts, string? inFlightCaseId)
        {
            var sb = new StringBuilder();
            sb.Append("Test will be retried in a fresh VS instance (attempt ")
              .Append(currentAttempt + 1).Append('/').Append(maxAttempts).AppendLine(" failed in the test harness).");

            sb.AppendLine();
            sb.Append("Harness exception: ").Append(ex.GetType().FullName).Append(": ").AppendLine(ex.Message);
            if (!string.IsNullOrEmpty(inFlightCaseId))
            {
                sb.Append("In-flight test at time of failure: ").AppendLine(inFlightCaseId);
            }
            else
            {
                sb.AppendLine("No test was mid-flight when the failure occurred (the harness failed before dispatching a test). This usually indicates the experimental VS instance failed to start, the VSIX under test failed to load, or a remoting/COM handshake failed.");
            }

            string? logDir = null;
            try
            {
                logDir = DataCollectionService.GetLogDirectory();
            }
            catch
            {
                // Best-effort: never let log-path discovery hide the original exception info.
            }

            if (!string.IsNullOrEmpty(logDir))
            {
                sb.AppendLine();
                sb.Append("Full exception, screenshot, DotNet/Watson event log entries and (if available) VS Activity log and IDE state were written to:").AppendLine();
                sb.Append("  ").AppendLine(logDir);
                sb.AppendLine("Look for files named '<time>-<TestName>-<ExceptionType>.log' (exception) and '.png' (screenshot at time of failure).");
            }

            sb.AppendLine();
            sb.AppendLine("Common causes:");
            sb.AppendLine("  - The experimental VS instance crashed or failed to start (check the Watson and IDE logs).");
            sb.AppendLine("  - The VSIX under test failed to load into the experimental hive (check the Activity log).");
            sb.AppendLine("  - A required MEF component is missing or the experimental hive is corrupt (try resetting it: 'devenv /rootsuffix Exp /resetuserdata').");
            sb.AppendLine("  - A remoting / COM call across the test harness boundary timed out or failed.");

            return sb.ToString().TrimEnd();
        }

        private ImmutableList<string> GetExtensionFiles(IEnumerable<IXunitTestCase> testCases)
        {
            var extensionFiles = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<IAssemblyInfo>();
            foreach (var testCase in testCases)
            {
                var assemblyInfo = testCase.Method.Type.Assembly;
                if (!visited.Add(assemblyInfo))
                {
                    continue;
                }

                var requiredExtensions = assemblyInfo.GetCustomAttributes(typeof(RequireExtensionAttribute));
                extensionFiles = extensionFiles.Union(requiredExtensions.Select(attributeInfo => attributeInfo.GetConstructorArguments().First().ToString()));
            }

            return extensionFiles.ToImmutableList();
        }

        private static Version GetVersion(VisualStudioVersion visualStudioVersion)
        {
            switch (visualStudioVersion)
            {
            case VisualStudioVersion.VS2012:
                return new Version(11, 0);

            case VisualStudioVersion.VS2013:
                return new Version(12, 0);

            case VisualStudioVersion.VS2015:
                return new Version(14, 0);

            case VisualStudioVersion.VS2017:
                return new Version(15, 0);

            case VisualStudioVersion.VS2019:
                return new Version(16, 0);

            case VisualStudioVersion.VS2022:
                return new Version(17, 0);

            case VisualStudioVersion.VS18:
                return new Version(18, 0);

            default:
                throw new ArgumentException();
            }
        }

        private VisualStudioInstanceKey GetVisualStudioVersionForTestCase(IXunitTestCase testCase)
        {
            if (testCase is IdeTestCaseBase ideTestCase)
            {
                return ideTestCase.VisualStudioInstanceKey;
            }

            return VisualStudioInstanceKey.Unspecified;
        }

        /// <summary>Cancels the wrapped source on <see cref="Dispose"/>, stopping a watchdog loop.</summary>
        private sealed class CancelOnDispose : IDisposable
        {
            private readonly CancellationTokenSource _cancellationTokenSource;

            public CancelOnDispose(CancellationTokenSource cancellationTokenSource)
                => _cancellationTokenSource = cancellationTokenSource;

            public void Dispose()
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
            }
        }

        private class IpcMessageSink : MarshalByRefObject, IMessageSink
        {
            private readonly IMessageSink _messageSink;
            private readonly IReadOnlyDictionary<string, ITestCase> _knownTestCasesByUniqueId;
            private readonly CancellationToken _cancellationToken;

            private readonly bool _finalAttempt;
            private readonly HashSet<string> _completedTestCaseIds;

            /// <summary>
            /// <see cref="DateTime.UtcNow"/> ticks at the last message from the IDE. Written from
            /// remoting threads and read by the stall watchdog, so it is exchanged atomically
            /// rather than stored as a <see cref="DateTime"/>.
            /// </summary>
            private long _lastMessageTicks;

            public IpcMessageSink(IMessageSink messageSink, IReadOnlyDictionary<string, ITestCase> knownTestCasesByUniqueId, bool finalAttempt, HashSet<string> completedTestCaseIds, CancellationToken cancellationToken)
            {
                _messageSink = messageSink;
                _knownTestCasesByUniqueId = knownTestCasesByUniqueId;
                _finalAttempt = finalAttempt;
                _completedTestCaseIds = completedTestCaseIds;
                _cancellationToken = cancellationToken;
                MarkActivity();
            }

            public string? CurrentTestCase
            {
                get;
                private set;
            }

            public string? CurrentTestCaseId
            {
                get;
                private set;
            }

            /// <summary>Gets how long it has been since the IDE last sent this sink a message.</summary>
            public TimeSpan TimeSinceLastMessage
                => DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastMessageTicks), DateTimeKind.Utc);

            /// <summary>Resets the stall clock. Called on every message, and once when armed.</summary>
            public void MarkActivity()
                => Interlocked.Exchange(ref _lastMessageTicks, DateTime.UtcNow.Ticks);

            public bool OnMessage(IMessageSinkMessage message)
            {
                MarkActivity();

                if (message is ITestAssemblyFinished testAssemblyFinished)
                {
                    // The test cases in the ITestAssemblyFinished message are remote proxies, but the objects won't be
                    // used until after the remote process terminates. Recreate the objects in the current process (or
                    // map them to an equivalent object already in the current process) to avoid using objects that are
                    // no longer available.
                    var testCases = testAssemblyFinished.TestCases.Select(testCase =>
                    {
                        if (_knownTestCasesByUniqueId.TryGetValue(testCase.UniqueID, out var knownTestCase))
                        {
                            return knownTestCase;
                        }
                        else if (testCase is IdeTestCase ideTestCase)
                        {
                            return new IdeTestCase(this, ideTestCase.DefaultMethodDisplay, ideTestCase.DefaultMethodDisplayOptions, ideTestCase.TestMethod, ideTestCase.VisualStudioInstanceKey, ideTestCase.TestMethodArguments);
                        }
                        else if (testCase is IdeTheoryTestCase ideTheoryTestCase)
                        {
                            return new IdeTheoryTestCase(this, ideTheoryTestCase.DefaultMethodDisplay, ideTheoryTestCase.DefaultMethodDisplayOptions, ideTheoryTestCase.TestMethod, ideTheoryTestCase.VisualStudioInstanceKey, ideTheoryTestCase.TestMethodArguments);
                        }
                        else if (testCase is IdeInstanceTestCase ideInstanceTestCase)
                        {
                            return new IdeInstanceTestCase(this, ideInstanceTestCase.DefaultMethodDisplay, ideInstanceTestCase.DefaultMethodDisplayOptions, ideInstanceTestCase.TestMethod, ideInstanceTestCase.VisualStudioInstanceKey, ideInstanceTestCase.TestMethodArguments);
                        }
                        else
                        {
                            return new XunitTestCase(this, TestMethodDisplay.ClassAndMethod, TestMethodDisplayOptions.None, testCase.TestMethod, testCase.TestMethodArguments);
                        }
                    });

                    return !_cancellationToken.IsCancellationRequested;
                }
                else if (message is ITestCaseMessage testCaseMessage && _completedTestCaseIds.Contains(testCaseMessage.TestCase.UniqueID))
                {
                    // Defense-in-depth: drop any stray messages for test cases that have already finished.
                    // Re-emitting results for already-completed tests (e.g. via a post-crash error-reporter pass)
                    // would flip earlier passes to failures in the runner UI.
                    return !_cancellationToken.IsCancellationRequested;
                }
                else if (message is ITestCaseStarting testCaseStarting)
                {
                    CurrentTestCase = DataCollectionService.GetTestName(testCaseStarting.TestCase);
                    CurrentTestCaseId = testCaseStarting.TestCase.UniqueID;
                    return _messageSink.OnMessage(message);
                }
                else if (message is ITestCaseFinished testCaseFinished)
                {
                    CurrentTestCase = null;
                    CurrentTestCaseId = null;

                    if (_finalAttempt || testCaseFinished.TestsFailed == 0)
                    {
                        _completedTestCaseIds.Add(testCaseFinished.TestCase.UniqueID);
                    }
                    else
                    {
                        // This test will run again; report the statistics as skipped instead of failed
                        message = new TestCaseFinished(
                            testCaseFinished.TestCase,
                            testCaseFinished.ExecutionTime,
                            testCaseFinished.TestsRun,
                            testsFailed: 0,
                            testCaseFinished.TestsSkipped + testCaseFinished.TestsFailed);
                    }

                    if (_cancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    return _messageSink.OnMessage(message);
                }
                else if (!_finalAttempt && message is ITestFailed testFailed)
                {
                    // This test failed a non-final attempt and will be retried. Record it so flaky
                    // tests (fail-then-pass) can be identified after the run, and surface the cause
                    // in the skip reason so it is greppable in the build log. The full per-attempt
                    // exception, screenshot, and IDE state are captured separately by DataCollectionService.
                    var errorId = testFailed.ExceptionTypes.FirstOrDefault() ?? "Unknown";
                    var failureMessage = testFailed.Messages.FirstOrDefault() ?? string.Empty;
                    DataCollectionService.RecordRetriedFailure(testFailed.Test.DisplayName, errorId, failureMessage);

                    // This test will run again; report it as skipped instead of failed
                    message = new TestSkipped(testFailed.Test, $"FLAKY (will retry): failed with {errorId}.");
                }
                else if (message is ITestAssemblyStarting)
                {
                    return !_cancellationToken.IsCancellationRequested;
                }

                return _messageSink.OnMessage(message);
            }

            // The life of this object is managed explicitly
            public override object? InitializeLifetimeService()
            {
                return null;
            }
        }

        private class IpcMessageBus : MarshalByRefObject, IMessageBus
        {
            private readonly IMessageBus _messageBus;

            public IpcMessageBus(IMessageBus messageBus)
            {
                _messageBus = messageBus;
            }

            public void Dispose() => _messageBus.Dispose();

            public bool QueueMessage(IMessageSinkMessage message) => _messageBus.QueueMessage(message);

            // The life of this object is managed explicitly
            public override object? InitializeLifetimeService() => null;
        }

        private class IpcTestAssembly : LongLivedMarshalByRefObject, ITestAssembly
        {
            private readonly ITestAssembly _testAssembly;
            private readonly IAssemblyInfo _assembly;

            public IpcTestAssembly(ITestAssembly testAssembly)
            {
                _testAssembly = testAssembly;
                _assembly = new IpcAssemblyInfo(_testAssembly.Assembly);
            }

            public IAssemblyInfo Assembly => _assembly;

            public string ConfigFileName => _testAssembly.ConfigFileName;

            public void Deserialize(IXunitSerializationInfo info)
            {
                _testAssembly.Deserialize(info);
            }

            public void Serialize(IXunitSerializationInfo info)
            {
                _testAssembly.Serialize(info);
            }
        }

        private class IpcTestFrameworkExecutionOptions : LongLivedMarshalByRefObject, ITestFrameworkExecutionOptions
        {
            private readonly ITestFrameworkExecutionOptions _executionOptions;

            public IpcTestFrameworkExecutionOptions(ITestFrameworkExecutionOptions executionOptions)
            {
                _executionOptions = executionOptions;
            }

            public TValue GetValue<TValue>(string name)
            {
                return _executionOptions.GetValue<TValue>(name);
            }

            public void SetValue<TValue>(string name, TValue value)
            {
                _executionOptions.SetValue(name, value);
            }
        }

        private class IpcAssemblyInfo : LongLivedMarshalByRefObject, IAssemblyInfo
        {
            private IAssemblyInfo _assemblyInfo;

            public IpcAssemblyInfo(IAssemblyInfo assemblyInfo)
            {
                _assemblyInfo = assemblyInfo;
            }

            public string AssemblyPath => _assemblyInfo.AssemblyPath;

            public string Name => _assemblyInfo.Name;

            public IEnumerable<IAttributeInfo> GetCustomAttributes(string assemblyQualifiedAttributeTypeName)
            {
                return _assemblyInfo.GetCustomAttributes(assemblyQualifiedAttributeTypeName).ToArray();
            }

            public ITypeInfo GetType(string typeName)
            {
                return _assemblyInfo.GetType(typeName);
            }

            public IEnumerable<ITypeInfo> GetTypes(bool includePrivateTypes)
            {
                return _assemblyInfo.GetTypes(includePrivateTypes).ToArray();
            }
        }

        /// <summary>
        /// A collection orderer wrapper that ensures <see cref="IdeInstanceTestCase"/> runs after other test cases.
        /// </summary>
        private sealed class TestCollectionOrdererWrapper : ITestCollectionOrderer
        {
            public TestCollectionOrdererWrapper(ITestCollectionOrderer underlying)
            {
                Underlying = underlying;
            }

            public ITestCollectionOrderer Underlying { get; }

            public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
            {
                var collections = Underlying.OrderTestCollections(testCollections).ToArray();
                var collectionsWithoutIdeInstanceCases = collections.Where(collection => !ContainsIdeInstanceCase(collection));
                var collectionsWithIdeInstanceCases = collections.Where(collection => ContainsIdeInstanceCase(collection));
                return collectionsWithoutIdeInstanceCases.Concat(collectionsWithIdeInstanceCases);
            }

            private static bool ContainsIdeInstanceCase(ITestCollection collection)
            {
                var assemblyName = new AssemblyName(collection.TestAssembly.Assembly.Name);
                return assemblyName.Name == "Microsoft.VisualStudio.Extensibility.Testing.Xunit";
            }
        }
    }
}
