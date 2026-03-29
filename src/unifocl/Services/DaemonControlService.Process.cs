using Spectre.Console;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

internal sealed partial class DaemonControlService
{
    private async Task<bool> StartDaemonAsync(
        DaemonStartOptions startOptions,
        DaemonRuntime runtime,
        Action<string> log,
        TimeSpan? startupTimeout = null)
    {
        var existing = runtime.GetByPort(startOptions.Port);
        if (existing is not null && ProcessUtil.IsAlive(existing.Pid) && await TrySendControlAsync(existing.Port, "PING", "PONG"))
        {
            log($"[yellow]daemon[/]: port {startOptions.Port} already has a running daemon (pid {existing.Pid})");
            return true;
        }

        if (existing is not null)
        {
            runtime.Remove(startOptions.Port);
        }

        var launch = ResolveDaemonLaunch(startOptions);
        var process = Process.Start(launch);
        if (process is null)
        {
            log("[red]daemon[/]: failed to start daemon process");
            return false;
        }
        var launchedAtUtc = DateTime.UtcNow;

        var outputTail = new Queue<string>();
        var outputLines = new ConcurrentQueue<string>();
        using var outputDrainCts = new CancellationTokenSource();
        var outputDrainTasks = Array.Empty<Task>();
        if (startOptions.Headless)
        {
            var mode = startOptions.AllowUnsafe ? "unsafe (UPM disabled, ignore compile errors)" : "safe (UPM enabled)";
            log($"[grey]daemon[/]: starting Unity in Host mode on port {startOptions.Port} ({mode})");
        }
        outputDrainTasks = StartBackgroundOutputDrain(
            process,
            startOptions.Headless,
            outputDrainCts.Token,
            line =>
            {
                CaptureProcessOutputLine(outputTail, line);
                outputLines.Enqueue(line);
            });

        var resolvedStartupTimeout = ResolveStartupTimeout(startupTimeout);
        var ready = startOptions.Headless
            ? await WaitForHostModeDaemonReadyAsync(
                startOptions.Port,
                process,
                outputLines,
                resolvedStartupTimeout,
                elapsed => log($"[grey]daemon[/]: startup in progress... {elapsed.TotalSeconds:0}s elapsed"),
                line => log($"[grey]unity[/]: {Markup.Escape(line)}"))
            : await WaitForDaemonReadyAsync(startOptions.Port, resolvedStartupTimeout);
        if (!ready)
        {
            while (outputLines.TryDequeue(out var line))
            {
                log($"[grey]unity[/]: {Markup.Escape(line)}");
            }

            if (process.HasExited)
            {
                var details = BuildProcessFailureSummary(outputTail);
                var compileLines = ExtractCompileErrorLines(outputTail);
                _lastStartupFailure = new DaemonStartupFailure(
                    IsCompileError: compileLines.Count > 0,
                    Summary: string.IsNullOrWhiteSpace(details) ? $"process exited with code {process.ExitCode}" : details,
                    Lines: compileLines.Count > 0 ? compileLines : outputTail.ToList());
                log($"[red]daemon[/]: process exited before daemon became ready (pid {process.Id}, exit {process.ExitCode})");
                if (!string.IsNullOrWhiteSpace(details))
                {
                    log($"[red]daemon[/]: startup output -> {Markup.Escape(details)}");
                }
            }
            else
            {
                _lastStartupFailure = new DaemonStartupFailure(
                    IsCompileError: false,
                    Summary: $"daemon did not respond on port {startOptions.Port} within {resolvedStartupTimeout.TotalSeconds:0}s",
                    Lines: outputTail.ToList());
                log($"[red]daemon[/]: process launched (pid {process.Id}) but not responding on port {startOptions.Port} within {resolvedStartupTimeout.TotalSeconds:0}s");
                TryTerminateSpawnedProcess(process, log);
            }

            runtime.Remove(startOptions.Port);
            outputDrainCts.Cancel();
            await AwaitBackgroundTasksAsync(outputDrainTasks, TimeSpan.FromSeconds(2));
            return false;
        }

        // Host mode Unity launch does not self-register in daemon runtime metadata.
        if (startOptions.Headless)
        {
            runtime.Upsert(new DaemonInstance(
                startOptions.Port,
                process.Id,
                launchedAtUtc,
                startOptions.UnityPath,
                startOptions.Headless,
                startOptions.ProjectPath,
                DateTime.UtcNow));
        }

        _lastStartupFailure = null;
        outputDrainCts.Cancel();
        await AwaitBackgroundTasksAsync(outputDrainTasks, TimeSpan.FromSeconds(2));
        log($"[green]daemon[/]: started [white]pid={process.Id}[/] [white]port={startOptions.Port}[/] [white]mode={(startOptions.Headless ? "host" : "bridge")}[/]");
        return true;
    }

