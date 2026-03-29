using Spectre.Console;
using System.Runtime.InteropServices;
using System.Text.Json;

internal sealed partial class ProjectLifecycleService
{
    private Task<bool> HandleUnityDetectAsync(Action<string> log)
    {
        var editors = UnityEditorPathService.DetectInstalledEditors(out var hubRoot);
        if (editors.Count == 0)
        {
            log("[yellow]unity[/]: no installed editors detected");
            return Task.FromResult(true);
        }

        if (!string.IsNullOrWhiteSpace(hubRoot))
        {
            log($"[grey]unity[/]: Hub root [white]{Markup.Escape(hubRoot)}[/]");
        }

        log("[grey]unity[/]: detected installed editors");
        foreach (var editor in editors)
        {
            log($"[grey]unity[/]: [white]{Markup.Escape(editor.Version)}[/] -> [white]{Markup.Escape(editor.EditorPath)}[/]");
        }

        return Task.FromResult(true);
    }

    private Task<bool> HandleUnitySetAsync(string input, CommandSpec matched, Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (args.Count != 1)
        {
            log("[red]error[/]: usage /unity set <path>");
            return Task.FromResult(true);
        }

        var unityPath = ResolveAbsolutePath(args[0], Directory.GetCurrentDirectory());
        if (!UnityEditorPathService.TrySetDefaultEditorPath(unityPath, out var saveError))
        {
            log($"[red]error[/]: {Markup.Escape(saveError ?? "failed to save default Unity editor path")}");
            return Task.FromResult(true);
        }

        Environment.SetEnvironmentVariable("UNITY_PATH", unityPath);
        log($"[green]unity[/]: default editor set -> [white]{Markup.Escape(unityPath)}[/]");
        return Task.FromResult(true);
    }

    private static Task<bool> HandleHelpAsync(string input, CommandSpec matched, Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        var topic = args.Count == 0 ? "root" : args[0].Trim().ToLowerInvariant();

        switch (topic)
        {
            case "root":
            case "general":
                LogHelpSection("root commands", CliCommandCatalog.CreateRootCommands(), log);
                log("[grey]help[/]: topics -> project | inspector | build | upm | daemon");
                log("[grey]help[/]: usage  -> /help <topic>");
                return Task.FromResult(true);
            case "project":
                LogHelpSection("project mode commands", CliCommandCatalog.CreateProjectCommands(), log);
                return Task.FromResult(true);
            case "inspector":
                LogHelpSection("inspector mode commands", CliCommandCatalog.CreateInspectorCommands(), log);
                return Task.FromResult(true);
            case "build":
                LogHelpSection(
                    "build commands",
                    CliCommandCatalog.CreateRootCommands().Where(command => command.Trigger.StartsWith("/build", StringComparison.Ordinal)).ToList(),
                    log);
                return Task.FromResult(true);
            case "upm":
                LogHelpSection(
                    "upm commands",
                    CliCommandCatalog.CreateRootCommands().Where(command => command.Trigger.StartsWith("/upm", StringComparison.Ordinal)).ToList(),
                    log);
                return Task.FromResult(true);
            case "daemon":
                LogHelpSection(
                    "daemon commands",
                    CliCommandCatalog.CreateRootCommands().Where(command => command.Trigger.StartsWith("/daemon", StringComparison.Ordinal)).ToList(),
                    log);
                return Task.FromResult(true);
            default:
                log($"[yellow]help[/]: unknown topic [white]{Markup.Escape(topic)}[/]");
                log("[grey]help[/]: available topics -> root | project | inspector | build | upm | daemon");
                return Task.FromResult(true);
        }
    }

