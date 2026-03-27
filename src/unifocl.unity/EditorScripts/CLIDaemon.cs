#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UniFocl.SharedModels;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using PMPackageInfo = UnityEditor.PackageManager.PackageInfo;
using Process = System.Diagnostics.Process;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace UniFocl.EditorBridge
{
    [InitializeOnLoad]
    internal static class CLIDaemonInitializeOnLoad
    {
        static CLIDaemonInitializeOnLoad()
        {
            if (Application.isBatchMode)
            {
                return;
            }

            if (CLIDaemon.HasDaemonServiceArg())
            {
                return;
            }

            CLIDaemon.TryStartInitializeOnLoadBridge();
        }
    }

    public static class CLIDaemon
    {
        private const string DefaultMcpGitInstallTarget = "https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#main";
        private static HttpListener? _listener;
        private static CancellationTokenSource? _cts;
        private static Task? _acceptLoopTask;
        private static DateTime _lastActivityUtc;
        private static bool _running;
        private static bool _autoExitEditor;
        private static bool _isBatchMode;
        private static int _inactivityTtlSeconds;
        private static int _mainThreadManagedThreadId;
        private static string _runtimeInstanceId = Guid.NewGuid().ToString("N");
        private static volatile bool _editorIsCompiling;
        private static volatile bool _editorIsUpdating;
        private static readonly object ProjectCommandStatusLock = new();
        private static ProjectCommandStatusResponse _projectCommandStatus = new();
        private static string _projectCommandStageLogSignature = string.Empty;
        private static readonly ConcurrentQueue<MainThreadWorkItem> _mainThreadWorkQueue = new();
        private static readonly ConcurrentQueue<Action> _mainThreadActionQueue = new();
        private static readonly ConcurrentDictionary<long, Task> _requestTasks = new();
        private static long _requestTaskIdSeed;

        public static bool HasDaemonServiceArg()
        {
            var args = Environment.GetCommandLineArgs();
            return Array.IndexOf(args, "--daemon-service") >= 0;
        }

        public static void StartServer()
        {
            var options = ParseServiceArgs(Environment.GetCommandLineArgs());
            StartInternal(options, autoExitEditorOnInactivity: true);

            while (_running)
            {
                DrainMainThreadWorkQueue();
                Thread.Sleep(200);
            }
        }

        public static void InstallRequiredMcpPackageBatch()
        {
            var args = Environment.GetCommandLineArgs();
            var packageId = ResolveCommandArg(args, "--upm-install-package") ?? "com.coplaydev.unity-mcp";
            var fallbackGitTarget = ResolveCommandArg(args, "--upm-install-git-url") ?? DefaultMcpGitInstallTarget;
            var statusPath = ResolveCommandArg(args, "--upm-install-status-file");
            var processId = GetCurrentProcessId();

            WriteUpmInstallStatus(statusPath, new UpmBatchInstallStatus
            {
                pid = processId,
                packageId = packageId,
                stage = "starting",
                success = false,
                message = "initializing unity batch install"
            });

            try
            {
                WriteUpmInstallStatus(statusPath, new UpmBatchInstallStatus
                {
                    pid = processId,
                    packageId = packageId,
                    stage = "installing",
                    success = false,
                    message = "running UPM install"
                });

                var targets = new List<string> { packageId };
                if (!string.IsNullOrWhiteSpace(fallbackGitTarget)
                    && !fallbackGitTarget.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                {
                    targets.Add(fallbackGitTarget);
                }

                var installSucceeded = false;
                var installError = string.Empty;
                foreach (var target in targets)
                {
                    if (!target.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                    {
                        WriteUpmInstallStatus(statusPath, new UpmBatchInstallStatus
                        {
                            pid = processId,
                            packageId = packageId,
                            stage = "installing",
                            success = false,
                            message = $"retrying UPM install with fallback target: {target}"
                        });
                    }

                    var addRequest = Client.Add(target);
                    if (WaitForUpmRequest(addRequest, TimeSpan.FromMinutes(6), out installError))
                    {
                        installSucceeded = true;
                        break;
                    }
                }

                if (!installSucceeded)
                {
                    throw new InvalidOperationException(installError);
                }

                WriteUpmInstallStatus(statusPath, new UpmBatchInstallStatus
                {
                    pid = processId,
                    packageId = packageId,
                    stage = "verifying",
                    success = false,
                    message = "verifying installed package"
                });

                var listRequest = Client.List(true, true);
                if (!WaitForUpmRequest(listRequest, TimeSpan.FromMinutes(2), out var listError))
                {
                    throw new InvalidOperationException(listError);
                }

                var installed = false;
                if (listRequest.Result is not null)
                {
                    foreach (var package in listRequest.Result)
                    {
                        if (package is null)
                        {
                            continue;
                        }

                        if (package.name.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                        {
                            installed = true;
                            break;
                        }
                    }
                }
                if (!installed)
                {
                    throw new InvalidOperationException($"UPM install completed but package is not listed: {packageId}");
                }

                WriteUpmInstallStatus(statusPath, new UpmBatchInstallStatus
                {
                    pid = processId,
                    packageId = packageId,
                    stage = "resolving-dependencies",
                    success = false,
                    message = "installing package.json dependencies recursively"
                });

                var recursiveDeps = InstallDependenciesRecursivelyFromPackageJson(
                    packageId,
                    listRequest.Result,
                    processId,
                    statusPath);
                if (!recursiveDeps.Success)
                {
                    throw new InvalidOperationException(recursiveDeps.Error);
                }

                WriteUpmInstallStatus(statusPath, new UpmBatchInstallStatus
                {
                    pid = processId,
                    packageId = packageId,
                    stage = "completed",
                    success = true,
                    message = $"installed package {packageId}"
                });
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                WriteUpmInstallStatus(statusPath, new UpmBatchInstallStatus
                {
                    pid = processId,
                    packageId = packageId,
                    stage = "failed",
                    success = false,
                    message = $"{ex.GetType().Name}: {ex.Message}",
                    detail = string.Empty
                });
                EditorApplication.Exit(1);
            }
        }

        private static RecursiveDependencyInstallResult InstallDependenciesRecursivelyFromPackageJson(
            string rootPackageId,
            IEnumerable<PMPackageInfo> installedPackages,
            int processId,
            string? statusPath)
        {
            var installedById = new Dictionary<string, PMPackageInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in installedPackages)
            {
                if (package is null || string.IsNullOrWhiteSpace(package.name))
                {
                    continue;
                }

                installedById[package.name] = package;
            }

            var queue = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            queue.Enqueue(rootPackageId);

            while (queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                if (!visited.Add(currentId))
                {
                    continue;
                }

                if (!installedById.TryGetValue(currentId, out var currentPackage)
                    || currentPackage is null
                    || string.IsNullOrWhiteSpace(currentPackage.resolvedPath))
                {
                    continue;
                }

                var deps = ParseDependenciesFromPackageJson(currentPackage.resolvedPath);
                foreach (var dep in deps)
                {
                    if (installedById.ContainsKey(dep.Key))
                    {
                        queue.Enqueue(dep.Key);
                        continue;
                    }

                    var target = ComposeDependencyInstallTarget(dep.Key, dep.Value);
                    WriteUpmInstallStatus(statusPath, new UpmBatchInstallStatus
                    {
                        pid = processId,
                        packageId = rootPackageId,
                        stage = "resolving-dependencies",
                        success = false,
                        message = $"installing dependency {dep.Key} via target {target}"
                    });

                    var addRequest = Client.Add(target);
                    if (!WaitForUpmRequest(addRequest, TimeSpan.FromMinutes(4), out var depInstallError))
                    {
                        return RecursiveDependencyInstallResult.Fail($"failed to install dependency {dep.Key}: {depInstallError}");
                    }

                    var refreshRequest = Client.List(true, true);
                    if (!WaitForUpmRequest(refreshRequest, TimeSpan.FromMinutes(2), out var refreshError))
                    {
                        return RecursiveDependencyInstallResult.Fail($"failed to refresh package list after dependency install: {refreshError}");
                    }

                    installedById.Clear();
                    foreach (var pkg in refreshRequest.Result)
                    {
                        if (pkg is null || string.IsNullOrWhiteSpace(pkg.name))
                        {
                            continue;
                        }

                        installedById[pkg.name] = pkg;
                    }

                    if (!installedById.ContainsKey(dep.Key))
                    {
                        return RecursiveDependencyInstallResult.Fail($"dependency install completed but package is not registered: {dep.Key}");
                    }

                    queue.Enqueue(dep.Key);
                }
            }

            return RecursiveDependencyInstallResult.Ok();
        }

        private static Dictionary<string, string> ParseDependenciesFromPackageJson(string packageRootPath)
        {
            var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(packageRootPath))
            {
                return dependencies;
            }

            var packageJsonPath = Path.Combine(packageRootPath, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                return dependencies;
            }

            string raw;
            try
            {
                raw = File.ReadAllText(packageJsonPath);
            }
            catch
            {
                return dependencies;
            }

            var dependenciesMatch = Regex.Match(
                raw,
                "\"dependencies\"\\s*:\\s*\\{(?<body>.*?)\\}",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!dependenciesMatch.Success)
            {
                return dependencies;
            }

            var body = dependenciesMatch.Groups["body"].Value;
            var entryMatches = Regex.Matches(
                body,
                "\"(?<id>[^\"]+)\"\\s*:\\s*\"(?<value>[^\"]+)\"",
                RegexOptions.Singleline);

            foreach (Match entry in entryMatches)
            {
                if (!entry.Success)
                {
                    continue;
                }

                var id = entry.Groups["id"].Value.Trim();
                var value = entry.Groups["value"].Value.Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                dependencies[id] = value;
            }

            return dependencies;
        }

        private static string ComposeDependencyInstallTarget(string packageId, string versionSpec)
        {
            var normalizedPackageId = packageId?.Trim() ?? string.Empty;
            var normalizedVersionSpec = versionSpec?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedVersionSpec))
            {
                return normalizedPackageId;
            }

            if (normalizedVersionSpec.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || normalizedVersionSpec.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || normalizedVersionSpec.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
                || normalizedVersionSpec.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
                || normalizedVersionSpec.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedVersionSpec;
            }

            return $"{normalizedPackageId}@{normalizedVersionSpec}";
        }

        public static void TryStartInitializeOnLoadBridge()
        {
            if (_running)
            {
                return;
            }

            var options = LoadBridgeOptionsFromProject();
            StartInternal(options, autoExitEditorOnInactivity: false);
        }

        private static void StartInternal(DaemonServiceArgs options, bool autoExitEditorOnInactivity)
        {
            if (_running)
            {
                return;
            }

            _autoExitEditor = autoExitEditorOnInactivity;
            _isBatchMode = Application.isBatchMode;
            _inactivityTtlSeconds = Math.Max(1, options.ttlSeconds);
            _lastActivityUtc = DateTime.UtcNow;
            _mainThreadManagedThreadId = Environment.CurrentManagedThreadId;
            _runtimeInstanceId = Guid.NewGuid().ToString("N");
            RefreshEditorStateCache();

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{options.port}/");
            _listener.Start();
            _running = true;

            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.quitting += OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);

            var dispatcherMode = "update-queue";
            Debug.Log($"[unifocl] CLIDaemon started on http://127.0.0.1:{options.port}/ (batch={_isBatchMode}, autoExit={_autoExitEditor}, dispatcher={dispatcherMode})");
        }

        private static async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            if (_listener is null)
            {
                return;
            }

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }

                    var requestTask = Task.Run(() => HandleRequestAsync(context, cancellationToken), cancellationToken);
                    TrackRequestTask(requestTask);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (HttpListenerException)
            {
            }
        }

        private static async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var request = context.Request;
            var path = (request.Url?.AbsolutePath ?? "/").TrimEnd('/');
            if (path.Length == 0)
            {
                path = "/";
            }

            try
            {
                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/ping", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextResponseAsync(context.Response, "PONG", cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/runtime-id", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextResponseAsync(context.Response, _runtimeInstanceId, cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/project/command-status", StringComparison.OrdinalIgnoreCase))
                {
                    var requestedId = request.QueryString["requestId"];
                    if (!string.IsNullOrWhiteSpace(requestedId)
                        && DaemonProjectService.TryGetDurableMutationStatus(requestedId, out var durableStatus))
                    {
                        durableStatus.isCompiling = _editorIsCompiling;
                        durableStatus.isUpdating = _editorIsUpdating;
                        await WriteJsonResponseAsync(context.Response, JsonUtility.ToJson(durableStatus), cancellationToken: cancellationToken);
                        return;
                    }

                    await WriteJsonResponseAsync(context.Response, GetProjectCommandStatusPayload(requestedId), cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/touch", StringComparison.OrdinalIgnoreCase))
                {
                    MarkActivity();
                    await WriteTextResponseAsync(context.Response, "OK", cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/stop", StringComparison.OrdinalIgnoreCase))
                {
                    var shutdownMode = _isBatchMode ? "host-session shutdown" : "bridge-session detach";
                    Debug.Log($"[unifocl] daemon /stop requested from CLI ({shutdownMode}); stopping daemon listener.");
                    await WriteTextResponseAsync(context.Response, "STOPPING", cancellationToken: cancellationToken);
                    if (_isBatchMode
                        && (_mainThreadManagedThreadId == 0 || Environment.CurrentManagedThreadId != _mainThreadManagedThreadId))
                    {
                        _ = await ExecuteOnMainThreadAsync(() =>
                        {
                            StopInternal(quitEditor: true);
                            return "OK";
                        });
                    }
                    else
                    {
                        StopInternal(quitEditor: _isBatchMode);
                        if (!_isBatchMode)
                        {
                            // Bridge mode lives inside Unity GUI; restart listener so future /open can reattach without restarting the editor.
                            EditorApplication.delayCall += RestartBridgeModeListenerAfterDetach;
                        }
                    }
                    return;
                }

                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/asset-index", StringComparison.OrdinalIgnoreCase))
                {
                    var revisionRaw = request.QueryString["revision"];
                    int? knownRevision = null;
                    if (int.TryParse(revisionRaw, out var revision) && revision > 0)
                    {
                        knownRevision = revision;
                    }

                    MarkActivity();
                    await WriteJsonResponseAsync(context.Response, DaemonAssetIndexService.BuildPayload(knownRevision), cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/hierarchy/snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    MarkActivity();
                    var response = await ExecuteOnMainThreadAsync(DaemonHierarchyService.BuildSnapshotPayload);
                    await WriteJsonResponseAsync(context.Response, response, cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/hierarchy/command", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = await ReadRequestBodyAsync(request, cancellationToken);
                    MarkActivity();
                    var response = await ExecuteOnMainThreadAsync(() => DaemonHierarchyService.ExecuteCommand(payload));
                    await WriteJsonResponseAsync(context.Response, response, cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/hierarchy/find", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = await ReadRequestBodyAsync(request, cancellationToken);
                    MarkActivity();
                    var response = await ExecuteOnMainThreadAsync(() => DaemonHierarchyService.ExecuteSearch(payload));
                    await WriteJsonResponseAsync(context.Response, response, cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/inspect", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = await ReadRequestBodyAsync(request, cancellationToken);
                    MarkActivity();
                    var response = await ExecuteOnMainThreadAsync(() => DaemonInspectorService.Execute(payload));
                    await WriteJsonResponseAsync(context.Response, response, cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/project/command", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = await ReadRequestBodyAsync(request, cancellationToken);
                    MarkActivity();
                    var action = TryExtractProjectAction(payload);
                    var requestId = TryExtractProjectRequestId(payload);
                    var stopwatch = Stopwatch.StartNew();
                    Debug.Log($"[unifocl] project command received: action={action}");
                    BeginProjectCommand(action, requestId);
                    var response = await ExecuteOnMainThreadAsync(() => DaemonProjectService.ExecuteAsync(payload));
                    stopwatch.Stop();
                    CompleteProjectCommandFromResponse(response);
                    Debug.Log($"[unifocl] project command completed: action={action}, elapsedMs={stopwatch.ElapsedMilliseconds}");
                    await WriteJsonResponseAsync(context.Response, response, cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/project/mutation/submit", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = await ReadRequestBodyAsync(request, cancellationToken);
                    MarkActivity();
                    var response = await ExecuteOnMainThreadAsync(() => DaemonProjectService.SubmitMutationPayload(payload));
                    await WriteJsonResponseAsync(context.Response, response, cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/project/mutation/status", StringComparison.OrdinalIgnoreCase))
                {
                    var requestId = request.QueryString["requestId"] ?? string.Empty;
                    MarkActivity();
                    var response = await ExecuteOnMainThreadAsync(() => DaemonProjectService.GetMutationStatusPayload(requestId));
                    await WriteJsonResponseAsync(context.Response, response, cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/project/mutation/result", StringComparison.OrdinalIgnoreCase))
                {
                    var requestId = request.QueryString["requestId"] ?? string.Empty;
                    MarkActivity();
                    var response = await ExecuteOnMainThreadAsync(() => DaemonProjectService.GetMutationResultPayload(requestId));
                    await WriteJsonResponseAsync(context.Response, response, cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/project/mutation/cancel", StringComparison.OrdinalIgnoreCase))
                {
                    var requestId = request.QueryString["requestId"] ?? string.Empty;
                    MarkActivity();
                    var response = await ExecuteOnMainThreadAsync(() => DaemonProjectService.CancelMutationPayload(requestId));
                    await WriteJsonResponseAsync(context.Response, response, cancellationToken: cancellationToken);
                    return;
                }

                // Thin custom tool adapter endpoint used by MCP clients/tools.
                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/mcp/unifocl_project_command", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = await ReadRequestBodyAsync(request, cancellationToken);
                    MarkActivity();
                    var response = await ExecuteOnMainThreadAsync(() => HandleUnifoclMcpToolPayload(payload));
                    await WriteJsonResponseAsync(context.Response, response, cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/build/status", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonResponseAsync(context.Response, DaemonProjectService.GetBuildStatusPayload(), cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/compile/status", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonResponseAsync(context.Response, DaemonProjectService.GetCompilationStatusPayload(), cancellationToken: cancellationToken);
                    return;
                }

                if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/build/log", StringComparison.OrdinalIgnoreCase))
                {
                    var offsetRaw = request.QueryString["offset"];
                    var limitRaw = request.QueryString["limit"];
                    var errorsOnlyRaw = request.QueryString["errorsOnly"];
                    _ = long.TryParse(offsetRaw, out var offset);
                    _ = int.TryParse(limitRaw, out var limit);
                    _ = bool.TryParse(errorsOnlyRaw, out var errorsOnly);
                    await WriteJsonResponseAsync(context.Response, DaemonProjectService.ReadBuildLogPayload(offset, limit, errorsOnly), cancellationToken: cancellationToken);
                    return;
                }

                // ExecV2 structured command adapter
                if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/agent/exec", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = await ReadRequestBodyAsync(request, cancellationToken);
                    var response = await HandleExecV2Async(payload, cancellationToken);
                    await WriteJsonResponseAsync(context.Response, response, cancellationToken: cancellationToken);
                    return;
                }

                MarkActivity();
                await WriteTextResponseAsync(context.Response, "ERR", 404, cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (IsClientDisconnectException(ex))
                {
                    // The CLI side may timeout/cancel and close the socket while the editor is finishing work.
                    // Treat this as benign transport churn rather than a daemon runtime failure.
                    Debug.Log($"[unifocl] client disconnected before response write: {request.HttpMethod} {path} ({ex.GetType().Name})");
                    return;
                }

                Debug.LogError($"[unifocl] request failed: {request.HttpMethod} {path} -> {ex}");
                MarkProjectCommandFailed($"request exception: {ex.GetType().Name}: {ex.Message}");
                try
                {
                    var detail = $"ERR: {ex.GetType().Name}: {ex.Message}";
                    await WriteTextResponseAsync(context.Response, detail, 500, cancellationToken: cancellationToken);
                }
                catch
                {
                }
            }
        }

        private static string TryExtractProjectAction(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return "<empty>";
            }

            const string marker = "\"action\"";
            var actionIndex = payload.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (actionIndex < 0)
            {
                return "<missing>";
            }

            var colonIndex = payload.IndexOf(':', actionIndex + marker.Length);
            if (colonIndex < 0)
            {
                return "<invalid>";
            }

            var valueStart = payload.IndexOf('"', colonIndex + 1);
            if (valueStart < 0)
            {
                return "<invalid>";
            }

            var valueEnd = payload.IndexOf('"', valueStart + 1);
            if (valueEnd <= valueStart)
            {
                return "<invalid>";
            }

            return payload[(valueStart + 1)..valueEnd];
        }

        private static string HandleUnifoclMcpToolPayload(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "mcp tool payload is required",
                    kind = "mcp"
                });
            }

            McpProjectCommandToolRequest? request;
            try
            {
                request = JsonUtility.FromJson<McpProjectCommandToolRequest>(payload);
            }
            catch
            {
                request = null;
            }

            if (request is null || string.IsNullOrWhiteSpace(request.operation))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "mcp tool operation is required",
                    kind = "mcp"
                });
            }

            var operation = request.operation.Trim().ToLowerInvariant();
            return operation switch
            {
                "submit" => string.IsNullOrWhiteSpace(request.commandPayload)
                    ? JsonUtility.ToJson(new ProjectCommandAcceptedResponse
                    {
                        ok = false,
                        stage = "error",
                        message = "submit requires commandPayload"
                    })
                    : DaemonProjectService.SubmitMutationPayload(request.commandPayload),
                "get_status" => string.IsNullOrWhiteSpace(request.requestId)
                    ? JsonUtility.ToJson(new ProjectCommandStatusResponse
                    {
                        requestId = string.Empty,
                        active = false,
                        stage = "error",
                        detail = "get_status requires requestId",
                        state = "error",
                        isDurable = true
                    })
                    : DaemonProjectService.GetMutationStatusPayload(request.requestId),
                "get_result" => string.IsNullOrWhiteSpace(request.requestId)
                    ? JsonUtility.ToJson(new ProjectCommandResultResponse
                    {
                        found = false,
                        completed = false,
                        success = false,
                        requestId = string.Empty,
                        action = string.Empty,
                        state = "error",
                        message = "get_result requires requestId"
                    })
                    : DaemonProjectService.GetMutationResultPayload(request.requestId),
                "cancel" => string.IsNullOrWhiteSpace(request.requestId)
                    ? JsonUtility.ToJson(new ProjectCommandAcceptedResponse
                    {
                        ok = false,
                        stage = "error",
                        message = "cancel requires requestId"
                    })
                    : DaemonProjectService.CancelMutationPayload(request.requestId),
                "execute_custom_tool" => string.IsNullOrWhiteSpace(request.tool)
                    ? JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "execute_custom_tool requires tool name",
                        kind = "mcp"
                    })
                    : DaemonCustomToolService.ExecuteCustomTool(request.tool, request.args, request.dryRun),
                _ => JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"unsupported mcp tool operation: {request.operation}",
                    kind = "mcp"
                })
            };
        }

        private static async Task<string> HandleExecV2Async(string payload, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return ExecV2ErrorJson("request body is required", string.Empty);
            }

            ExecV2AdapterRequest? req;
            try
            {
                req = JsonUtility.FromJson<ExecV2AdapterRequest>(payload);
            }
            catch
            {
                req = null;
            }

            if (req is null || string.IsNullOrWhiteSpace(req.operation))
            {
                return ExecV2ErrorJson("operation is required", string.Empty);
            }

            var requestId = req.requestId ?? string.Empty;
            var op = req.operation.Trim().ToLowerInvariant();

            string projectPayload;
            switch (op)
            {
                case "asset.rename":
                {
                    if (string.IsNullOrWhiteSpace(req.args.assetPath) || string.IsNullOrWhiteSpace(req.args.newAssetPath))
                    {
                        return ExecV2ErrorJson("asset.rename requires args.assetPath and args.newAssetPath", requestId);
                    }

                    projectPayload = BuildExecV2ProjectPayload("rename-asset", req.args.assetPath, req.args.newAssetPath, null, requestId);
                    break;
                }

                case "asset.remove":
                {
                    if (string.IsNullOrWhiteSpace(req.args.assetPath))
                    {
                        return ExecV2ErrorJson("asset.remove requires args.assetPath", requestId);
                    }

                    projectPayload = BuildExecV2ProjectPayload("remove-asset", req.args.assetPath, null, null, requestId);
                    break;
                }

                case "asset.create_script":
                {
                    if (string.IsNullOrWhiteSpace(req.args.assetPath))
                    {
                        return ExecV2ErrorJson("asset.create_script requires args.assetPath", requestId);
                    }

                    projectPayload = BuildExecV2ProjectPayload("mk-script", req.args.assetPath, null, req.args.content, requestId);
                    break;
                }

                case "asset.create":
                {
                    if (string.IsNullOrWhiteSpace(req.args.assetPath))
                    {
                        return ExecV2ErrorJson("asset.create requires args.assetPath", requestId);
                    }

                    projectPayload = BuildExecV2ProjectPayload("mk-asset", req.args.assetPath, null, req.args.content, requestId);
                    break;
                }

                case "build.run":
                {
                    projectPayload = BuildExecV2ProjectPayload("build-run", null, null, null, requestId);
                    break;
                }

                case "build.exec":
                {
                    if (string.IsNullOrWhiteSpace(req.args.method))
                    {
                        return ExecV2ErrorJson("build.exec requires args.method", requestId);
                    }

                    var methodContent = $"{{\"method\":\"{EscapeJsonString(req.args.method)}\"}}";
                    projectPayload = BuildExecV2ProjectPayload("build-exec", null, null, methodContent, requestId);
                    break;
                }

                case "build.scenes.set":
                {
                    if (req.args.scenes == null || req.args.scenes.Length == 0)
                    {
                        return ExecV2ErrorJson("build.scenes.set requires args.scenes (array of scene paths)", requestId);
                    }

                    var sceneParts = new string[req.args.scenes.Length];
                    for (var i = 0; i < req.args.scenes.Length; i++)
                    {
                        sceneParts[i] = $"\"{EscapeJsonString(req.args.scenes[i])}\"";
                    }

                    var scenesContent = $"{{\"scenes\":[{string.Join(",", sceneParts)}]}}";
                    projectPayload = BuildExecV2ProjectPayload("build-scenes-set", null, null, scenesContent, requestId);
                    break;
                }

                case "upm.remove":
                {
                    if (string.IsNullOrWhiteSpace(req.args.packageId))
                    {
                        return ExecV2ErrorJson("upm.remove requires args.packageId", requestId);
                    }

                    var upmContent = $"{{\"packageId\":\"{EscapeJsonString(req.args.packageId)}\"}}";
                    projectPayload = BuildExecV2ProjectPayload("upm-remove", null, null, upmContent, requestId);
                    break;
                }

                default:
                    return ExecV2ErrorJson($"unknown operation: {req.operation}", requestId);
            }

            MarkActivity();
            Debug.Log($"[unifocl] /agent/exec: operation={op} requestId={requestId}");
            var resultJson = await ExecuteOnMainThreadAsync(() => DaemonProjectService.ExecuteAsync(projectPayload));
            return $"{{\"status\":\"Completed\",\"requestId\":\"{EscapeJsonString(requestId)}\",\"result\":{resultJson}}}";
        }

        private static string BuildExecV2ProjectPayload(
            string action,
            string? assetPath,
            string? newAssetPath,
            string? content,
            string requestId)
        {
            var r = new ProjectCommandRequest
            {
                action = action,
                assetPath = assetPath ?? string.Empty,
                newAssetPath = newAssetPath ?? string.Empty,
                content = content ?? string.Empty,
                requestId = requestId,
            };
            return JsonUtility.ToJson(r);
        }

        private static string ExecV2ErrorJson(string error, string requestId)
            => $"{{\"status\":\"Failed\",\"requestId\":\"{EscapeJsonString(requestId)}\",\"error\":\"{EscapeJsonString(error)}\"}}";

        private static string EscapeJsonString(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        private static string? TryExtractProjectRequestId(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            const string marker = "\"requestId\"";
            var actionIndex = payload.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (actionIndex < 0)
            {
                return null;
            }

            var colonIndex = payload.IndexOf(':', actionIndex + marker.Length);
            if (colonIndex < 0)
            {
                return null;
            }

            var valueStart = payload.IndexOf('"', colonIndex + 1);
            if (valueStart < 0)
            {
                return null;
            }

            var valueEnd = payload.IndexOf('"', valueStart + 1);
            if (valueEnd <= valueStart)
            {
                return null;
            }

            var requestId = payload[(valueStart + 1)..valueEnd];
            return string.IsNullOrWhiteSpace(requestId) ? null : requestId;
        }

        private static async Task<string> ReadRequestBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            var payload = await reader.ReadToEndAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return payload;
        }

        private static async Task WriteTextResponseAsync(
            HttpListenerResponse response,
            string payload,
            int statusCode = 200,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(payload + Environment.NewLine);
                response.StatusCode = statusCode;
                response.ContentType = "text/plain; charset=utf-8";
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            finally
            {
                try
                {
                    response.Close();
                }
                catch
                {
                }
            }
        }

        private static async Task WriteJsonResponseAsync(
            HttpListenerResponse response,
            string payload,
            int statusCode = 200,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(payload + Environment.NewLine);
                response.StatusCode = statusCode;
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (IsClientDisconnectException(ex))
            {
                // Ignore write failures when peer already closed/cancelled the request.
            }
            finally
            {
                try
                {
                    response.Close();
                }
                catch
                {
                }
            }
        }

        private static bool IsClientDisconnectException(Exception ex)
        {
            if (ex is ObjectDisposedException)
            {
                return true;
            }

            if (ex is HttpListenerException)
            {
                return true;
            }

            if (ex is IOException ioEx && ioEx.InnerException is System.Net.Sockets.SocketException socketEx)
            {
                return socketEx.SocketErrorCode is System.Net.Sockets.SocketError.Shutdown
                    or System.Net.Sockets.SocketError.ConnectionAborted
                    or System.Net.Sockets.SocketError.ConnectionReset
                    or System.Net.Sockets.SocketError.NotConnected
                    or System.Net.Sockets.SocketError.OperationAborted;
            }

            return false;
        }

        private static void MarkActivity()
        {
            _lastActivityUtc = DateTime.UtcNow;
        }

        private static void OnEditorUpdate()
        {
            RefreshEditorStateCache();

            if (!_running)
            {
                return;
            }

            // Always service bridge/main-thread work while daemon is running.
            DrainMainThreadWorkQueue();

            if (!_autoExitEditor)
            {
                return;
            }

            if (DateTime.UtcNow - _lastActivityUtc < TimeSpan.FromSeconds(_inactivityTtlSeconds))
            {
                return;
            }

            Debug.Log("[unifocl] CLIDaemon inactivity TTL reached; exiting editor process.");
            StopInternal(quitEditor: true);
        }

        private static void RefreshEditorStateCache()
        {
            _editorIsCompiling = EditorApplication.isCompiling;
            _editorIsUpdating = EditorApplication.isUpdating;
        }

        private static void StopInternal(bool quitEditor)
        {
            if (!_running)
            {
                return;
            }

            var wasBatchMode = _isBatchMode;
            var isMainThread = _mainThreadManagedThreadId != 0
                && Environment.CurrentManagedThreadId == _mainThreadManagedThreadId;

            _running = false;
            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }

            try
            {
                _listener?.Stop();
            }
            catch
            {
            }

            TryDrainBackgroundDaemonTasks();

            _cts?.Dispose();
            _cts = null;
            _listener = null;

            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.quitting -= OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;

            FailPendingMainThreadWork();

            if (quitEditor)
            {
                RequestEditorExit(wasBatchMode, isMainThread);
            }

            _isBatchMode = false;
            _mainThreadManagedThreadId = 0;
        }

        private static void TrackRequestTask(Task requestTask)
        {
            var id = Interlocked.Increment(ref _requestTaskIdSeed);
            _requestTasks[id] = requestTask;
            _ = requestTask.ContinueWith(task =>
            {
                _requestTasks.TryRemove(id, out _);
                if (task.IsFaulted && task.Exception is not null)
                {
                    Debug.LogError($"[unifocl] request task failed: {task.Exception.GetBaseException().Message}");
                }
            }, TaskScheduler.Default);
        }

        private static void TryDrainBackgroundDaemonTasks()
        {
            var acceptLoopTask = _acceptLoopTask;
            var requestTasks = new List<Task>(_requestTasks.Count);
            foreach (var requestTask in _requestTasks.Values)
            {
                requestTasks.Add(requestTask);
            }

            if (acceptLoopTask is null && requestTasks.Count == 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var tasks = new List<Task>(requestTasks.Count + (acceptLoopTask is null ? 0 : 1));
                    if (acceptLoopTask is not null)
                    {
                        tasks.Add(acceptLoopTask);
                    }

                    foreach (var requestTask in requestTasks)
                    {
                        tasks.Add(requestTask);
                    }

                    if (tasks.Count == 0)
                    {
                        return;
                    }

                    var drain = Task.WhenAll(tasks);
                    var completed = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                    if (completed != drain)
                    {
                        Debug.LogWarning($"[unifocl] daemon shutdown timed out while draining {tasks.Count} background task(s)");
                    }
                }
                catch
                {
                }
            });
        }

        private static void OnEditorQuitting()
        {
            if (!_running)
            {
                return;
            }

            Debug.Log("[unifocl] editor quitting detected; shutting down daemon listener and releasing port.");
            StopInternal(quitEditor: false);
        }

        private static void OnBeforeAssemblyReload()
        {
            if (!_running)
            {
                return;
            }

            Debug.Log("[unifocl] domain reload detected; shutting down daemon listener before reload.");
            StopInternal(quitEditor: false);
        }

        private static void RestartBridgeModeListenerAfterDetach()
        {
            try
            {
                if (_running || Application.isBatchMode || HasDaemonServiceArg())
                {
                    return;
                }

                Debug.Log("[unifocl] restarting bridge listener after detach request");
                TryStartInitializeOnLoadBridge();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[unifocl] failed to restart bridge listener after detach: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RequestEditorExit(bool wasBatchMode, bool isMainThread)
        {
            if (isMainThread)
            {
                EditorApplication.Exit(0);
                return;
            }

            if (wasBatchMode)
            {
                Environment.Exit(0);
                return;
            }

            EditorApplication.delayCall += () => EditorApplication.Exit(0);
        }

        private static Task<string> ExecuteOnMainThreadAsync(Func<string> work)
        {
            if (_mainThreadManagedThreadId != 0
                && Environment.CurrentManagedThreadId == _mainThreadManagedThreadId)
            {
                try
                {
                    return Task.FromResult(work());
                }
                catch (Exception ex)
                {
                    return Task.FromException<string>(ex);
                }
            }

            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainThreadWorkQueue.Enqueue(new MainThreadWorkItem(work, completion));
            return completion.Task;
        }

        private static Task<string> ExecuteOnMainThreadAsync(Func<Task<string>> workAsync)
        {
            if (_mainThreadManagedThreadId != 0
                && Environment.CurrentManagedThreadId == _mainThreadManagedThreadId)
            {
                try
                {
                    return workAsync();
                }
                catch (Exception ex)
                {
                    return Task.FromException<string>(ex);
                }
            }

            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainThreadActionQueue.Enqueue(() =>
            {
                _ = ExecuteAsyncWork();
            });
            return completion.Task;

            async Task ExecuteAsyncWork()
            {
                try
                {
                    completion.TrySetResult(await workAsync().ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }
        }

        private static void DrainMainThreadWorkQueue()
        {
            while (_mainThreadActionQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[unifocl] main-thread action failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            while (_mainThreadWorkQueue.TryDequeue(out var item))
            {
                try
                {
                    item.Completion.TrySetResult(item.Work());
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
            }
        }

        private static void FailPendingMainThreadWork()
        {
            var ex = new InvalidOperationException("CLI daemon stopped before main-thread work could execute");
            while (_mainThreadActionQueue.TryDequeue(out _))
            {
            }

            while (_mainThreadWorkQueue.TryDequeue(out var item))
            {
                item.Completion.TrySetException(ex);
            }
        }

        internal static void DispatchOnMainThread(Action action)
        {
            if (action is null)
            {
                return;
            }

            var isMainThread = _mainThreadManagedThreadId != 0
                && Environment.CurrentManagedThreadId == _mainThreadManagedThreadId;
            if (isMainThread)
            {
                action();
                return;
            }

            if (_isBatchMode)
            {
                _mainThreadActionQueue.Enqueue(action);
                return;
            }

            EditorApplication.delayCall += () =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[unifocl] delayed main-thread action failed: {ex.GetType().Name}: {ex.Message}");
                }
            };
        }

        internal static void UpdateProjectCommandStage(string stage, string detail = "")
        {
            lock (ProjectCommandStatusLock)
            {
                _projectCommandStatus.stage = string.IsNullOrWhiteSpace(stage) ? _projectCommandStatus.stage : stage;
                _projectCommandStatus.detail = detail ?? string.Empty;
                _projectCommandStatus.lastUpdatedAtUtc = DateTime.UtcNow.ToString("O");
                _projectCommandStatus.isCompiling = _editorIsCompiling;
                _projectCommandStatus.isUpdating = _editorIsUpdating;
                var signature =
                    $"{_projectCommandStatus.requestId}|{_projectCommandStatus.stage}|{_projectCommandStatus.detail}|compiling={_projectCommandStatus.isCompiling}|updating={_projectCommandStatus.isUpdating}";
                if (string.IsNullOrWhiteSpace(_projectCommandStatus.requestId)
                    || _projectCommandStatus.requestId.Equals("not-found", StringComparison.Ordinal)
                    || signature.Equals(_projectCommandStageLogSignature, StringComparison.Ordinal))
                {
                    return;
                }

                _projectCommandStageLogSignature = signature;
                Debug.Log(
                    $"[unifocl] project command stage: requestId={_projectCommandStatus.requestId}, action={_projectCommandStatus.action}, stage={_projectCommandStatus.stage}, detail={_projectCommandStatus.detail}, compiling={_projectCommandStatus.isCompiling}, updating={_projectCommandStatus.isUpdating}");
            }
        }

        private static void BeginProjectCommand(string action, string? requestId)
        {
            lock (ProjectCommandStatusLock)
            {
                var now = DateTime.UtcNow.ToString("O");
                _projectCommandStatus = new ProjectCommandStatusResponse
                {
                    requestId = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId!,
                    action = action,
                    active = true,
                    success = false,
                    stage = "received",
                    detail = string.Empty,
                    startedAtUtc = now,
                    lastUpdatedAtUtc = now,
                    finishedAtUtc = string.Empty,
                    isCompiling = _editorIsCompiling,
                    isUpdating = _editorIsUpdating
                };
                _projectCommandStageLogSignature = string.Empty;
            }
        }

        private static void CompleteProjectCommandFromResponse(string responsePayload)
        {
            var ok = false;
            var detail = "project command completed";
            try
            {
                var parsed = JsonUtility.FromJson<ProjectCommandResponse>(responsePayload);
                if (parsed is not null)
                {
                    ok = parsed.ok;
                    if (!string.IsNullOrWhiteSpace(parsed.message))
                    {
                        detail = parsed.message;
                    }
                }
            }
            catch
            {
            }

            CompleteProjectCommand(ok, detail);
        }

        private static void MarkProjectCommandFailed(string detail)
        {
            lock (ProjectCommandStatusLock)
            {
                if (!_projectCommandStatus.active)
                {
                    return;
                }
            }

            CompleteProjectCommand(false, detail);
        }

        private static void CompleteProjectCommand(bool success, string detail)
        {
            lock (ProjectCommandStatusLock)
            {
                _projectCommandStatus.active = false;
                _projectCommandStatus.success = success;
                _projectCommandStatus.stage = success ? "completed" : "failed";
                _projectCommandStatus.detail = detail ?? string.Empty;
                _projectCommandStatus.lastUpdatedAtUtc = DateTime.UtcNow.ToString("O");
                _projectCommandStatus.finishedAtUtc = _projectCommandStatus.lastUpdatedAtUtc;
                _projectCommandStatus.isCompiling = _editorIsCompiling;
                _projectCommandStatus.isUpdating = _editorIsUpdating;
            }
        }

        private static string GetProjectCommandStatusPayload(string? requestedId)
        {
            lock (ProjectCommandStatusLock)
            {
                if (!string.IsNullOrWhiteSpace(requestedId)
                    && !_projectCommandStatus.requestId.Equals(requestedId, StringComparison.Ordinal))
                {
                    return JsonUtility.ToJson(new ProjectCommandStatusResponse
                    {
                        requestId = requestedId,
                        action = string.Empty,
                        active = false,
                        success = false,
                        stage = "not-found",
                        detail = "request id not found in current daemon runtime",
                        startedAtUtc = string.Empty,
                        lastUpdatedAtUtc = DateTime.UtcNow.ToString("O"),
                        finishedAtUtc = string.Empty,
                        isCompiling = _editorIsCompiling,
                        isUpdating = _editorIsUpdating
                    });
                }

                return JsonUtility.ToJson(_projectCommandStatus);
            }
        }

        internal static void MarkAssetIndexDirty() => DaemonAssetIndexService.MarkDirty();

        private static DaemonServiceArgs ParseServiceArgs(string[] args)
        {
            var parsed = new DaemonServiceArgs();
            parsed.headless = true;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--port":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var port))
                        {
                            parsed.port = port;
                            i++;
                        }
                        break;
                    case "--project":
                        if (i + 1 < args.Length)
                        {
                            parsed.projectPath = args[i + 1];
                            i++;
                        }
                        break;
                    case "--ttl-seconds":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var ttl))
                        {
                            parsed.ttlSeconds = Math.Max(1, ttl);
                            i++;
                        }
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(parsed.projectPath))
            {
                parsed.projectPath = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            }

            return parsed;
        }

        private static string? ResolveCommandArg(string[] args, string key)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].Equals(key, StringComparison.Ordinal))
                {
                    continue;
                }

                if (i + 1 < args.Length)
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        private static int GetCurrentProcessId()
        {
            try
            {
                return Process.GetCurrentProcess().Id;
            }
            catch
            {
                return 0;
            }
        }

        private static void WriteUpmInstallStatus(string? path, UpmBatchInstallStatus payload)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, JsonUtility.ToJson(payload));
            }
            catch
            {
            }
        }

        private static bool WaitForUpmRequest(Request request, TimeSpan timeout, out string error)
        {
            var startedAt = DateTime.UtcNow;
            while (!request.IsCompleted)
            {
                if (DateTime.UtcNow - startedAt > timeout)
                {
                    error = $"UPM request timed out after {(int)timeout.TotalSeconds} seconds";
                    return false;
                }

                Thread.Sleep(200);
            }

            if (request.Status != StatusCode.Success)
            {
                var detail = request.Error is null
                    ? "unknown package manager error"
                    : $"{request.Error.errorCode}: {request.Error.message}";
                error = $"UPM request failed: {detail}";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static DaemonServiceArgs LoadBridgeOptionsFromProject()
        {
            var projectPath = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            var bridgePath = Path.Combine(projectPath, ".unifocl", "bridge.json");
            var parsed = new DaemonServiceArgs
            {
                projectPath = projectPath,
                ttlSeconds = 600,
                headless = false
            };

            if (!File.Exists(bridgePath))
            {
                return parsed;
            }

            try
            {
                var json = File.ReadAllText(bridgePath);
                var bridge = JsonUtility.FromJson<BridgeConfig>(json);
                if (bridge?.daemon is not null && bridge.daemon.port > 0)
                {
                    parsed.port = bridge.daemon.port;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] Failed to parse bridge config at {bridgePath}: {ex.Message}");
            }

            return parsed;
        }

        private readonly struct MainThreadWorkItem
        {
            public MainThreadWorkItem(Func<string> work, TaskCompletionSource<string> completion)
            {
                Work = work;
                Completion = completion;
            }

            public Func<string> Work { get; }
            public TaskCompletionSource<string> Completion { get; }
        }

        [Serializable]
        private sealed class McpProjectCommandToolRequest
        {
            public string operation = string.Empty;
            public string requestId = string.Empty;
            public string commandPayload = string.Empty;
            public string tool = string.Empty;
            public string args = string.Empty;
            public bool dryRun;
        }

        private sealed class UpmBatchInstallStatus
        {
            public int pid;
            public string packageId = string.Empty;
            public string stage = string.Empty;
            public bool success;
            public string message = string.Empty;
            public string detail = string.Empty;
        }

        private readonly struct RecursiveDependencyInstallResult
        {
            private RecursiveDependencyInstallResult(bool success, string error)
            {
                Success = success;
                Error = error;
            }

            public bool Success { get; }
            public string Error { get; }

            public static RecursiveDependencyInstallResult Ok()
            {
                return new RecursiveDependencyInstallResult(true, string.Empty);
            }

            public static RecursiveDependencyInstallResult Fail(string error)
            {
                return new RecursiveDependencyInstallResult(false, error ?? "unknown recursive dependency install failure");
            }
        }
    }

    internal sealed class CLIDaemonAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            _ = importedAssets;
            _ = deletedAssets;
            _ = movedAssets;
            _ = movedFromAssetPaths;
            CLIDaemon.MarkAssetIndexDirty();
        }
    }
}
#endif
