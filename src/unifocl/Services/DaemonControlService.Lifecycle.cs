using Spectre.Console;
using System.Diagnostics;
using System.Text.Json;

internal sealed partial class DaemonControlService
{
    private static readonly TimeSpan EditorPingTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Makes sure an Editor with unifocl's Pipeline commands has the project open, and attaches the
    /// session to it. An Editor that is already running, GUI or batchmode, is used as it is; otherwise
    /// <c>unity open --batchmode</c> starts one, which plays the part of the old Host mode.
    /// </summary>
    /// <remarks>
    /// <paramref name="requireBridgeMode"/> and <paramref name="preferHostMode"/> are kept for callers:
    /// every Editor now serves the same Pipeline commands, and a second Editor cannot open a project that
    /// one already has open, so neither changes what happens.
    /// </remarks>
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
        _ = requireBridgeMode;
        _ = preferHostMode;
        var port = ResolveProjectDaemonPort(projectPath);
        var timeout = ResolveStartupTimeout(startupTimeout);

        if (UnityCliLocator.Locate() is null)
        {
            Fail(UnityCliLocator.InstallHint);
            log($"[red]daemon[/]: {Markup.Escape(UnityCliLocator.InstallHint)}");
            return false;
        }

        var running = await UnityEditorLifecycle.FindAsync(projectPath);
        if (running is not null || IsUnityClientActiveForProject(projectPath))
        {
            var description = running is null
                ? "a Unity Editor holds the project lock"
                : $"Unity Editor pid={running.Pid} ({(running.Batchmode ? "batchmode" : "GUI")}, {running.State})";
            log($"[grey]daemon[/]: {Markup.Escape(description)}; waiting for unifocl's Pipeline commands");
            if (!await WaitForEditorLinkAsync(projectPath, UnityBridgeAttachWaitTimeout, log))
            {
                Fail($"{description}, but it does not answer unifocl.bridge commands. {PipelineSetupHint}");
                log($"[red]daemon[/]: {Markup.Escape(description)}, but it does not answer unifocl.bridge commands");
                log($"[yellow]daemon[/]: {Markup.Escape(PipelineSetupHint)}");
                return false;
            }

            if (runtime.GetByPort(port) is { } known)
            {
                WarnIfProjectSourceStale(known, log);
            }

            AttachEditor(session, runtime, port, projectPath, running);
            log($"[green]daemon[/]: attached to the running Unity Editor{(running is null ? string.Empty : $" [white]pid={running.Pid}[/]")}");
            return true;
        }

        var mode = allowUnsafe ? "unsafe (ignore compile errors)" : "safe";
        log($"[grey]daemon[/]: starting Unity in batchmode through the Unity CLI ({mode})");
        var open = await UnityEditorLifecycle.OpenAsync(projectPath, ignoreCompileErrors: allowUnsafe, timeout);
        if (!open.Ok)
        {
            Fail(open.Error ?? "unity open failed", open.CompileErrors);
            log($"[red]daemon[/]: {Markup.Escape(open.Error ?? "unity open failed")}");
            foreach (var line in open.CompileErrors.Take(5))
            {
                log($"[red]unity[/]: {Markup.Escape(line)}");
            }

            return false;
        }

        if (!await WaitForEditorLinkAsync(projectPath, ProjectCommandReadyTimeout, log))
        {
            Fail($"the Editor started (pid={open.Pid}) but does not answer unifocl.bridge commands. {PipelineSetupHint}");
            log($"[red]daemon[/]: the Editor started but does not answer unifocl.bridge commands; closing it");
            log($"[yellow]daemon[/]: {Markup.Escape(PipelineSetupHint)}");
            if (!open.AlreadyOpen)
            {
                await UnityEditorLifecycle.CloseAsync(projectPath, TimeSpan.FromSeconds(30));
            }

            return false;
        }

