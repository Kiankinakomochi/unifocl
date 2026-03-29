using Spectre.Console;
using System.Diagnostics;
using System.Text.Json;

internal sealed partial class DaemonControlService
{
    public async Task<bool> EnsureProjectDaemonAsync(
        string projectPath,
        DaemonRuntime runtime,
        CliSessionState session,
        Action<string> log,
        bool requireBridgeMode = false,
        bool preferHostMode = false,
        bool allowUnsafe = false,
        TimeSpan? startupTimeout = null)
    {
        var port = ResolveProjectDaemonPort(projectPath);
        var existing = runtime.GetByPort(port);
        var unityEditorActive = IsUnityClientActiveForProject(projectPath);

        // Unity's InitializeOnLoad Bridge mode endpoint can already be serving this port even when it's not in runtime registry.
        if (await TryAttachProjectDaemonAsync(projectPath, session, attemptCount: 1))
        {
            var projectCommandReady = await IsProjectCommandEndpointResponsiveAsync(port, TimeSpan.FromSeconds(3));
            if (!projectCommandReady)
            {
                if (unityEditorActive)
                {
                    log($"[grey]daemon[/]: endpoint 127.0.0.1:{port} is reachable; waiting for Unity editor bridge command readiness");
                    var bridgeWait = await WaitForUnityBridgeAttachWithDiagnosticsAsync(projectPath, port, session, log, UnityBridgeAttachWaitTimeout);
                    if (bridgeWait.Attached)
                    {
                        return true;
                    }

                    if (bridgeWait.EditorClosedDuringWait)
                    {
                        log("[yellow]daemon[/]: Unity editor closed while waiting for Bridge mode; continuing with Host mode startup");
                    }
                    else
                    {
                        _lastStartupFailure = new DaemonStartupFailure(
                            IsCompileError: false,
                            Summary: $"Unity editor bridge endpoint is reachable but project commands are not ready on port {port} ({bridgeWait.DiagnosticSummary})",
                            Lines: []);
                        log($"[red]daemon[/]: Unity editor bridge is not ready on [white]127.0.0.1:{port}[/] ({Markup.Escape(bridgeWait.DiagnosticSummary)})");
                        return false;
                    }
                }
                else
                {
                    log($"[yellow]daemon[/]: endpoint 127.0.0.1:{port} responds to ping but project commands are unresponsive; restarting bridge");
                    await TrySendControlAsync(port, "STOP", "STOPPING");
                    ClearAttachedPort(session);
                    await Task.Delay(200);
                }
            }
            else
            {
                var bridgeSatisfied = !requireBridgeMode || SupportsBridgeMode(existing);
                var hostModeSatisfied = !preferHostMode || (existing?.Headless ?? false);
                var managedRuntimePresent = existing is not null;

                if (managedRuntimePresent && bridgeSatisfied && hostModeSatisfied)
                {
                    WarnIfProjectSourceStale(existing!, log);
                    return true;
                }

                if (!preferHostMode && bridgeSatisfied)
                {
                    if (existing is not null) WarnIfProjectSourceStale(existing, log);
                    return true;
                }

                log($"[yellow]daemon[/]: endpoint 127.0.0.1:{port} is attachable but unmanaged; restarting in managed Host mode");
                await TrySendControlAsync(port, "STOP", "STOPPING");
                ClearAttachedPort(session);
                await Task.Delay(200);
            }
        }

        if (unityEditorActive)
        {
            log($"[grey]daemon[/]: Unity editor lock detected for project; waiting for Bridge mode endpoint on [white]127.0.0.1:{port}[/]");
            var bridgeWait = await WaitForUnityBridgeAttachWithDiagnosticsAsync(projectPath, port, session, log, UnityBridgeAttachWaitTimeout);
            if (bridgeWait.Attached)
            {
                return true;
            }

            if (bridgeWait.EditorClosedDuringWait)
            {
                log("[yellow]daemon[/]: Unity editor closed while waiting for Bridge mode; continuing with Host mode startup");
            }
            else
            {
                _lastStartupFailure = new DaemonStartupFailure(
                    IsCompileError: false,
                    Summary: $"Unity editor lock detected for project, but bridge endpoint 127.0.0.1:{port} is not attachable ({bridgeWait.DiagnosticSummary})",
                    Lines: []);
                log($"[red]daemon[/]: Unity editor is already running for this project, but Bridge mode endpoint [white]127.0.0.1:{port}[/] is not attachable");
                log($"[yellow]daemon[/]: bridge diagnostics -> {Markup.Escape(bridgeWait.DiagnosticSummary)}");
                log("[yellow]daemon[/]: Host mode launch is skipped while Unity lock is active to avoid Unity file-lock startup failure");
                return false;
            }
        }

        if (existing is not null)
        {
            var stopped = await TrySendControlAsync(port, "STOP", "STOPPING");
            if (stopped)
            {
                var deadline = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < deadline && ProcessUtil.IsAlive(existing.Pid))
                {
                    await Task.Delay(120);
                }
            }

            runtime.Remove(port);
        }