    private static Task<bool> HandleStatusAsync(
        CliSessionState session,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        log("[grey]status[/]: session");
        log($"[grey]status[/]: mode={session.Mode}, context={session.ContextMode}");
        log($"[grey]status[/]: attached-port={(DaemonControlService.GetPort(session)?.ToString() ?? "none")}");
        log($"[grey]status[/]: project={(string.IsNullOrWhiteSpace(session.CurrentProjectPath) ? "none" : session.CurrentProjectPath)}");
        log($"[grey]status[/]: safe-mode={(session.SafeModeEnabled ? "on" : "off")}");
        if (session.LastOpenedUtc is DateTimeOffset openedAtUtc)
        {
            log($"[grey]status[/]: last-opened={openedAtUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss zzz}");
        }

        var daemons = daemonRuntime.GetAll()
            .OrderBy(instance => instance.Port)
            .ToList();
        if (daemons.Count == 0)
        {
            log("[grey]status[/]: daemon runtime -> no active daemon entries");
            return Task.FromResult(true);
        }

        log($"[grey]status[/]: daemon runtime -> {daemons.Count} active instance(s)");
        foreach (var daemon in daemons)
        {
            var uptime = DateTime.UtcNow - daemon.StartedAtUtc;
            var mode = daemon.Headless ? "host" : "bridge";
            var project = string.IsNullOrWhiteSpace(daemon.ProjectPath) ? "-" : daemon.ProjectPath;
            log($"[grey]status[/]: port={daemon.Port} pid={daemon.Pid} mode={mode} uptime={uptime.TotalMinutes:0}m project={Markup.Escape(project)}");
        }

        return Task.FromResult(true);
    }

    private static Task<bool> HandleExamplesAsync(Action<string> log)
    {
        log("[grey]examples[/]: common workflows");
        log("[grey]examples[/]: open project -> /open ./MyUnityProject");
        log("[grey]examples[/]: attach daemon -> /daemon attach 8080");
        log("[grey]examples[/]: view status -> /status");
        log("[grey]examples[/]: project commands -> mk script --name PlayerController");
        log("[grey]examples[/]: package management -> /upm list --outdated");
        log("[grey]examples[/]: build run -> /build run Android --dev");
        log("[grey]examples[/]: build logs -> /build logs");
        log("[grey]examples[/]: inspect object -> /inspect /Player");
        return Task.FromResult(true);
    }

    private static async Task<bool> HandleUpdateAsync(Action<string> log)
    {
        log($"[grey]update[/]: installed version -> [white]{Markup.Escape(CliVersion.SemVer)}[/]");

        if (!TryResolveCurrentPlatformUpdateSpec(out var platformSpec, out var platformError))
        {
            log($"[red]error[/]: {Markup.Escape(platformError)}");
            return true;
        }

        log($"[grey]update[/]: target platform -> [white]{Markup.Escape(platformSpec.Label)}[/]");
        var fetchReleaseResult = await TryFetchLatestGitHubReleaseAsync();
        if (!fetchReleaseResult.Ok || fetchReleaseResult.Release is null)
        {
            log($"[red]error[/]: update check failed ({Markup.Escape(fetchReleaseResult.Error)})");
            return true;
        }

        var release = fetchReleaseResult.Release;
        var releaseVersion = release.TagName.TrimStart('v');
        log($"[grey]update[/]: latest release -> [white]{Markup.Escape(release.TagName)}[/]");

        if (TryParseComparableSemVer(CliVersion.SemVer, out var installedVersion)
            && TryParseComparableSemVer(releaseVersion, out var latestVersion)
            && latestVersion <= installedVersion)
        {
            log("[green]update[/]: already on the latest release");
            return true;
        }

        var asset = release.Assets.FirstOrDefault(candidate =>
            candidate.Name.EndsWith(platformSpec.AssetSuffix, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            log($"[red]error[/]: no release asset found for {Markup.Escape(platformSpec.Label)}");
            log("[grey]update[/]: available assets");
            foreach (var candidate in release.Assets)
            {
                log($"[grey]  -[/] {Markup.Escape(candidate.Name)}");
            }

            return true;
        }

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "unifocl-update",
            releaseVersion,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var archivePath = Path.Combine(tempRoot, asset.Name);
        var extractDirectory = Path.Combine(tempRoot, "extracted");
        Directory.CreateDirectory(extractDirectory);

        log($"[grey]update[/]: downloading [white]{Markup.Escape(asset.Name)}[/]");
        var downloadResult = await DownloadReleaseAssetAsync(asset.DownloadUrl, archivePath);
        if (!downloadResult.Ok)
        {
            log($"[red]error[/]: failed to download release asset ({Markup.Escape(downloadResult.Error)})");
            return true;
        }

        var extractResult = await ExtractReleaseArchiveAsync(archivePath, extractDirectory, platformSpec.ArchiveType);
        if (!extractResult.Ok)
        {
            log($"[red]error[/]: failed to extract release asset ({Markup.Escape(extractResult.Error)})");
            return true;
        }

        var extractedExecutablePath = FindExtractedExecutablePath(extractDirectory, platformSpec.ExecutableName);
        if (string.IsNullOrWhiteSpace(extractedExecutablePath))
        {
            log("[red]error[/]: extracted archive did not include an executable payload");
            return true;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            var stagedPath = StageDownloadedExecutableForManualInstall(
                extractedExecutablePath,
                releaseVersion,
                platformSpec.ExecutableName,
                Directory.GetCurrentDirectory());
            log("[yellow]update[/]: current executable path is unavailable (likely dotnet run/dev mode)");
            log($"[green]update[/]: downloaded latest binary -> [white]{Markup.Escape(stagedPath)}[/]");
            return true;
        }

        var processDirectory = Path.GetDirectoryName(processPath) ?? Directory.GetCurrentDirectory();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var stagedPath = StageDownloadedExecutableForManualInstall(
                extractedExecutablePath,
                releaseVersion,
                platformSpec.ExecutableName,
                processDirectory);
            if (await TrySpawnWingetUpgradeAsync(log))
            {
                return true;
            }

            if (TrySpawnDeferredWindowsSwap(stagedPath, processPath, log))
            {
                log($"[green]update[/]: downloaded [white]{Markup.Escape(asset.Name)}[/] — swap queued");
                log("[grey]update[/]: the binary will be replaced automatically when you quit unifocl");
            }
            else
            {
                log($"[green]update[/]: downloaded latest binary -> [white]{Markup.Escape(stagedPath)}[/]");
                log($"[grey]update[/]: replace [white]{Markup.Escape(processPath)}[/] with staged binary after quitting unifocl");
            }

            return true;
        }

        try
        {
            File.Copy(extractedExecutablePath, processPath, overwrite: true);
            TryApplyUnixExecutableMode(processPath);
            log($"[green]update[/]: updated executable in place -> [white]{Markup.Escape(processPath)}[/]");
            log("[grey]update[/]: restart unifocl to use the new version");
            return true;
        }
        catch (Exception ex)
        {
            var stagedPath = StageDownloadedExecutableForManualInstall(
                extractedExecutablePath,
                releaseVersion,
                platformSpec.ExecutableName,
                processDirectory);
            log($"[yellow]update[/]: in-place replacement failed ({Markup.Escape(ex.Message)})");
            log($"[green]update[/]: downloaded latest binary -> [white]{Markup.Escape(stagedPath)}[/]");
            log($"[grey]update[/]: replace [white]{Markup.Escape(processPath)}[/] with staged binary after quitting unifocl");
            return true;
        }
    }