        var started = await UnityEditorLifecycle.FindAsync(projectPath);
        AttachEditor(session, runtime, port, projectPath, started ?? new PipelineEditorInstance(projectPath, 0, open.Pid, "ready", string.Empty, Batchmode: !open.AlreadyOpen));
        _lastStartupFailure = null;
        log($"[green]daemon[/]: started Unity [white]pid={open.Pid}[/] in batchmode");
        return true;
    }

    private const string PipelineSetupHint =
        "The project needs the com.unity.pipeline package and the unifocl Editor package (run /init), on Unity 6000.0 or later.";

    private void Fail(string summary, IReadOnlyList<string>? compileErrors = null)
    {
        _lastStartupFailure = new DaemonStartupFailure(
            IsCompileError: compileErrors is { Count: > 0 },
            Summary: summary,
            Lines: compileErrors?.ToList() ?? []);
    }

    private static void AttachEditor(
        CliSessionState session,
        DaemonRuntime runtime,
        int port,
        string projectPath,
        PipelineEditorInstance? editor)
    {
        SetAttachedPort(session, port, projectPath);
        if (editor is { Batchmode: true })
        {
            // Batchmode Editors are the ones /close and /daemon stop shut down, so they are listed.
            var now = DateTime.UtcNow;
            var startedAt = runtime.GetByPort(port) is { } known && known.Pid == editor.Pid ? known.StartedAtUtc : now;
            runtime.Upsert(new DaemonInstance(port, editor.Pid, startedAt, UnityCliLocator.Locate(), Headless: true, projectPath, now));
        }
    }

    /// <summary>Polls the bridge ping until the Editor answers, logging progress every 10 s.</summary>
    private static async Task<bool> WaitForEditorLinkAsync(string projectPath, TimeSpan timeout, Action<string> log)
    {
        var startedAt = DateTime.UtcNow;
        var deadline = startedAt + timeout;
        var nextProgressAt = startedAt.AddSeconds(10);
        while (true)
        {
            if (await UnityEditorLifecycle.PingAsync(projectPath, EditorPingTimeout))
            {
                return true;
            }

            var now = DateTime.UtcNow;
            if (now >= deadline)
            {
                return false;
            }

            if (now >= nextProgressAt)
            {
                var state = (await UnityEditorLifecycle.FindAsync(projectPath))?.State ?? "not registered with the Unity CLI";
                log($"[grey]daemon[/]: still waiting for the Editor ({Markup.Escape(state)}; {(deadline - now).TotalSeconds:0}s left)");
                nextProgressAt = now.AddSeconds(10);
            }

            await Task.Delay(1000);
        }
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

        if (requireManagedRuntime && runtime.GetByPort(port) is null)
        {
            return false;
        }

        return await UnityEditorLifecycle.PingAsync(projectPath, EditorPingTimeout);
    }

    public async Task<bool> HasStableManagedProjectDaemonAsync(string projectPath, DaemonRuntime runtime, CliSessionState session)
    {
        return await HasStableProjectDaemonAsync(projectPath, runtime, session, requireManagedRuntime: true);
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
            if (await UnityEditorLifecycle.FindAsync(projectPath) is null)
            {
                break;
            }

            if (await UnityEditorLifecycle.PingAsync(projectPath, EditorPingTimeout))
            {
                SetAttachedPort(session, port, projectPath);
                return true;
            }

            if (attempt < normalizedAttemptCount)
            {
                await Task.Delay(normalizedDelayMs);
            }
        }

        log?.Invoke("[yellow]daemon[/]: no Unity Editor with unifocl's Pipeline commands has the project open");
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

    /// <summary>
    /// Detaches from the Editor behind <paramref name="port"/>, and shuts it down when it is a batchmode
    /// Editor. A GUI Editor belongs to the user and stays open, as it did with the old Bridge mode.
    /// </summary>
    public async Task<bool> StopDaemonByPortAsync(
        int port,
        DaemonRuntime runtime,
        CliSessionState session,
        Action<string> log)
    {
        runtime.CleanStaleEntries();
        var projectPath = EditorLinkHandler.TryGetProject(port) ?? runtime.GetByPort(port)?.ProjectPath;
        if (projectPath is null)
        {
            return await StopCliServiceByPortAsync(port, runtime, session, log);
        }

        if (!await StopEditorAsync(projectPath, log))
        {
            return false;
        }

        runtime.Remove(port);
        if (GetPort(session) == port)
        {
            ClearAttachedPort(session);
        }

        return true;
    }

    private static async Task<bool> StopEditorAsync(string projectPath, Action<string> log)
    {
        var editor = await UnityEditorLifecycle.FindAsync(projectPath);
        if (editor is null)
        {
            return true;
        }

        if (!editor.Batchmode)
        {
            log($"[grey]daemon[/]: detached; the Unity Editor (pid={editor.Pid}) stays open");
            return true;
        }

        var close = await UnityEditorLifecycle.CloseAsync(projectPath, TimeSpan.FromSeconds(30));
        if (!close.Ok)
        {
            log($"[red]daemon[/]: could not close the batchmode Editor pid={editor.Pid} ({Markup.Escape(close.Error ?? "unknown error")})");
            return false;
        }

        if (close.Unsaved.Count > 0)
        {
            log($"[yellow]daemon[/]: the Editor discarded unsaved changes in {Markup.Escape(string.Join(", ", close.Unsaved))}");
        }

        return true;
    }

    /// <summary>Stops a unifocl CLI daemon service (<c>--daemon-service</c>), which is not an Editor.</summary>
    private static async Task<bool> StopCliServiceByPortAsync(
        int port,
        DaemonRuntime runtime,
        CliSessionState session,
        Action<string> log)
    {
        var target = runtime.GetByPort(port);
        var stopOk = await TrySendControlAsync(port, "STOP", "STOPPING");
        if (!stopOk && target is not null)
        {
            log($"[red]daemon[/]: failed to stop daemon on port {port} via control socket");
            return false;
        }

        if (target is not null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline && ProcessUtil.IsAlive(target.Pid))
            {
                await Task.Delay(120);
            }

            if (ProcessUtil.IsAlive(target.Pid))
            {
                log($"[red]daemon[/]: daemon process pid={target.Pid} is still alive after shutdown");
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
                    EditorLinkHandler.RegisterProject(configuredPort, projectPath);
                    return configuredPort;
                }
            }
        }
        catch
        {
            // Ignore malformed local bridge config and fall back to deterministic port.
        }

        var port = ComputeProjectDaemonPort(projectPath);
        EditorLinkHandler.RegisterProject(port, projectPath);
        return port;
    }
}
