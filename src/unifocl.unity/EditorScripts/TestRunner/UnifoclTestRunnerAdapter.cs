// UNITY_TESTS_FRAMEWORK mirrors the accompanying asmdef's define constraint. Unity supplies it
// through the asmdef's versionDefines when com.unity.test-framework is installed; the guard keeps
// the file compiling cleanly in harnesses that build these sources directly (e.g. compatcheck)
// without the Test Framework assemblies on the reference path.
#if UNITY_EDITOR && UNITY_TESTS_FRAMEWORK
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UniFocl.EditorBridge.TestRunner
{
    /// <summary>
    /// Drives Unity's Test Framework from inside the running editor and hands results back to
    /// the bridge through disk.
    ///
    /// This lives in its own assembly because <c>UnityEditor.TestRunner</c> declares
    /// <c>autoReferenced: false</c> and is gated behind the <c>UNITY_TESTS_FRAMEWORK</c> define,
    /// so the main bridge assembly cannot reference it without breaking projects that do not
    /// have the test framework installed. The accompanying asmdef carries the same define
    /// constraint: with the package absent this assembly is skipped, the handlers below are
    /// never registered, and the bridge reports that cleanly.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class UnifoclTestRunnerAdapter : ICallbacks
    {
        private static readonly UnifoclTestRunnerAdapter Instance = new();

        private static TestRunnerApi? _api;

        /// <summary>
        /// The requestId of the job in flight. Static so it survives for the lifetime of the
        /// domain; a domain reload mid-run is handled by the CLI's poll timeout rather than by
        /// trying to resume, since the run itself does not survive the reload either.
        /// </summary>
        private static string _activeRequestId = string.Empty;

        static UnifoclTestRunnerAdapter()
        {
            // Registered once per domain, never per run: Unity's CallbacksHolder appends without
            // deduplicating, so a per-run registration would leave RunFinished firing once for
            // every run ever started. The holder is reset by a domain reload, and so is this
            // static constructor, which keeps the two in step.
            TestRunnerApi.RegisterTestCallback(Instance);

            DaemonTestRunnerBridge.RunHandler = StartRun;
            DaemonTestRunnerBridge.ListHandler = StartList;
        }

        /// <summary>
        /// One API object per domain. ScriptableObjects are not collected like plain objects, so
        /// creating one per call would leak instances until the next reload.
        /// </summary>
        private static TestRunnerApi Api
        {
            get
            {
                // Unity overloads equality so a destroyed object also compares equal to null,
                // which is the behaviour wanted here. That overload is opaque to the compiler's
                // null-state analysis, hence the suppression on the return.
                if (_api == null)
                {
                    _api = ScriptableObject.CreateInstance<TestRunnerApi>();
                }

                return _api!;
            }
        }

        // ── Handlers registered with the bridge ─────────────────────────────────

        private static string? StartRun(string requestId)
        {
            if (!string.IsNullOrEmpty(_activeRequestId))
            {
                return $"a test job is already running (requestId={_activeRequestId}). "
                     + "If the CLI timed out and left it stranded, trigger a domain reload "
                     + "(recompile any script, or use the Test Runner window) to clear it.";
            }

            try
            {
                _activeRequestId = requestId;
                Api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode }));
                return null;
            }
            catch (Exception ex)
            {
                _activeRequestId = string.Empty;
                return $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        private static string? StartList(string requestId)
        {
            try
            {
                // The callback may fire asynchronously once test assemblies have been scanned,
                // which is why listing goes through the same marker handoff as a run.
                Api.RetrieveTestList(TestMode.EditMode, root =>
                {
                    var entries = new List<TestListEntry>();
                    try
                    {
                        CollectLeafTests(root, entries);
                        DaemonTestRunnerBridge.SaveResult(new TestJobResult
                        {
                            requestId = requestId,
                            kind = DaemonTestRunnerBridge.ListKind,
                            ok = true,
                            tests = entries.ToArray(),
                        });
                    }
                    catch (Exception ex)
                    {
                        SaveFailure(requestId, DaemonTestRunnerBridge.ListKind,
                            $"failed to collect the test list: {ex.GetType().Name}: {ex.Message}");
                    }
                });

                return null;
            }
            catch (Exception ex)
            {
                return $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        // ── ICallbacks ──────────────────────────────────────────────────────────

        public void RunStarted(ITestAdaptor testsToRun)
        {
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            string requestId = _activeRequestId;
            _activeRequestId = string.Empty;

            if (string.IsNullOrEmpty(requestId))
            {
                // A run started from the Test Runner window rather than from unifocl.
                return;
            }

            try
            {
                string xmlPath = DaemonTestRunnerBridge.GetResultsXmlPath();
                Directory.CreateDirectory(Path.GetDirectoryName(xmlPath)!);

                // Emits the same NUnit v3 XML the subprocess runner produced, so the CLI parses
                // one format regardless of which path executed the tests.
                TestRunnerApi.SaveResultToFile(result, xmlPath);

                DaemonTestRunnerBridge.SaveResult(new TestJobResult
                {
                    requestId = requestId,
                    kind = DaemonTestRunnerBridge.RunKind,
                    ok = true,
                    xmlPath = xmlPath,
                });
            }
            catch (Exception ex)
            {
                SaveFailure(requestId, DaemonTestRunnerBridge.RunKind,
                    $"failed to write test results: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void TestStarted(ITestAdaptor test)
        {
        }

        public void TestFinished(ITestResultAdaptor result)
        {
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Walks the test tree and keeps only leaves. Interior nodes are assemblies, namespaces
        /// and fixtures, which are not runnable test cases.
        /// </summary>
        private static void CollectLeafTests(ITestAdaptor node, List<TestListEntry> entries)
        {
            if (node is null)
            {
                return;
            }

            if (!node.HasChildren)
            {
                entries.Add(new TestListEntry
                {
                    testName = node.FullName ?? node.Name ?? string.Empty,
                    assembly = ResolveAssemblyName(node),
                });
                return;
            }

            foreach (var child in node.Children)
            {
                CollectLeafTests(child, entries);
            }
        }

        /// <summary>
        /// Walks up to the nearest ancestor that represents an assembly. Unlike the subprocess
        /// runner, which guessed the assembly from the dotted name prefix, this is exact.
        /// </summary>
        private static string ResolveAssemblyName(ITestAdaptor node)
        {
            for (var current = node; current is not null; current = current.Parent)
            {
                if (!current.IsTestAssembly)
                {
                    continue;
                }

                // Assembly nodes are named after the built file, e.g. "MyTests.dll".
                string name = current.Name ?? string.Empty;
                return string.IsNullOrEmpty(name) ? "Unknown" : Path.GetFileNameWithoutExtension(name);
            }

            return "Unknown";
        }

        private static void SaveFailure(string requestId, string kind, string message)
        {
            Debug.LogError($"[unifocl] {message}");
            DaemonTestRunnerBridge.SaveResult(new TestJobResult
            {
                requestId = requestId,
                kind = kind,
                ok = false,
                message = message,
            });
        }
    }
}
#endif