    public bool TryGetLastStartupFailure(out DaemonStartupFailure? failure)
    {
        failure = _lastStartupFailure;
        return failure is not null;
    }

    private static ProcessStartInfo ResolveDaemonLaunch(DaemonStartOptions options)
    {
        if (options.Headless && !string.IsNullOrWhiteSpace(options.UnityPath) && !string.IsNullOrWhiteSpace(options.ProjectPath))
        {
            var unityPsi = new ProcessStartInfo(options.UnityPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            unityPsi.ArgumentList.Add("-projectPath");
            unityPsi.ArgumentList.Add(options.ProjectPath);
            unityPsi.ArgumentList.Add("-batchmode");
            unityPsi.ArgumentList.Add("-nographics");
            unityPsi.ArgumentList.Add("-vcsMode");
            unityPsi.ArgumentList.Add("None");
            if (options.AllowUnsafe)
            {
                unityPsi.ArgumentList.Add("-noUpm");
                unityPsi.ArgumentList.Add("-ignoreCompileErrors");
            }
            unityPsi.ArgumentList.Add("-executeMethod");
            unityPsi.ArgumentList.Add("UniFocl.EditorBridge.CLIDaemon.StartServer");
            unityPsi.ArgumentList.Add("--daemon-service");
            unityPsi.ArgumentList.Add("--port");
            unityPsi.ArgumentList.Add(options.Port.ToString());
            unityPsi.ArgumentList.Add("--project");
            unityPsi.ArgumentList.Add(options.ProjectPath);
            unityPsi.ArgumentList.Add("--headless");
            unityPsi.ArgumentList.Add("--ttl-seconds");
            unityPsi.ArgumentList.Add(DefaultInactivityTimeoutSeconds.ToString());
            return unityPsi;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Unable to resolve current process path for daemon launch.");
        }

        var daemonArgs = new List<string>
        {
            "--daemon-service",
            "--port", options.Port.ToString(),
            "--ttl-seconds", DefaultInactivityTimeoutSeconds.ToString()
        };

        if (options.UnityPath is not null)
        {
            daemonArgs.Add("--unity");
            daemonArgs.Add(options.UnityPath);
        }

        if (options.ProjectPath is not null)
        {
            daemonArgs.Add("--project");
            daemonArgs.Add(options.ProjectPath);
        }

        if (options.Headless)
        {
            daemonArgs.Add("--headless");
        }

        if (options.AllowUnsafe)
        {
            daemonArgs.Add("--allow-unsafe");
        }

        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Assembly.GetEntryAssembly()?.Location
                               ?? throw new InvalidOperationException("Unable to resolve entry assembly path.");
            var psi = new ProcessStartInfo(processPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(assemblyPath);
            foreach (var arg in daemonArgs)
            {
                psi.ArgumentList.Add(arg);
            }

            return psi;
        }

        var directPsi = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };
        foreach (var arg in daemonArgs)
        {
            directPsi.ArgumentList.Add(arg);
        }

        return directPsi;
    }

    private static Task[] StartBackgroundOutputDrain(
        Process process,
        bool headless,
        CancellationToken cancellationToken,
        Action<string>? onLine = null)
    {
        if (!headless)
        {
            return [];
        }

        var tasks = new List<Task>(2);
        if (process.StartInfo.RedirectStandardOutput)
        {
            tasks.Add(DrainProcessOutputAsync(process.StandardOutput, onLine, cancellationToken));
        }

        if (process.StartInfo.RedirectStandardError)
        {
            tasks.Add(DrainProcessOutputAsync(process.StandardError, onLine, cancellationToken));
        }

        return [.. tasks];
    }

