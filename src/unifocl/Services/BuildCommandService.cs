using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Spectre.Console;

internal sealed class BuildCommandService
{
    private static readonly TimeSpan BuildMonitorPeriodicRenderInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan UnityHubInstallTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AdbInstallTimeout = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HierarchyDaemonClient _daemonClient = new();
    private readonly Dictionary<string, List<BuildTargetCandidate>> _buildTargetCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task HandleBuildCommandAsync(
        string input,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            log("[yellow]mode[/]: open a project first with /open");
            return;
        }

        var tokens = Tokenize(input);
        if (tokens.Count == 0 || !tokens[0].Equals("/build", StringComparison.OrdinalIgnoreCase))
        {
            log("[x] usage: /build <run|exec|scenes|addressables|cancel|targets|logs>");
            return;
        }

        if (tokens.Count < 2)
        {
            log("[x] usage: /build <run|exec|scenes|addressables|cancel|targets|logs>");
            return;
        }

        var subcommand = tokens[1].ToLowerInvariant();
        switch (subcommand)
        {
            case "run":
                await HandleRunAsync(tokens, session, daemonControlService, daemonRuntime, log);
                return;
            case "exec":
                await HandleExecAsync(tokens, session, daemonControlService, daemonRuntime, log);
                return;
            case "scenes":
                await HandleScenesAsync(tokens, session, daemonControlService, daemonRuntime, log);
                return;
            case "addressables":
                await HandleAddressablesAsync(tokens, session, daemonControlService, daemonRuntime, log);
                return;
            case "cancel":
                await HandleCancelAsync(tokens, session, daemonControlService, daemonRuntime, log);
                return;
            case "targets":
                await HandleTargetsAsync(tokens, session, daemonControlService, daemonRuntime, log);
                return;
            case "logs":
                await HandleLogsAsync(tokens, session, daemonControlService, daemonRuntime, log);
                return;
            default:
                log($"[x] unsupported build subcommand: {Markup.Escape(tokens[1])}");
                log("supported: /build run | /build exec | /build scenes | /build addressables | /build cancel | /build targets | /build logs");
                return;
        }
    }

    public async Task NotifyAttachedBuildIfAnyAsync(CliSessionState session, Action<string> log)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            return;
        }

        var status = await _daemonClient.GetBuildStatusAsync(port);
        if (status is null || !status.Running)
        {
            return;
        }

        var step = string.IsNullOrWhiteSpace(status.Step) ? "running" : status.Step;
        log($"[yellow]build[/]: resumed monitoring for ongoing {Markup.Escape(status.Kind)} build - {Markup.Escape(step)} ({status.Progress01 * 100:0}%)");
    }

    private async Task HandleRunAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        try
        {
            var dev = false;
            var debug = false;
            var clean = false;
            string? outputPath = null;
            var positionalArgs = new List<string>();
            for (var i = 2; i < tokens.Count; i++)
            {
                var option = tokens[i];
                if (!option.StartsWith("--", StringComparison.Ordinal))
                {
                    positionalArgs.Add(option);
                    continue;
                }

                if (option.Equals("--dev", StringComparison.OrdinalIgnoreCase))
                {
                    dev = true;
                    continue;
                }

                if (option.Equals("--debug", StringComparison.OrdinalIgnoreCase))
                {
                    debug = true;
                    continue;
                }

                if (option.Equals("--clean", StringComparison.OrdinalIgnoreCase))
                {
                    clean = true;
                    continue;
                }

                if (option.Equals("--path", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= tokens.Count)
                    {
                        log("[x] usage: /build run [target] [--dev] [--debug] [--clean] [--path <output-path>]");
                        return;
                    }

                    outputPath = tokens[++i];
                    continue;
                }

                if (option.StartsWith("--path=", StringComparison.OrdinalIgnoreCase))
                {
                    outputPath = option["--path=".Length..];
                    continue;
                }

                log($"[x] unsupported flag: {Markup.Escape(option)}");
                log("supported flags: --dev --debug --clean --path <output-path>");
                return;
            }

            var unityReady = await RunWithSpinnerAsync(
                "Ensuring Unity bridge context...",
                () => EnsureUnityContextAsync(session, daemonControlService, daemonRuntime, requireUnityBridge: true));
            if (!unityReady)
            {
                log("[x] build run failed: Unity editor bridge is unavailable; set UNITY_PATH or open Unity editor for this project");
                return;
            }

            var targetCandidates = await GetBuildTargetCandidatesAsync(session, log, forceRefresh: false, showStatus: true);
            if (targetCandidates.Count == 0)
            {
                log("[x] build run failed: target list is unavailable");
                return;
            }

            var requestedTarget = positionalArgs.FirstOrDefault();
            var selectedTarget = ResolveSelectedTarget(requestedTarget, targetCandidates, log);
            if (selectedTarget is null)
            {
                return;
            }

            if (!selectedTarget.Installed)
            {
                log($"[yellow]build[/]: target [white]{Markup.Escape(selectedTarget.DisplayName)}[/] is not installed; queuing install then build");
                var installed = await TryInstallTargetSupportAsync(selectedTarget, session, daemonControlService, daemonRuntime, log);
                if (!installed)
                {
                    log($"[x] build run failed: could not install target support for {Markup.Escape(selectedTarget.DisplayName)}");
                    return;
                }
            }

            var payload = JsonSerializer.Serialize(
                new BuildRunRequestPayload(selectedTarget.TargetToken, dev, debug, clean, outputPath),
                WriteJsonOptions);
            var response = await RunWithSpinnerAsync(
                "Queueing build run...",
                () => ExecuteProjectCommandAsync(session, new ProjectCommandRequestDto("build-run", null, null, payload)));
            EmitProjectResponse("build run", response, log);
            if (!response.Ok)
            {
                return;
            }

            await MonitorBuildProgressAsync(session, daemonControlService, daemonRuntime, log, promptDeploy: true);
        }
        catch (Exception ex)
        {
            log($"[x] build run crashed in CLI: {Markup.Escape(ex.GetType().Name)}: {Markup.Escape(ex.Message)}");
            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
            {
                var firstLine = ex.StackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                if (!string.IsNullOrWhiteSpace(firstLine))
                {
                    log($"[grey]build[/]: stack {Markup.Escape(firstLine)}");
                }
            }
        }
    }

    private async Task HandleExecAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        if (tokens.Count < 3)
        {
            log("[x] usage: /build exec <Method>");
            return;
        }

        var method = string.Join(' ', tokens.Skip(2)).Trim();
        if (string.IsNullOrWhiteSpace(method))
        {
            log("[x] usage: /build exec <Method>");
            return;
        }

        var unityReady = await RunWithSpinnerAsync(
            "Ensuring Unity bridge context...",
            () => EnsureUnityContextAsync(session, daemonControlService, daemonRuntime, requireUnityBridge: true));
        if (!unityReady)
        {
            log("[x] build exec failed: Unity editor bridge is unavailable; set UNITY_PATH or open Unity editor for this project");
            return;
        }

        var payload = JsonSerializer.Serialize(new BuildExecRequestPayload(method), WriteJsonOptions);
        var response = await RunWithSpinnerAsync(
            "Queueing build exec...",
            () => ExecuteProjectCommandAsync(session, new ProjectCommandRequestDto("build-exec", null, null, payload)));
        EmitProjectResponse("build exec", response, log);
        if (!response.Ok)
        {
            return;
        }

        await MonitorBuildProgressAsync(session, daemonControlService, daemonRuntime, log, promptDeploy: false);
    }

    private async Task HandleScenesAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        if (tokens.Count != 2)
        {
            log("[x] usage: /build scenes");
            return;
        }

        var unityReady = await RunWithSpinnerAsync(
            "Ensuring Unity bridge context...",
            () => EnsureUnityContextAsync(session, daemonControlService, daemonRuntime, requireUnityBridge: true));
        if (!unityReady)
        {
            log("[x] build scenes failed: Unity editor bridge is unavailable; set UNITY_PATH or open Unity editor for this project");
            return;
        }

        var response = await RunWithSpinnerAsync(
            "Loading build scenes...",
            () => ExecuteProjectCommandAsync(session, new ProjectCommandRequestDto("build-scenes-get", null, null, null)));
        if (!response.Ok)
        {
            EmitProjectResponse("build scenes", response, log);
            return;
        }

        BuildScenesResponsePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BuildScenesResponsePayload>(response.Content ?? string.Empty, ReadJsonOptions);
        }
        catch (Exception ex)
        {
            log($"[x] build scenes failed: invalid payload ({Markup.Escape(ex.Message)})");
            return;
        }

        var scenes = (payload?.Scenes ?? [])
            .Where(scene => !string.IsNullOrWhiteSpace(scene.Path))
            .ToList();
        if (scenes.Count == 0)
        {
            log("[yellow]build[/]: no scenes are currently configured in EditorBuildSettings");
            return;
        }

        var prompt = new MultiSelectionPrompt<string>()
            .Title("Select scenes to [green]enable[/] in [grey]EditorBuildSettings[/]")
            .NotRequired()
            .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to save)[/]");
        foreach (var scene in scenes)
        {
            prompt.AddChoice(scene.Path!);
        }

        var selected = CliTheme.PromptWithDividers(() => AnsiConsole.Prompt(prompt));
        var selectedSet = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        var updatePayload = new BuildScenesUpdateRequestPayload(
            scenes.Select(scene => new BuildSceneEntryPayload(scene.Path!, selectedSet.Contains(scene.Path!))).ToList());
        var content = JsonSerializer.Serialize(updatePayload, WriteJsonOptions);
        var saveResponse = await RunWithSpinnerAsync(
            "Saving build scenes...",
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("build-scenes-set", null, null, content)));
        EmitProjectResponse("build scenes", saveResponse, log);
    }

    private async Task HandleAddressablesAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var clean = false;
        var update = false;
        for (var i = 2; i < tokens.Count; i++)
        {
            var option = tokens[i];
            if (option.Equals("--clean", StringComparison.OrdinalIgnoreCase))
            {
                clean = true;
                continue;
            }

            if (option.Equals("--update", StringComparison.OrdinalIgnoreCase))
            {
                update = true;
                continue;
            }

            log($"[x] unsupported flag: {Markup.Escape(option)}");
            log("supported flags: --clean --update");
            return;
        }

        var unityReady = await RunWithSpinnerAsync(
            "Ensuring Unity bridge context...",
            () => EnsureUnityContextAsync(session, daemonControlService, daemonRuntime, requireUnityBridge: true));
        if (!unityReady)
        {
            log("[x] build addressables failed: Unity editor bridge is unavailable; set UNITY_PATH or open Unity editor for this project");
            return;
        }

        var payload = JsonSerializer.Serialize(new BuildAddressablesRequestPayload(clean, update), WriteJsonOptions);
        var response = await RunWithSpinnerAsync(
            "Queueing addressables build...",
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("build-addressables", null, null, payload)));
        EmitProjectResponse("build addressables", response, log);
        if (!response.Ok)
        {
            return;
        }

        await MonitorBuildProgressAsync(session, daemonControlService, daemonRuntime, log, promptDeploy: false);
    }

    private async Task HandleCancelAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        if (tokens.Count != 2)
        {
            log("[x] usage: /build cancel");
            return;
        }

        await CancelBuildAndTeardownAsync(session, daemonControlService, daemonRuntime, log, promptReturnKey: true);
    }

    private async Task HandleTargetsAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        if (tokens.Count != 2)
        {
            log("[x] usage: /build targets");
            return;
        }

        var unityReady = await RunWithSpinnerAsync(
            "Ensuring Unity bridge context...",
            () => EnsureUnityContextAsync(session, daemonControlService, daemonRuntime, requireUnityBridge: true));
        if (!unityReady)
        {
            log("[x] build targets failed: Unity editor bridge is unavailable; set UNITY_PATH or open Unity editor for this project");
            return;
        }

        var targets = await GetBuildTargetCandidatesAsync(session, log, forceRefresh: false, showStatus: true);
        if (targets.Count == 0)
        {
            log("[yellow]build[/]: no build targets were reported by the bridge");
            return;
        }

        log("[grey]build[/]: installed build target support");
        foreach (var target in targets)
        {
            var mark = target.Installed ? "[green]installed[/]" : "[grey]not installed[/]";
            var note = string.IsNullOrWhiteSpace(target.Note) ? string.Empty : $" [grey]({Markup.Escape(target.Note)})[/]";
            log($" - [white]{Markup.Escape(target.DisplayName)}[/]: {mark}{note}");
        }
    }

    private async Task<List<BuildTargetCandidate>> GetBuildTargetCandidatesAsync(
        CliSessionState session,
        Action<string> log,
        bool forceRefresh,
        bool showStatus)
    {
        if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath)
            && !forceRefresh
            && _buildTargetCache.TryGetValue(session.CurrentProjectPath, out var cached))
        {
            return cached;
        }

        async Task<List<BuildTargetCandidate>> QueryAsync()
        {
            var response = await ExecuteProjectCommandAsync(session, new ProjectCommandRequestDto("build-targets", null, null, null));
            if (!response.Ok)
            {
                EmitProjectResponse("build targets", response, log);
                return [];
            }

            BuildTargetsResponsePayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<BuildTargetsResponsePayload>(response.Content ?? string.Empty, ReadJsonOptions);
            }
            catch (Exception ex)
            {
                log($"[x] build targets failed: invalid payload ({Markup.Escape(ex.Message)})");
                return [];
            }

            var parsed = (payload?.Targets ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .Select(entry =>
                {
                    var targetToken = string.IsNullOrWhiteSpace(entry.Note) ? entry.Name! : entry.Note!;
                    return new BuildTargetCandidate(
                        entry.Name!,
                        targetToken,
                        entry.Installed,
                        ResolveUnityHubModuleId(entry.Name!, targetToken),
                        entry.Note);
                })
                .ToList();

            if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                _buildTargetCache[session.CurrentProjectPath] = parsed;
            }

            return parsed;
        }

        if (!showStatus || Console.IsInputRedirected)
        {
            return await QueryAsync();
        }

        var result = new List<BuildTargetCandidate>();
        await AnsiConsole.Status()
            .Spinner(TuiTrackableProgress.StatusSpinner)
            .StartAsync("Querying installed build targets...", async _ =>
            {
                result = await QueryAsync();
            });
        return result;
    }

    private BuildTargetCandidate? ResolveSelectedTarget(string? requestedTarget, IReadOnlyList<BuildTargetCandidate> targets, Action<string> log)
    {
        if (!string.IsNullOrWhiteSpace(requestedTarget))
        {
            var matched = MatchTarget(requestedTarget, targets);
            if (matched is not null)
            {
                return matched;
            }

            log($"[x] build run failed: unknown target '{Markup.Escape(requestedTarget)}'");
            log($"[grey]build[/]: available targets -> {string.Join(", ", targets.Select(t => t.DisplayName))}");
            return null;
        }

        if (Console.IsInputRedirected)
        {
            var fallback = targets.FirstOrDefault(target => target.Installed) ?? targets.FirstOrDefault();
            if (fallback is null)
            {
                log("[x] build run failed: no selectable target");
                return null;
            }

            log($"[yellow]build[/]: non-interactive mode; defaulting to {Markup.Escape(fallback.DisplayName)}");
            return fallback;
        }

        var ordered = targets
            .OrderByDescending(target => target.Installed)
            .ThenBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var prompt = new SelectionPrompt<BuildTargetCandidate>()
            .Title("Select build target")
            .PageSize(Math.Min(12, ordered.Count))
            .UseConverter(target => target.Installed
                ? $"[white]{Markup.Escape(target.DisplayName)}[/] [green](installed)[/]"
                : $"[grey]{Markup.Escape(target.DisplayName)}[/] [dim](not installed, will install)[/]")
            .AddChoices(ordered);
        return CliTheme.PromptWithDividers(() => AnsiConsole.Prompt(prompt));
    }

    private static BuildTargetCandidate? MatchTarget(string raw, IReadOnlyList<BuildTargetCandidate> targets)
    {
        var normalized = NormalizeTarget(raw);
        foreach (var target in targets)
        {
            if (NormalizeTarget(target.DisplayName) == normalized
                || NormalizeTarget(target.TargetToken) == normalized
                || (target.Note is not null && NormalizeTarget(target.Note) == normalized))
            {
                return target;
            }

            foreach (var alias in EnumerateTargetAliases(target))
            {
                if (NormalizeTarget(alias) == normalized)
                {
                    return target;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateTargetAliases(BuildTargetCandidate target)
    {
        var token = target.TargetToken;
        if (token.Equals("StandaloneWindows64", StringComparison.OrdinalIgnoreCase))
        {
            yield return "win64";
            yield return "windows64";
        }
        else if (token.Equals("Android", StringComparison.OrdinalIgnoreCase))
        {
            yield return "android";
        }
        else if (token.Equals("iOS", StringComparison.OrdinalIgnoreCase))
        {
            yield return "ios";
        }
        else if (token.Equals("WebGL", StringComparison.OrdinalIgnoreCase))
        {
            yield return "webgl";
        }
        else if (token.Equals("StandaloneOSX", StringComparison.OrdinalIgnoreCase))
        {
            yield return "mac";
            yield return "macos";
            yield return "osx";
        }
        else if (token.Equals("StandaloneLinux64", StringComparison.OrdinalIgnoreCase))
        {
            yield return "linux";
            yield return "linux64";
        }
    }

    private static string NormalizeTarget(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    private async Task<bool> TryInstallTargetSupportAsync(
        BuildTargetCandidate target,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(target.ModuleId))
        {
            log($"[x] install failed: no Unity Hub module mapping for {Markup.Escape(target.DisplayName)}");
            return false;
        }

        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            log("[x] install failed: project path is unavailable");
            return false;
        }

        if (!UnityEditorPathService.TryReadProjectEditorVersion(session.CurrentProjectPath, out var version, out var versionError))
        {
            log($"[x] install failed: could not resolve Unity editor version ({Markup.Escape(versionError ?? "unknown error")})");
            return false;
        }

        if (!TryResolveUnityHubPath(out var unityHubPath))
        {
            log("[x] install failed: Unity Hub executable was not found (set UNITY_HUB_PATH)");
            return false;
        }

        log($"[grey]build[/]: installing module [white]{Markup.Escape(target.ModuleId)}[/] for Unity [white]{Markup.Escape(version)}[/]");
        var installArgs = $"-- --headless install-modules --version {version} --module {target.ModuleId}";
        var exitCode = await RunProcessStreamingAsync(
            unityHubPath,
            installArgs,
            line => log($"[grey]hub[/]: {Markup.Escape(line)}"),
            timeout: UnityHubInstallTimeout);
        if (exitCode != 0)
        {
            log($"[x] install failed: Unity Hub exited with code {exitCode}");
            return false;
        }

        if (DaemonControlService.GetPort(session) is int attachedPort)
        {
            await daemonControlService.StopDaemonByPortAsync(attachedPort, daemonRuntime, session, _ => { });
        }

        var unityReady = await RunWithSpinnerAsync(
            "Restarting Unity bridge after module install...",
            () => EnsureUnityContextAsync(session, daemonControlService, daemonRuntime, requireUnityBridge: true));
        if (!unityReady)
        {
            log("[x] install succeeded but bridge restart failed");
            return false;
        }

        var refreshedTargets = await GetBuildTargetCandidatesAsync(session, log, forceRefresh: true, showStatus: true);
        var refreshed = MatchTarget(target.TargetToken, refreshedTargets);
        if (refreshed?.Installed == true)
        {
            log($"[green]build[/]: module installation complete for {Markup.Escape(target.DisplayName)}");
            return true;
        }

        log($"[x] install failed: target {Markup.Escape(target.DisplayName)} still reports not installed");
        return false;
    }

    private static bool TryResolveUnityHubPath(out string executablePath)
    {
        var env = Environment.GetEnvironmentVariable("UNITY_HUB_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            executablePath = env;
            return true;
        }

        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? new[]
            {
                "/Applications/Unity Hub.app/Contents/MacOS/Unity Hub",
                "/Applications/Unity Hub.app/Contents/MacOS/UnityHub"
            }
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new[]
                {
                    @"C:\Program Files\Unity Hub\Unity Hub.exe",
                    @"C:\Program Files\Unity Hub\UnityHub.exe"
                }
                : new[]
                {
                    "/usr/bin/unityhub",
                    "/opt/unityhub/unityhub"
                };
        var existing = candidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            executablePath = existing;
            return true;
        }

        executablePath = string.Empty;
        return false;
    }

    private static async Task<int> RunProcessStreamingAsync(
        string fileName,
        string arguments,
        Action<string> onLine,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                onLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                onLine(eventArgs.Data);
            }
        };

        if (!process.Start())
        {
            return -1;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);
        using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            var reason = cancellationToken.IsCancellationRequested
                ? "canceled by caller"
                : $"timed out after {(int)effectiveTimeout.TotalSeconds}s";
            onLine($"process termination requested: {reason}");
            return -1;
        }

        return process.ExitCode;
    }

    private static string? ResolveUnityHubModuleId(string displayName, string targetToken)
    {
        var normalized = NormalizeTarget(targetToken);
        if (normalized == NormalizeTarget("StandaloneWindows64") || NormalizeTarget(displayName) == NormalizeTarget("Win64"))
        {
            return "windows-il2cpp";
        }

        if (normalized == NormalizeTarget("Android"))
        {
            return "android";
        }

        if (normalized == NormalizeTarget("iOS"))
        {
            return "ios";
        }

        if (normalized == NormalizeTarget("WebGL"))
        {
            return "webgl";
        }

        if (normalized == NormalizeTarget("StandaloneOSX"))
        {
            return "mac-il2cpp";
        }

        if (normalized == NormalizeTarget("StandaloneLinux64"))
        {
            return "linux-il2cpp";
        }

        return null;
    }

    private async Task HandleLogsAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        _ = tokens;
        var unityReady = await RunWithSpinnerAsync(
            "Connecting to daemon...",
            () => EnsureUnityContextAsync(session, daemonControlService, daemonRuntime, requireUnityBridge: false));
        if (!unityReady)
        {
            log("[x] build logs failed: daemon/bridge is unavailable");
            return;
        }

        if (DaemonControlService.GetPort(session) is not int port)
        {
            log("[x] build logs failed: daemon is not attached");
            return;
        }

        var status = await RunWithSpinnerAsync("Fetching build log status...", () => _daemonClient.GetBuildStatusAsync(port));
        if (status is null || string.IsNullOrWhiteSpace(status.LogPath))
        {
            log("[yellow]build[/]: no build log is available yet");
            return;
        }

        BuildLogTailService.RunInteractive(status.LogPath!, "Build Logs");
    }

    private async Task MonitorBuildProgressAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log,
        bool promptDeploy)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            return;
        }

        var logOffset = 0L;
        var recentLogLines = new List<BuildLogLineDto>();
        var errorsOnly = false;
        var lastHeartbeatUtc = DateTime.MinValue;
        var lastStatusSnapshot = string.Empty;
        var statusUnchangedSince = DateTime.UtcNow;
        var lastRenderedFrameSignature = string.Empty;
        var lastRenderedAtUtc = DateTime.MinValue;
        var canceledByUser = false;
        DateTime? unreachableSinceUtc = null;

        while (true)
        {
            var status = await _daemonClient.GetBuildStatusAsync(port);
            if (status is null)
            {
                unreachableSinceUtc ??= DateTime.UtcNow;
                RenderBuildMonitorFrame(
                    null,
                    recentLogLines,
                    errorsOnly,
                    "daemon unreachable (Q/Esc: close monitor)",
                    null);

                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
                    {
                        session.ContextMode = CliContextMode.Project;
                        return;
                    }
                }

                if (DateTime.UtcNow - unreachableSinceUtc.Value > TimeSpan.FromSeconds(30))
                {
                    log("[yellow]build[/]: daemon remained unreachable for 30s; leaving build monitor");
                    session.ContextMode = CliContextMode.Project;
                    return;
                }

                await Task.Delay(1000);
                continue;
            }

            unreachableSinceUtc = null;

            if (DateTime.TryParse(status.LastHeartbeatUtc, out var parsedHeartbeat))
            {
                lastHeartbeatUtc = parsedHeartbeat.ToUniversalTime();
            }
            else
            {
                lastHeartbeatUtc = DateTime.UtcNow;
            }

            var snapshot = $"{status.Step}|{status.Progress01:0.000}|{status.Message}|{status.LastDiagnostic}";
            if (!snapshot.Equals(lastStatusSnapshot, StringComparison.Ordinal))
            {
                lastStatusSnapshot = snapshot;
                statusUnchangedSince = DateTime.UtcNow;
            }

            if (!string.IsNullOrWhiteSpace(status.LogPath))
            {
                var chunk = await _daemonClient.GetBuildLogChunkAsync(port, logOffset, 120, errorsOnly: false);
                if (chunk is not null)
                {
                    logOffset = chunk.NextOffset;
                    if (chunk.Lines.Count > 0)
                    {
                        recentLogLines.AddRange(chunk.Lines);
                        if (recentLogLines.Count > 40)
                        {
                            recentLogLines.RemoveRange(0, recentLogLines.Count - 40);
                        }
                    }
                }
            }

            var footer = status.Running
                ? "E: toggle error filter | C: cancel build | Q: hide monitor"
                : "Build finished";
            var elapsedNoStatusChange = DateTime.UtcNow - statusUnchangedSince;
            var heartbeatAge = DateTime.UtcNow - lastHeartbeatUtc;
            var stallHint = status.Running && (elapsedNoStatusChange > TimeSpan.FromSeconds(20) || heartbeatAge > TimeSpan.FromSeconds(20))
                ? $"No status change for {(int)elapsedNoStatusChange.TotalSeconds}s (heartbeat age {(int)heartbeatAge.TotalSeconds}s)"
                : null;
            var frameSignature = BuildMonitorFrameSignature(status, logOffset, errorsOnly, stallHint);
            var now = DateTime.UtcNow;
            var shouldRender = !frameSignature.Equals(lastRenderedFrameSignature, StringComparison.Ordinal)
                               || (now - lastRenderedAtUtc) >= BuildMonitorPeriodicRenderInterval
                               || !status.Running;
            if (shouldRender)
            {
                RenderBuildMonitorFrame(status, recentLogLines, errorsOnly, footer, stallHint);
                lastRenderedFrameSignature = frameSignature;
                lastRenderedAtUtc = now;
            }

            if (!status.Running)
            {
                break;
            }

            var delayUntil = DateTime.UtcNow.AddMilliseconds(500);
            while (DateTime.UtcNow < delayUntil)
            {
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(40);
                    continue;
                }

                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.E)
                {
                    errorsOnly = !errorsOnly;
                    break;
                }

                if (key.Key == ConsoleKey.C)
                {
                    canceledByUser = true;
                    await CancelBuildAndTeardownAsync(session, daemonControlService, daemonRuntime, log, promptReturnKey: true);
                    break;
                }

                if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
                {
                    break;
                }
            }

            if (canceledByUser)
            {
                session.ContextMode = CliContextMode.Project;
                return;
            }
        }

        var finalStatus = await _daemonClient.GetBuildStatusAsync(port);
        if (finalStatus is null)
        {
            log("[yellow]build[/]: finished, but final status could not be fetched");
            return;
        }

        var icon = finalStatus.Success ? "[green]success[/]" : "[red]failed[/]";
        log($"[grey]build[/]: status {icon} - {Markup.Escape(finalStatus.Message ?? string.Empty)}");
        if (!string.IsNullOrWhiteSpace(finalStatus.OutputPath))
        {
            var escapedPath = Markup.Escape(finalStatus.OutputPath!);
            var url = Uri.TryCreate(finalStatus.OutputPath, UriKind.Absolute, out var parsed)
                ? parsed.AbsoluteUri
                : $"file://{finalStatus.OutputPath.Replace(" ", "%20", StringComparison.Ordinal)}";
            log($"[grey]build[/]: output [link={Markup.Escape(url)}]{escapedPath}[/]");
        }
        if (!string.IsNullOrWhiteSpace(finalStatus.LogPath))
        {
            log($"[grey]build[/]: log file [white]{Markup.Escape(finalStatus.LogPath)}[/]");
        }

        if (!finalStatus.Success || !promptDeploy || string.IsNullOrWhiteSpace(finalStatus.OutputPath))
        {
            return;
        }

        if (!finalStatus.OutputPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        log("[grey]build[/]: Build successful. Deploy to connected ADB device? [Y/n]");
        var answer = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(answer) && answer.StartsWith("n", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TryRunAdbInstall(finalStatus.OutputPath, log);
    }

    private async Task CancelBuildAndTeardownAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log,
        bool promptReturnKey)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            log("[x] build cancel failed: daemon is not attached");
            return;
        }

        var beforeCancel = await RunWithSpinnerAsync(
            "Capturing pre-cancel status...",
            () => _daemonClient.GetBuildStatusAsync(port));
        var response = await RunWithSpinnerAsync(
            "Sending cancel signal...",
            () => ExecuteProjectCommandAsync(session, new ProjectCommandRequestDto("build-cancel", null, null, null)));
        EmitProjectResponse("build cancel", response, log);

        var tornDown = await RunWithSpinnerAsync(
            "Tearing down build daemon...",
            () => daemonControlService.StopDaemonByPortAsync(port, daemonRuntime, session, _ => { }));
        if (!tornDown)
        {
            log($"[x] build cancel failed: unable to teardown daemon port {port}");
        }
        else
        {
            log($"[yellow]build[/]: daemon port {port} terminated to tear down running build");
        }

        if (!string.IsNullOrWhiteSpace(beforeCancel?.LogPath) && File.Exists(beforeCancel.LogPath))
        {
            BuildLogTailService.ShowSnapshotAndWait(
                beforeCancel.LogPath,
                "Cancelled Build Log",
                promptReturnKey && !Console.IsInputRedirected);
        }
        else if (promptReturnKey && !Console.IsInputRedirected)
        {
            AnsiConsole.MarkupLine("[grey]build[/]: cancelled. Press any key to return to project mode.");
            _ = Console.ReadKey(intercept: true);
        }

        session.ContextMode = CliContextMode.Project;
    }

    private static void RenderBuildMonitorFrame(
        BuildStatusDto? status,
        IReadOnlyList<BuildLogLineDto> logLines,
        bool errorsOnly,
        string footer,
        string? stallHint)
    {
        AnsiConsole.Clear();
        var title = status is null
            ? "[bold deepskyblue1]Build Monitor[/]"
            : $"[bold deepskyblue1]Build Monitor[/] [grey]({Markup.Escape(status.Kind)})[/]";
        AnsiConsole.MarkupLine(title);

        if (status is null)
        {
            AnsiConsole.MarkupLine("[yellow]status[/]: waiting for daemon...");
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(footer)}[/]");
            return;
        }

        var progress = Math.Clamp(status.Progress01, 0f, 1f);
        var blocks = 30;
        var bar = TuiTrackableProgress.BuildProgressBar(progress, blocks);
        var step = string.IsNullOrWhiteSpace(status.Step) ? "building..." : status.Step;
        var state = status.Running ? "[yellow]running[/]" : (status.Success ? "[green]success[/]" : "[red]failed[/]");
        AnsiConsole.MarkupLine($"[grey]state[/]: {state}  [grey]step[/]: [white]{Markup.Escape(step)}[/]");
        AnsiConsole.MarkupLine($"[grey]progress[/]: [deepskyblue1]{Markup.Escape(bar)}[/] [white]{progress * 100:0}%[/]");
        AnsiConsole.MarkupLine($"[grey]message[/]: {Markup.Escape(status.Message ?? string.Empty)}");
        if (!string.IsNullOrWhiteSpace(status.LogPath))
        {
            AnsiConsole.MarkupLine($"[grey]log[/]: [white]{Markup.Escape(status.LogPath)}[/]");
        }
        if (!string.IsNullOrWhiteSpace(status.LastDiagnostic))
        {
            AnsiConsole.MarkupLine($"[grey]diagnostic[/]: {Markup.Escape(status.LastDiagnostic)}");
        }
        if (!string.IsNullOrWhiteSpace(status.LastException))
        {
            AnsiConsole.MarkupLine($"[red]exception[/]: {Markup.Escape(status.LastException)}");
        }
        if (!string.IsNullOrWhiteSpace(stallHint))
        {
            AnsiConsole.MarkupLine($"[yellow]stall[/]: {Markup.Escape(stallHint)}");
        }
        AnsiConsole.MarkupLine($"[dim]log filter: {(errorsOnly ? "errors only" : "all")}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Live Logs[/]");

        var visible = logLines
            .Where(line => !errorsOnly || line.Level.Equals("error", StringComparison.OrdinalIgnoreCase))
            .TakeLast(12)
            .ToList();
        if (visible.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]no log lines yet[/]");
        }
        else
        {
            foreach (var line in visible)
            {
                var style = line.Level.Equals("error", StringComparison.OrdinalIgnoreCase)
                    ? "red"
                    : (line.Level.Equals("warning", StringComparison.OrdinalIgnoreCase) ? "yellow" : "grey");
                AnsiConsole.MarkupLine($"[{style}]{Markup.Escape(line.Text)}[/]");
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(footer)}[/]");
    }

    private static string BuildMonitorFrameSignature(
        BuildStatusDto status,
        long logOffset,
        bool errorsOnly,
        string? stallHint)
    {
        return string.Join(
            '|',
            status.Running ? "1" : "0",
            status.Success ? "1" : "0",
            status.Progress01.ToString("0.000"),
            status.Kind ?? string.Empty,
            status.Step ?? string.Empty,
            status.Message ?? string.Empty,
            status.LastDiagnostic ?? string.Empty,
            status.LastException ?? string.Empty,
            status.LogPath ?? string.Empty,
            errorsOnly ? "1" : "0",
            stallHint ?? string.Empty,
            logOffset.ToString());
    }

    private static void TryRunAdbInstall(string apkPath, Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo("adb", $"install -r \"{apkPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                log("[x] adb deploy failed: unable to start adb process");
                return;
            }

            if (!process.WaitForExit((int)AdbInstallTimeout.TotalMilliseconds))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                log($"[x] adb deploy failed: timed out after {(int)AdbInstallTimeout.TotalSeconds}s");
                return;
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (process.ExitCode == 0)
            {
                log($"[green]adb[/]: {Markup.Escape(output.Trim())}");
                return;
            }

            log($"[x] adb deploy failed: {Markup.Escape(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim())}");
        }
        catch (Exception ex)
        {
            log($"[x] adb deploy failed: {Markup.Escape(ex.Message)}");
        }
    }

    private static void EmitProjectResponse(string actionLabel, ProjectCommandResponseDto response, Action<string> log)
    {
        if (!response.Ok)
        {
            log($"[x] {actionLabel} failed: {Markup.Escape(response.Message ?? "unknown error")}");
            return;
        }

        log($"[green]build[/]: {Markup.Escape(response.Message)}");
        if (string.IsNullOrWhiteSpace(response.Content))
        {
            return;
        }

        BuildOperationContentPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BuildOperationContentPayload>(response.Content, ReadJsonOptions);
        }
        catch
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(payload?.Summary))
        {
            log($"[grey]build[/]: {Markup.Escape(payload.Summary)}");
        }

        if (payload?.Details is null)
        {
            return;
        }

        foreach (var detail in payload.Details.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            log($"[grey]build[/]: {Markup.Escape(detail)}");
        }
    }

    private async Task<ProjectCommandResponseDto> ExecuteProjectCommandAsync(CliSessionState session, ProjectCommandRequestDto request)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            return new ProjectCommandResponseDto(false, "daemon is not attached", null, null);
        }

        return await _daemonClient.ExecuteProjectCommandAsync(port, request);
    }

    private static async Task<T> RunWithSpinnerAsync<T>(string statusText, Func<Task<T>> action)
    {
        if (Console.IsInputRedirected)
        {
            return await action();
        }

        var result = default(T);
        await AnsiConsole.Status()
            .Spinner(TuiTrackableProgress.StatusSpinner)
            .StartAsync(statusText, async _ =>
            {
                result = await action();
            });
        return result!;
    }

    private static async Task<bool> EnsureUnityContextAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        bool requireUnityBridge = false)
    {
        if (await daemonControlService.TouchAttachedDaemonAsync(session))
        {
            if (!requireUnityBridge)
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            return false;
        }

        if (!requireUnityBridge
            && DaemonControlService.IsUnityClientActiveForProject(session.CurrentProjectPath))
        {
            await daemonControlService.TryAttachProjectDaemonAsync(session.CurrentProjectPath, session);
            return true;
        }

        return await daemonControlService.EnsureProjectDaemonAsync(
            session.CurrentProjectPath,
            daemonRuntime,
            session,
            _ => { },
            requireUnityBridge);
    }

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private sealed record BuildRunRequestPayload(
        string Target,
        bool Development,
        bool ScriptDebugging,
        bool Clean,
        string? OutputPath);

    private sealed record BuildExecRequestPayload(string Method);

    private sealed record BuildAddressablesRequestPayload(bool Clean, bool Update);

    private sealed record BuildOperationContentPayload(string? Summary, List<string>? Details);

    private sealed record BuildTargetsResponsePayload(List<BuildTargetPayload>? Targets);

    private sealed record BuildTargetPayload(string? Name, bool Installed, string? Note);

    private sealed record BuildTargetCandidate(
        string DisplayName,
        string TargetToken,
        bool Installed,
        string? ModuleId,
        string? Note);

    private sealed record BuildScenesResponsePayload(List<BuildSceneEntryPayload>? Scenes);

    private sealed record BuildScenesUpdateRequestPayload(List<BuildSceneEntryPayload> Scenes);

    private sealed record BuildSceneEntryPayload(string Path, bool Enabled);
}