    private Task<bool> HandleInstallHookAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var targetPath = !string.IsNullOrWhiteSpace(session.CurrentProjectPath)
            ? session.CurrentProjectPath!
            : Directory.GetCurrentDirectory();
        if (!LooksLikeUnityProject(targetPath))
        {
            log("[red]error[/]: /install-hook requires an open Unity project or running from a Unity project root");
            return Task.FromResult(true);
        }

        log($"[grey]install-hook[/]: initializing bridge dependencies at [white]{Markup.Escape(targetPath)}[/]");
        return HandleInitAsync(
            $"/init \"{targetPath}\"",
            new CommandSpec("/init", "Initialize bridge", "/init"),
            session,
            daemonControlService,
            daemonRuntime,
            log);
    }

    private static async Task<bool> HandleDoctorAsync(
        CliSessionState session,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var dotnet = await RunProcessAsync("dotnet", "--version", Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(8));
        if (dotnet.ExitCode == 0)
        {
            var version = dotnet.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "unknown";
            log($"[green]doctor[/]: dotnet ok ({Markup.Escape(version)})");
        }
        else
        {
            log("[red]doctor[/]: dotnet not available on PATH");
        }

        var git = await RunProcessAsync("git", "--version", Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(8));
        if (git.ExitCode == 0)
        {
            var version = git.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "unknown";
            log($"[green]doctor[/]: git ok ({Markup.Escape(version)})");
        }
        else
        {
            log("[red]doctor[/]: git not available on PATH");
        }

        var editors = UnityEditorPathService.DetectInstalledEditors(out var hubRoot);
        if (editors.Count == 0)
        {
            log("[yellow]doctor[/]: Unity editors were not detected");
        }
        else
        {
            var hubLabel = string.IsNullOrWhiteSpace(hubRoot) ? "unknown" : hubRoot;
            log($"[green]doctor[/]: Unity editor detection ok ({editors.Count} found; hub={Markup.Escape(hubLabel)})");
        }

        if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            var projectValid = LooksLikeUnityProject(session.CurrentProjectPath);
            log(projectValid
                ? $"[green]doctor[/]: project layout ok ({Markup.Escape(session.CurrentProjectPath)})"
                : $"[red]doctor[/]: project layout invalid ({Markup.Escape(session.CurrentProjectPath)})");
        }

        var daemonCount = daemonRuntime.GetAll().Count();
        log($"[grey]doctor[/]: active daemon entries={daemonCount}");
        return true;
    }

    private static Task<bool> HandleScanAsync(
        string input,
        CommandSpec matched,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (!TryParseScanArgs(args, out var rootPathRaw, out var depth, out var parseError))
        {
            log($"[red]error[/]: {Markup.Escape(parseError)}");
            return Task.FromResult(true);
        }

        var rootPath = ResolveAbsolutePath(rootPathRaw, Directory.GetCurrentDirectory());
        if (!Directory.Exists(rootPath))
        {
            log($"[red]error[/]: scan root not found -> {Markup.Escape(rootPath)}");
            return Task.FromResult(true);
        }

        var matches = ScanUnityProjects(rootPath, depth);
        log($"[grey]scan[/]: root={Markup.Escape(rootPath)} depth={depth} matches={matches.Count}");
        if (matches.Count == 0)
        {
            return Task.FromResult(true);
        }

        for (var i = 0; i < matches.Count; i++)
        {
            log($"[grey]scan[/]: [white]{i + 1}[/]. {Markup.Escape(matches[i])}");
        }

        return Task.FromResult(true);
    }

    private static Task<bool> HandleInfoAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        var targetPath = args.Count switch
        {
            0 when !string.IsNullOrWhiteSpace(session.CurrentProjectPath) => session.CurrentProjectPath!,
            0 => Directory.GetCurrentDirectory(),
            1 => ResolveAbsolutePath(args[0], Directory.GetCurrentDirectory()),
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            log("[red]error[/]: usage /info <path>");
            return Task.FromResult(true);
        }

        if (!Directory.Exists(targetPath))
        {
            log($"[red]error[/]: path not found -> {Markup.Escape(targetPath)}");
            return Task.FromResult(true);
        }

        var unityProject = LooksLikeUnityProject(targetPath);
        log($"[grey]info[/]: path={Markup.Escape(targetPath)}");
        log($"[grey]info[/]: unity-project={(unityProject ? "yes" : "no")}");
        if (!unityProject)
        {
            return Task.FromResult(true);
        }

        var versionPath = Path.Combine(targetPath, "ProjectSettings", "ProjectVersion.txt");
        var editorVersion = TryReadEditorVersion(versionPath);
        if (!string.IsNullOrWhiteSpace(editorVersion))
        {
            log($"[grey]info[/]: unity-version={Markup.Escape(editorVersion)}");
        }
        else
        {
            log("[grey]info[/]: unity-version=unknown");
        }

        if (UnityEditorPathService.TryResolveEditorForProject(targetPath, out var resolvedEditorPath, out var resolvedEditorVersion, out _))
        {
            log($"[grey]info[/]: unity-editor-match={Markup.Escape(resolvedEditorVersion)}");
            log($"[grey]info[/]: unity-editor-path={Markup.Escape(resolvedEditorPath)}");
        }
        else
        {
            log("[yellow]info[/]: unity-editor-match=not found");
        }

        var daemonPort = DaemonControlService.ResolveProjectDaemonPort(targetPath);
        log($"[grey]info[/]: default-daemon-port={daemonPort}");

        if (TryGetProjectBridgeProtocol(targetPath, out var protocol))
        {
            log($"[grey]info[/]: bridge-protocol={Markup.Escape(protocol ?? "-")}");
        }
        else
        {
            log("[grey]info[/]: bridge-protocol=not configured");
        }

        var manifestPath = Path.Combine(targetPath, "Packages", "manifest.json");
        var dependencyCount = TryCountManifestDependencies(manifestPath);
        if (dependencyCount >= 0)
        {
            log($"[grey]info[/]: package-dependencies={dependencyCount}");
        }

        return Task.FromResult(true);
    }

    private static Task<bool> HandleLogsAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        var topic = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "daemon";
        var follow = args.Any(arg => arg.Equals("-f", StringComparison.OrdinalIgnoreCase)
                                     || arg.Equals("--follow", StringComparison.OrdinalIgnoreCase));
        if (topic.Equals("daemon", StringComparison.OrdinalIgnoreCase))
        {
            var daemons = daemonRuntime.GetAll()
                .OrderBy(instance => instance.Port)
                .ToList();
            if (daemons.Count == 0)
            {
                log("[grey]logs[/]: no active daemon runtime entries");
                return Task.FromResult(true);
            }

            log($"[grey]logs[/]: daemon entries ({daemons.Count})");
            foreach (var daemon in daemons)
            {
                var heartbeatAge = DateTime.UtcNow - daemon.LastHeartbeatUtc;
                log($"[grey]logs[/]: port={daemon.Port} pid={daemon.Pid} heartbeat-age={heartbeatAge.TotalSeconds:0}s project={Markup.Escape(daemon.ProjectPath ?? "-")}");
            }

            if (follow)
            {
                log("[yellow]logs[/]: follow mode is not implemented for daemon process output in this runtime");
            }

            return Task.FromResult(true);
        }

        if (!topic.Equals("unity", StringComparison.OrdinalIgnoreCase))
        {
            log("[red]error[/]: usage /logs [daemon|unity] [-f]");
            return Task.FromResult(true);
        }

        if (session.UnityLogPane.Count == 0)
        {
            log("[grey]logs[/]: unity log pane is empty");
            return Task.FromResult(true);
        }

        var lines = session.UnityLogPane.TakeLast(60).ToList();
        log($"[grey]logs[/]: unity log pane tail ({lines.Count} lines)");
        foreach (var line in lines)
        {
            log(Markup.Escape(line));
        }

        if (follow)
        {
            log("[yellow]logs[/]: follow mode is not implemented for cached Unity log pane");
        }

        return Task.FromResult(true);
    }

    private static bool TryParseScanArgs(
        IReadOnlyList<string> args,
        out string rootPath,
        out int depth,
        out string error)
    {
        rootPath = ".";
        depth = 3;
        error = string.Empty;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--root", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                {
                    error = "usage /scan [--root <dir>] [--depth <n>]";
                    return false;
                }

                rootPath = args[++i];
                continue;
            }

            if (arg.Equals("--depth", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || !int.TryParse(args[++i], out depth) || depth < 0 || depth > 12)
                {
                    error = "--depth must be an integer between 0 and 12";
                    return false;
                }

                continue;
            }

            error = $"unrecognized option {arg}; usage /scan [--root <dir>] [--depth <n>]";
            return false;
        }

        return true;
    }

    private static List<string> ScanUnityProjects(string rootPath, int maxDepth)
    {
        var matches = new List<string>();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((rootPath, 0));
        while (pending.Count > 0)
        {
            var (currentPath, depth) = pending.Dequeue();
            if (LooksLikeUnityProject(currentPath))
            {
                matches.Add(currentPath);
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(currentPath);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                pending.Enqueue((child, depth + 1));
            }
        }

        return matches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? TryReadEditorVersion(string projectVersionPath)
    {
        if (!File.Exists(projectVersionPath))
        {
            return null;
        }

        foreach (var line in File.ReadLines(projectVersionPath))
        {
            const string prefix = "m_EditorVersion:";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            return line[prefix.Length..].Trim();
        }

        return null;
    }

    private static int TryCountManifestDependencies(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return -1;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("dependencies", out var deps)
                || deps.ValueKind != JsonValueKind.Object)
            {
                return -1;
            }

            return deps.EnumerateObject().Count();
        }
        catch
        {
            return -1;
        }
    }

    private static void LogHelpSection(string title, IReadOnlyList<CommandSpec> commands, Action<string> log)
    {
        log($"[grey]help[/]: {title}");
        var deduped = commands
            .GroupBy(command => command.Signature, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(command => command.Signature, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var command in deduped)
        {
            var signature = command.Signature.Replace('[', '(').Replace(']', ')');
            log($"[grey]help[/]: [white]{Markup.Escape(signature)}[/] - {Markup.Escape(command.Description)}");
        }
    }
}