    private static async Task DrainProcessOutputAsync(StreamReader reader, Action<string>? onLine, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                onLine?.Invoke(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private static void CaptureProcessOutputLine(Queue<string> tail, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var trimmed = line.Trim();
        if (tail.Count >= ProcessOutputTailMaxLines)
        {
            tail.Dequeue();
        }

        tail.Enqueue(trimmed);
    }

    private static string BuildProcessFailureSummary(Queue<string> outputTail)
    {
        if (outputTail.Count == 0)
        {
            return string.Empty;
        }

        var prioritized = outputTail
            .Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)
                           || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
                           || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToList();
        if (prioritized.Count > 0)
        {
            return string.Join(" | ", prioritized);
        }

        return string.Join(" | ", outputTail.TakeLast(3));
    }

    private static List<string> ExtractCompileErrorLines(Queue<string> outputTail)
    {
        var lines = outputTail
            .Where(line =>
                line.Contains("error CS", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Scripts have compiler errors", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Script Compilation Error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Tundra build failed", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Take(30)
            .ToList();
        return lines;
    }

    private static void TryTerminateSpawnedProcess(Process process, Action<string> log)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000))
            {
                log($"[yellow]daemon[/]: failed to terminate stalled Unity process pid={process.Id} within 5s");
                return;
            }

            log($"[yellow]daemon[/]: terminated stalled Unity process pid={process.Id}");
        }
        catch (Exception ex)
        {
            log($"[yellow]daemon[/]: unable to terminate stalled Unity process pid={process.Id} ({Markup.Escape(ex.Message)})");
        }
    }

    private static void TryTerminateHostModeByPid(int pid, Action<string> log)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            TryTerminateSpawnedProcess(process, log);
        }
        catch (ArgumentException)
        {
            // Process already exited.
        }
        catch (Exception ex)
        {
            log($"[yellow]daemon[/]: unable to terminate Host mode Unity process pid={pid} ({Markup.Escape(ex.Message)})");
        }
    }

    private static async Task<bool> WaitForDaemonReadyAsync(int port, TimeSpan timeout, Action<TimeSpan>? onProgress = null)
    {
        var startedAt = DateTime.UtcNow;
        var deadline = startedAt.Add(timeout);
        var nextProgressAt = startedAt.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await TrySendControlAsync(port, "PING", "PONG"))
            {
                return true;
            }

            var now = DateTime.UtcNow;
            if (onProgress is not null && now >= nextProgressAt)
            {
                onProgress(now - startedAt);
                nextProgressAt = now.AddSeconds(5);
            }

            await Task.Delay(180);
        }

        return false;
    }

    private static async Task<bool> WaitForHostModeDaemonReadyAsync(
        int port,
        Process process,
        ConcurrentQueue<string> outputLines,
        TimeSpan timeout,
        Action<TimeSpan>? onProgress = null,
        Action<string>? onOutput = null)
    {
        var startedAt = DateTime.UtcNow;
        var deadline = startedAt.Add(timeout);
        var nextProgressAt = startedAt.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            while (outputLines.TryDequeue(out var line))
            {
                onOutput?.Invoke(line);
            }

            if (await TrySendControlAsync(port, "PING", "PONG"))
            {
                return true;
            }

            if (process.HasExited)
            {
                return false;
            }

            var now = DateTime.UtcNow;
            if (onProgress is not null && now >= nextProgressAt)
            {
                onProgress(now - startedAt);
                nextProgressAt = now.AddSeconds(5);
            }

            await Task.Delay(180);
        }

        while (outputLines.TryDequeue(out var line))
        {
            onOutput?.Invoke(line);
        }

        return false;
    }

    private static async Task<bool> TrySendControlAsync(int port, string request, string expectedResponse)
    {
        var endpoint = request switch
        {
            "PING" => ("GET", $"http://127.0.0.1:{port}/ping"),
            "TOUCH" => ("POST", $"http://127.0.0.1:{port}/touch"),
            "STOP" => ("POST", $"http://127.0.0.1:{port}/stop"),
            _ => default
        };
        if (string.IsNullOrWhiteSpace(endpoint.Item2))
        {
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            HttpResponseMessage response;
            if (endpoint.Item1 == "GET")
            {
                response = await Http.GetAsync(endpoint.Item2, cts.Token);
            }
            else
            {
                response = await Http.PostAsync(endpoint.Item2, new StringContent(string.Empty, Encoding.UTF8, "text/plain"), cts.Token);
            }

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var payload = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
            return string.Equals(payload, expectedResponse, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsProjectCommandEndpointResponsiveAsync(int port, TimeSpan timeout)
    {
        var probe = await ProbeProjectCommandEndpointAsync(port, timeout);
        return probe.Ok;
    }

    private static async Task<ProjectCommandProbeResult> ProbeProjectCommandEndpointAsync(int port, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var content = new StringContent("{\"action\":\"healthcheck\"}", Encoding.UTF8, "application/json");
            var response = await Http.PostAsync($"http://127.0.0.1:{port}/project/command", content, cts.Token);
            var payload = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
            if (!response.IsSuccessStatusCode)
            {
                return new ProjectCommandProbeResult(false, $"HTTP {(int)response.StatusCode}: {(string.IsNullOrWhiteSpace(payload) ? "<empty>" : payload)}");
            }

            if (string.IsNullOrWhiteSpace(payload))
            {
                return new ProjectCommandProbeResult(false, "HTTP 200 with empty payload");
            }

            return new ProjectCommandProbeResult(true, "ok");
        }
        catch (OperationCanceledException)
        {
            return new ProjectCommandProbeResult(false, $"timeout after {(int)timeout.TotalSeconds}s");
        }
        catch (Exception ex)
        {
            return new ProjectCommandProbeResult(false, ex.Message);
        }
    }

    private static async Task<bool> WaitForProjectCommandReadyAsync(
        int port,
        TimeSpan timeout,
        Action<TimeSpan>? onProgress = null)
    {
        var startedAt = DateTime.UtcNow;
        var deadline = startedAt.Add(timeout);
        var nextProgressAt = startedAt.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsProjectCommandEndpointResponsiveAsync(port, TimeSpan.FromSeconds(8)))
            {
                return true;
            }

            var now = DateTime.UtcNow;
            if (onProgress is not null && now >= nextProgressAt)
            {
                onProgress(now - startedAt);
                nextProgressAt = now.AddSeconds(5);
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private static TimeSpan ResolveStartupTimeout(TimeSpan? requestedTimeout)
    {
        var resolved = requestedTimeout ?? MinimumDaemonStartupTimeout;
        return resolved < MinimumDaemonStartupTimeout ? MinimumDaemonStartupTimeout : resolved;
    }

    private readonly record struct ProjectCommandProbeResult(bool Ok, string Detail);

    private static async Task<int?> ResolveAttachedOnlyPortAsync(CliSessionState session)
    {
        if (GetPort(session) is not int attachedPort)
        {
            return null;
        }

        return await TrySendControlAsync(attachedPort, "PING", "PONG") ? attachedPort : null;
    }

    private static string? ResolveDefaultUnityPath(string projectPath, Action<string>? log = null)
    {
        if (UnityEditorPathService.TryReadProjectEditorVersion(projectPath, out var requiredVersion, out _))
        {
            if (UnityEditorPathService.TryResolveEditorForProject(projectPath, out var resolvedEditorPath, out _, out var resolveError))
            {
                var resolvedVersion = UnityEditorPathService.TryInferVersionFromUnityPath(resolvedEditorPath) ?? "unknown";
                log?.Invoke($"[grey]unity[/]: project requires [white]{Markup.Escape(requiredVersion)}[/]; using editor [white]{Markup.Escape(resolvedVersion)}[/]");
                return resolvedEditorPath;
            }

            log?.Invoke($"[red]unity[/]: project requires Unity [white]{Markup.Escape(requiredVersion)}[/], but a matching editor was not found");
            if (!string.IsNullOrWhiteSpace(resolveError))
            {
                log?.Invoke($"[yellow]unity[/]: {Markup.Escape(resolveError)}");
            }

            return null;
        }

        if (UnityEditorPathService.TryGetProjectEditorPath(projectPath, out var projectEditorPath))
        {
            return projectEditorPath;
        }

        if (UnityEditorPathService.TryGetDefaultEditorPath(out var defaultEditorPath))
        {
            return defaultEditorPath;
        }

        var fromEnv = Environment.GetEnvironmentVariable("UNITY_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        return null;
    }

    private static async Task<UnityBridgeAttachWaitResult> WaitForUnityBridgeAttachWithDiagnosticsAsync(
        string projectPath,
        int port,
        CliSessionState session,
        Action<string> log,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        var nextDiagnosticAt = DateTime.UtcNow;
        var nextProgressAt = DateTime.UtcNow.AddSeconds(10);
        var lastSignature = string.Empty;
        var latestDiagnostics = new UnityBridgeBootDiagnostics(
            "package-import=unknown, script-compile=unknown, domain-reload=unknown",
            "initial");

        while (DateTime.UtcNow < deadline)
        {
            if (!IsUnityClientActiveForProject(projectPath))
            {
                return new UnityBridgeAttachWaitResult(
                    Attached: false,
                    DiagnosticSummary: "unity editor closed during bridge wait",
                    EditorClosedDuringWait: true);
            }

            if (await TrySendControlAsync(port, "PING", "PONG"))
            {
                SetAttachedPort(session, port, projectPath);
                await TrySendControlAsync(port, "TOUCH", "OK");
                var readyAfterAttach = await WaitForProjectCommandReadyAsync(
                    port,
                    ProjectCommandReadyTimeout,
                    elapsed => log($"[grey]daemon[/]: waiting for editor bridge endpoint... {elapsed.TotalSeconds:0}s elapsed"));
                if (readyAfterAttach)
                {
                    return new UnityBridgeAttachWaitResult(true, latestDiagnostics.Summary, EditorClosedDuringWait: false);
                }
            }

            var now = DateTime.UtcNow;
            if (now >= nextDiagnosticAt)
            {
                latestDiagnostics = CollectUnityBridgeBootDiagnostics(projectPath);
                var signature = latestDiagnostics.Signature;
                if (!signature.Equals(lastSignature, StringComparison.Ordinal))
                {
                    log($"[grey]daemon[/]: bridge-wait diagnostics -> {Markup.Escape(latestDiagnostics.Summary)}");
                    lastSignature = signature;
                }

                nextDiagnosticAt = now.AddSeconds(3);
            }

            if (now >= nextProgressAt)
            {
                var remaining = deadline - now;
                if (remaining < TimeSpan.Zero)
                {
                    remaining = TimeSpan.Zero;
                }

                log($"[grey]daemon[/]: still waiting for Unity bridge startup ({remaining.TotalSeconds:0}s timeout remaining)");
                nextProgressAt = now.AddSeconds(10);
            }

            await Task.Delay(500);
        }

        return new UnityBridgeAttachWaitResult(false, latestDiagnostics.Summary, EditorClosedDuringWait: false);
    }

    private static UnityBridgeBootDiagnostics CollectUnityBridgeBootDiagnostics(string projectPath)
    {
        var packageState = "unknown";
        var compileState = "unknown";
        var domainReloadState = "unknown";

        if (TryReadUnityEditorLogTail(256 * 1024, out var editorLogTail))
        {
            if (ContainsAny(editorLogTail, "Package Manager", "Resolving packages", "Registering", "UPM", "package resolution"))
            {
                packageState = "in-progress";
            }
            else
            {
                packageState = "idle";
            }

            if (ContainsAny(editorLogTail, "Script compilation", "Compiling", "Assembly-CSharp", "compiler errors", "Compilation failed"))
            {
                compileState = "in-progress-or-failed";
            }
            else
            {
                compileState = "idle";
            }

            if (ContainsAny(editorLogTail, "Domain Reload", "ReloadAssembly", "Reloading assemblies"))
            {
                domainReloadState = "in-progress";
            }
            else
            {
                domainReloadState = "idle";
            }
        }

        var packageJsonPath = Path.Combine(projectPath, "Packages", "packages-lock.json");
        if (File.Exists(packageJsonPath))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(packageJsonPath);
            if (age < TimeSpan.FromSeconds(30))
            {
                packageState = "recently-updated";
            }
        }

        var scriptAssembliesDir = Path.Combine(projectPath, "Library", "ScriptAssemblies");
        if (Directory.Exists(scriptAssembliesDir))
        {
            var newestAssemblyWrite = Directory.EnumerateFiles(scriptAssembliesDir, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            if (newestAssemblyWrite != DateTime.MinValue && DateTime.UtcNow - newestAssemblyWrite < TimeSpan.FromSeconds(30))
            {
                compileState = "recently-updated";
            }
        }

        var summary =
            $"package-import={packageState}, script-compile={compileState}, domain-reload={domainReloadState}";
        return new UnityBridgeBootDiagnostics(
            summary,
            $"{packageState}|{compileState}|{domainReloadState}");
    }

    private static bool TryReadUnityEditorLogTail(int maxBytes, out string text)
    {
        text = string.Empty;
        var path = ResolveUnityEditorLogPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
            {
                return false;
            }

            var length = (int)Math.Min(maxBytes, stream.Length);
            stream.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return false;
            }

            text = Encoding.UTF8.GetString(buffer, 0, read);
            return !string.IsNullOrWhiteSpace(text);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveUnityEditorLogPath()
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, "Library", "Logs", "Unity", "Editor.log");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "Unity", "Editor", "Editor.log");
            }
        }

        var unixHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(unixHome))
        {
            return Path.Combine(unixHome, ".config", "unity3d", "Editor.log");
        }

        return null;
    }

    private static bool ContainsAny(string text, params string[] markers)
    {
        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct UnityBridgeAttachWaitResult(bool Attached, string DiagnosticSummary, bool EditorClosedDuringWait);
    private readonly record struct UnityBridgeBootDiagnostics(string Summary, string Signature);
}