        var unityPath = ResolveDefaultUnityPath(projectPath, log);
        if (requireBridgeMode && string.IsNullOrWhiteSpace(unityPath))
        {
            log("[red]daemon[/]: hierarchy asset load requires Bridge mode, but no matching Unity editor path is configured");
            return false;
        }

        var startOptions = new DaemonStartOptions(
            port,
            unityPath,
            projectPath,
            Headless: true,
            AllowUnsafe: allowUnsafe);

        var started = await StartDaemonAsync(startOptions, runtime, log, startupTimeout);
        if (!started)
        {
            if (ShouldRetryAfterRecoverableStartupFailure(_lastStartupFailure))
            {
                log("[yellow]daemon[/]: detected recoverable startup failure; attempting one cleanup + restart");
                await CleanupRecoverableStartupFailureAsync(projectPath, runtime, session, log);
                started = await StartDaemonAsync(startOptions, runtime, log, startupTimeout);
                if (!started)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        SetAttachedPort(session, port, projectPath);
        await TrySendControlAsync(port, "TOUCH", "OK");
        var projectCommandReadyTimeout = ResolveStartupTimeout(startupTimeout);
        var projectCommandReadyAfterStart = await WaitForProjectCommandReadyAsync(
            port,
            projectCommandReadyTimeout,
            elapsed => log($"[grey]daemon[/]: waiting for project command endpoint... {elapsed.TotalSeconds:0}s elapsed"));
        if (!projectCommandReadyAfterStart)
        {
            var probe = await ProbeProjectCommandEndpointAsync(port, TimeSpan.FromSeconds(8));
            _lastStartupFailure = new DaemonStartupFailure(
                IsCompileError: false,
                Summary: $"project command endpoint is not ready on port {port} ({probe.Detail})",
                Lines: []);
            log($"[red]daemon[/]: project command endpoint is not ready on [white]127.0.0.1:{port}[/] ({Markup.Escape(probe.Detail)})");
            await TrySendControlAsync(port, "STOP", "STOPPING");
            var launched = runtime.GetByPort(port);
            if (launched is not null)
            {
                var deadline = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < deadline && ProcessUtil.IsAlive(launched.Pid))
                {
                    await Task.Delay(120);
                }
            }

            runtime.Remove(port);
            ClearAttachedPort(session);
            return false;
        }

        return true;
    }

    private static bool ShouldRetryAfterRecoverableStartupFailure(DaemonStartupFailure? failure)
    {
        if (failure is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(failure.Summary) && IsRecoverableStartupFailureLine(failure.Summary))
        {
            return true;
        }

        return failure.Lines.Any(IsRecoverableStartupFailureLine);
    }

    private static bool IsRecoverableStartupFailureLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        return line.Contains("Failed to start primary listening socket", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Failed to start secondary listening socket", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Failed to start the Unity Package Manager local server process", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Error: listen EPERM", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Licensing initialization failed", StringComparison.OrdinalIgnoreCase)
               || line.Contains("another Unity instance is running", StringComparison.OrdinalIgnoreCase);
    }

    private async Task CleanupRecoverableStartupFailureAsync(
        string projectPath,
        DaemonRuntime runtime,
        CliSessionState session,
        Action<string> log)
    {
        runtime.CleanStaleEntries();
        var matchingInstances = runtime.GetAll()
            .Where(instance =>
                !string.IsNullOrWhiteSpace(instance.ProjectPath)
                && Path.GetFullPath(instance.ProjectPath).Equals(Path.GetFullPath(projectPath), StringComparison.Ordinal))
            .OrderBy(instance => instance.Port)
            .ToList();

        foreach (var instance in matchingInstances)
        {
            await StopDaemonByPortAsync(instance.Port, runtime, session, log);
        }

        StopUnityLicensingClients(log);
        await Task.Delay(500);
    }

    public async Task<bool> HasStableProjectDaemonAsync(
        string projectPath,
        DaemonRuntime runtime,
        CliSessionState session,
        bool requireManagedRuntime = false)
    {
        var port = ResolveProjectDaemonPort(projectPath);
        if (GetPort(session) != port)
        {
            return false;
        }

        if (requireManagedRuntime)
        {
            var instance = runtime.GetByPort(port);
            if (instance is null || string.IsNullOrWhiteSpace(instance.UnityPath))
            {
                return false;
            }
        }

        if (!await TrySendControlAsync(port, "PING", "PONG"))
        {
            return false;
        }

        return await WaitForProjectCommandReadyAsync(port, TimeSpan.FromSeconds(8));
    }

    public async Task<bool> HasStableManagedProjectDaemonAsync(string projectPath, DaemonRuntime runtime, CliSessionState session)
    {
        return await HasStableProjectDaemonAsync(projectPath, runtime, session, requireManagedRuntime: true);
    }

    private static bool SupportsBridgeMode(DaemonInstance? instance)
    {
        if (instance is null)
        {
            // Port is serving but not from runtime registry (likely InitializeOnLoad Bridge mode).
            return true;
        }

        return !string.IsNullOrWhiteSpace(instance.UnityPath);
    }

    public async Task<bool> TryAttachProjectDaemonAsync(
        string projectPath,
        CliSessionState session,
        Action<string>? log = null,
        int attemptCount = 8,
        int attemptDelayMs = 250)
    {
        var port = ResolveProjectDaemonPort(projectPath);
        var normalizedAttemptCount = Math.Max(1, attemptCount);
        var normalizedDelayMs = Math.Max(50, attemptDelayMs);

        for (var attempt = 1; attempt <= normalizedAttemptCount; attempt++)
        {
            if (await TrySendControlAsync(port, "PING", "PONG"))
            {
                SetAttachedPort(session, port, projectPath);
                await TrySendControlAsync(port, "TOUCH", "OK");
                return true;
            }

            if (attempt < normalizedAttemptCount)
            {
                await Task.Delay(normalizedDelayMs);
            }
        }

        log?.Invoke($"[yellow]daemon[/]: project daemon endpoint 127.0.0.1:{port} is not ready for attachment");
        return false;
    }

    public async Task<bool> TouchAttachedDaemonAsync(CliSessionState session)
    {
        if (GetPort(session) is not int touchPort)
        {
            return false;
        }

        return await TrySendControlAsync(touchPort, "TOUCH", "OK");
    }

    public async Task<bool> StopDaemonByPortAsync(
        int port,
        DaemonRuntime runtime,
        CliSessionState session,
        Action<string> log)
    {
        runtime.CleanStaleEntries();
        var target = runtime.GetByPort(port);
        if (target is null)
        {
            var pingOk = await TrySendControlAsync(port, "PING", "PONG");
            if (pingOk)
            {
                var stopAttachedOnly = await TrySendControlAsync(port, "STOP", "STOPPING");
                if (!stopAttachedOnly)
                {
                    log($"[red]daemon[/]: failed to stop live endpoint on port {port}");
                    return false;
                }
            }

            runtime.Remove(port);
            if (GetPort(session) == port)
            {
                ClearAttachedPort(session);
            }

            return true;
        }

        var stopOk = await TrySendControlAsync(target.Port, "STOP", "STOPPING");
        if (!stopOk)
        {
            log($"[red]daemon[/]: failed to stop daemon on port {target.Port} via control socket");
            return false;
        }

        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline && ProcessUtil.IsAlive(target.Pid))
        {
            await Task.Delay(120);
        }

        if (ProcessUtil.IsAlive(target.Pid))
        {
            if (target.Headless)
            {
                TryTerminateHostModeByPid(target.Pid, log);
            }
            else
            {
                log($"[yellow]daemon[/]: process pid={target.Pid} is still alive after stop request; skipped force-kill because daemon is not running in Host mode");
            }
        }

        if (ProcessUtil.IsAlive(target.Pid))
        {
            log($"[red]daemon[/]: daemon process pid={target.Pid} is still alive after shutdown");
            return false;
        }

        runtime.Remove(target.Port);
        if (GetPort(session) == target.Port)
        {
            ClearAttachedPort(session);
        }

        return true;
    }

    public int StopUnityLicensingClients(Action<string> log)
    {
        List<Process> processes;
        try
        {
            processes = Process.GetProcesses()
                .Where(IsUnityLicensingClientProcess)
                .ToList();
        }
        catch (Exception ex)
        {
            log($"[yellow]daemon[/]: unable to enumerate Unity licensing client processes ({Markup.Escape(ex.Message)})");
            return 0;
        }

        var attempted = 0;
        var stopped = 0;
        foreach (var process in processes)
        {
            try
            {
                attempted++;
                if (TryTerminateUnityLicensingClient(process, log))
                {
                    stopped++;
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        if (attempted > 0)
        {
            log($"[grey]daemon[/]: licensing-client cleanup closed {stopped}/{attempted} process(es)");
        }

        return stopped;
    }

    private static bool IsUnityLicensingClientProcess(Process process)
    {
        if (process.Id == Environment.ProcessId)
        {
            return false;
        }

        string processName;
        try
        {
            processName = process.ProcessName;
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        if (processName.Contains("Unity.Licensing.Client", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("UnityLicensingClient", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return processName.Contains("unity", StringComparison.OrdinalIgnoreCase)
               && processName.Contains("licens", StringComparison.OrdinalIgnoreCase)
               && processName.Contains("client", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryTerminateUnityLicensingClient(Process process, Action<string> log)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(3000))
            {
                log($"[yellow]daemon[/]: licensing client pid={process.Id} did not exit within 3s");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            log($"[yellow]daemon[/]: unable to terminate licensing client pid={process.Id} ({Markup.Escape(ex.Message)})");
            return false;
        }
    }

    public static bool IsUnityClientActiveForProject(string projectPath)
    {
        var lockFile = Path.Combine(projectPath, "Temp", "UnityLockfile");
        if (!File.Exists(lockFile))
        {
            return false;
        }

        try
        {
            return Process.GetProcessesByName("Unity").Any() || Process.GetProcessesByName("Unity Editor").Any();
        }
        catch
        {
            return false;
        }
    }

    public static int ComputeProjectDaemonPort(string projectPath)
    {
        var normalized = Path.GetFullPath(projectPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');

        unchecked
        {
            var hash = 17;
            foreach (var ch in normalized)
            {
                hash = (hash * 31) + ch;
            }

            return 18080 + ((hash & 0x7fffffff) % 2000);
        }
    }

    public static int ResolveProjectDaemonPort(string projectPath)
    {
        var bridgePath = Path.Combine(projectPath, ".unifocl", "bridge.json");
        try
        {
            if (File.Exists(bridgePath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(bridgePath));
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("daemon", out var daemonElement)
                    && daemonElement.ValueKind == JsonValueKind.Object
                    && daemonElement.TryGetProperty("port", out var portElement)
                    && portElement.TryGetInt32(out var configuredPort)
                    && configuredPort is > 0 and <= 65535)
                {
                    return configuredPort;
                }
            }
        }
        catch
        {
            // Ignore malformed local bridge config and fall back to deterministic port.
        }

        return ComputeProjectDaemonPort(projectPath);
    }
}
