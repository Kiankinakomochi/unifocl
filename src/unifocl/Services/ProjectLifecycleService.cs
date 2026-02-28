using Spectre.Console;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal sealed class ProjectLifecycleService
{
    private readonly EditorDependencyInitializerService _editorDependencyInitializerService = new();
    private readonly ProjectViewService _projectViewService = new();

    public async Task<bool> TryHandleLifecycleCommandAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        return matched.Trigger switch
        {
            "/open" => await HandleOpenAsync(input, matched, session, daemonControlService, daemonRuntime, log),
            "/new" => await HandleNewAsync(input, matched, session, daemonControlService, daemonRuntime, log),
            "/clone" => await HandleCloneAsync(input, matched, session, daemonControlService, daemonRuntime, log),
            "/init" => await HandleInitAsync(input, matched, session, log),
            _ => false
        };
    }

    private async Task<bool> HandleOpenAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (args.Count < 1)
        {
            log("[red]error[/]: usage /open <path>");
            return true;
        }

        var projectPath = ResolveAbsolutePath(args[0], Directory.GetCurrentDirectory());
        await TryOpenProjectAsync(
            projectPath,
            session,
            daemonControlService,
            daemonRuntime,
            _editorDependencyInitializerService,
            promptForInitialization: true,
            log);
        return true;
    }

    private async Task<bool> HandleNewAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (args.Count < 1)
        {
            log("[red]error[/]: usage /new <project-name> [unity-version]");
            return true;
        }

        var projectName = args[0].Trim();
        var unityVersion = args.Count > 1 ? args[1].Trim() : "6000.0.0f1";
        if (string.IsNullOrWhiteSpace(projectName))
        {
            log("[red]error[/]: project name cannot be empty");
            return true;
        }

        var projectPath = ResolveAbsolutePath(projectName, Directory.GetCurrentDirectory());
        log($"[grey]new[/]: step 1/5 create project directory -> [white]{Markup.Escape(projectPath)}[/]");

        if (Directory.Exists(projectPath) && Directory.EnumerateFileSystemEntries(projectPath).Any())
        {
            log("[red]error[/]: target directory already exists and is not empty");
            return true;
        }

        try
        {
            Directory.CreateDirectory(projectPath);
            Directory.CreateDirectory(Path.Combine(projectPath, "Assets"));
            Directory.CreateDirectory(Path.Combine(projectPath, "Packages"));
            Directory.CreateDirectory(Path.Combine(projectPath, "ProjectSettings"));
        }
        catch (Exception ex)
        {
            log($"[red]error[/]: failed to create Unity folder structure ({Markup.Escape(ex.Message)})");
            return true;
        }

        log("[grey]new[/]: step 2/5 write Unity package manifest");
        var manifestResult = WriteDefaultUnityManifest(projectPath);
        if (!manifestResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(manifestResult.Error)}");
            return true;
        }

        log($"[grey]new[/]: step 3/5 set Unity editor version [white]{Markup.Escape(unityVersion)}[/]");
        var versionResult = WriteProjectVersion(projectPath, unityVersion);
        if (!versionResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(versionResult.Error)}");
            return true;
        }

        log("[grey]new[/]: step 4/5 generate local templates and bridge config");
        var configResult = EnsureProjectLocalConfig(projectPath);
        if (!configResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(configResult.Error)}");
            return true;
        }

        log("[grey]new[/]: step 5/5 open bootstrapped project");
        if (await TryOpenProjectAsync(
                projectPath,
                session,
                daemonControlService,
                daemonRuntime,
                _editorDependencyInitializerService,
                promptForInitialization: true,
                log))
        {
            log("[green]new[/]: Unity project scaffold ready");
        }
        else
        {
            log("[yellow]new[/]: project scaffolded, but auto-open failed");
        }

        return true;
    }

    private async Task<bool> HandleCloneAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (args.Count < 1)
        {
            log("[red]error[/]: usage /clone <git-url>");
            return true;
        }

        var gitUrl = args[0].Trim();
        if (string.IsNullOrWhiteSpace(gitUrl))
        {
            log("[red]error[/]: git url cannot be empty");
            return true;
        }

        log("[grey]clone[/]: step 1/4 validate git binary");
        var gitVersion = RunProcess("git", "--version", Directory.GetCurrentDirectory());
        if (gitVersion.ExitCode != 0)
        {
            log("[red]error[/]: git is not available on PATH");
            return true;
        }

        var targetFolderName = GuessRepoFolderName(gitUrl);
        var targetPath = Path.Combine(Directory.GetCurrentDirectory(), targetFolderName);
        log($"[grey]clone[/]: step 2/4 clone [white]{Markup.Escape(gitUrl)}[/] -> [white]{Markup.Escape(targetPath)}[/]");

        if (Directory.Exists(targetPath))
        {
            log("[red]error[/]: clone target already exists");
            return true;
        }

        var cloneResult = RunProcess("git", $"clone \"{gitUrl}\" \"{targetPath}\"", Directory.GetCurrentDirectory());
        if (cloneResult.ExitCode != 0)
        {
            log($"[red]error[/]: git clone failed ({Markup.Escape(SummarizeProcessError(cloneResult))})");
            return true;
        }

        log("[grey]clone[/]: step 3/4 write local templates and bridge config");
        var configResult = EnsureProjectLocalConfig(targetPath);
        if (!configResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(configResult.Error)}");
            return true;
        }

        log("[grey]clone[/]: step 4/4 open cloned project");
        if (await TryOpenProjectAsync(
                targetPath,
                session,
                daemonControlService,
                daemonRuntime,
                _editorDependencyInitializerService,
                promptForInitialization: true,
                log))
        {
            log("[green]clone[/]: repository cloned and prepared");
        }
        else
        {
            log("[yellow]clone[/]: repository cloned; open skipped (not a Unity project yet)");
        }

        return true;
    }

    private Task<bool> HandleInitAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (args.Count > 1)
        {
            log("[red]error[/]: usage /init [path-to-project]");
            return Task.FromResult(true);
        }

        string? targetPath = null;
        if (args.Count == 1)
        {
            targetPath = ResolveAbsolutePath(args[0], Directory.GetCurrentDirectory());
        }
        else if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            targetPath = session.CurrentProjectPath;
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            log("[red]error[/]: no attached project; use /init <path-to-project> or /open first");
            return Task.FromResult(true);
        }

        var initResult = _editorDependencyInitializerService.InitializeProject(targetPath, log);
        if (!initResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(initResult.Error)}");
            return Task.FromResult(true);
        }

        log($"[green]init[/]: ready at [white]{Markup.Escape(targetPath)}[/]");
        return Task.FromResult(true);
    }

    private async Task<bool> TryOpenProjectAsync(
        string projectPath,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        EditorDependencyInitializerService editorDependencyInitializerService,
        bool promptForInitialization,
        Action<string> log)
    {
        log($"[grey]open[/]: step 1/4 resolve project path -> [white]{Markup.Escape(projectPath)}[/]");

        if (!Directory.Exists(projectPath))
        {
            log("[red]error[/]: project directory does not exist");
            return false;
        }

        if (!LooksLikeUnityProject(projectPath))
        {
            log("[red]error[/]: path is not a Unity project (missing Assets/ or ProjectSettings/)");
            return false;
        }

        log("[grey]open[/]: step 2/4 validate Unity project layout");
        var bridgeResult = EnsureProjectLocalConfig(projectPath);
        if (!bridgeResult.Ok)
        {
            log($"[red]error[/]: {Markup.Escape(bridgeResult.Error)}");
            return false;
        }

        if (promptForInitialization)
        {
            if (editorDependencyInitializerService.NeedsInitialization(projectPath, out var initReason))
            {
                log($"[yellow]init[/]: editor bridge dependency is missing or invalid ({Markup.Escape(initReason)}).");
                if (editorDependencyInitializerService.PromptForInitialization(log))
                {
                    var initResult = editorDependencyInitializerService.InitializeProject(projectPath, log);
                    if (!initResult.Ok)
                    {
                        log($"[red]error[/]: {Markup.Escape(initResult.Error)}");
                        return false;
                    }
                }
                else
                {
                    log("[yellow]init[/]: skipped; run /init to enable editor-side bridge package");
                }
            }
            else
            {
                log("[grey]init[/]: editor bridge dependency already installed");
            }
        }

        log("[grey]open[/]: step 3/4 route runtime by active Unity client lock");
        var daemonPort = DaemonControlService.ComputeProjectDaemonPort(projectPath);
        if (DaemonControlService.IsUnityClientActiveForProject(projectPath))
        {
            session.AttachedPort = null;
            SaveDaemonSession(projectPath, new DaemonSessionInfo(daemonPort, DateTimeOffset.UtcNow, false));
            log("[grey]daemon[/]: Unity editor lock detected; routing operations via InitializeOnLoad bridge");
        }
        else
        {
            var started = await daemonControlService.EnsureProjectDaemonAsync(projectPath, daemonRuntime, session, log);
            if (!started)
            {
                log("[red]daemon[/]: failed to start or attach headless daemon");
                return false;
            }

            SaveDaemonSession(projectPath, new DaemonSessionInfo(daemonPort, DateTimeOffset.UtcNow, true));
            log($"[grey]daemon[/]: started headless daemon on [white]127.0.0.1:{daemonPort}[/]");
        }

        session.CurrentProjectPath = projectPath;
        session.Mode = CliMode.Project;
        session.LastOpenedUtc = DateTimeOffset.UtcNow;
        log("[grey]open[/]: step 4/4 load project context");
        _projectViewService.OpenInitialView(session, log);
        return true;
    }

    private static void SaveDaemonSession(string projectPath, DaemonSessionInfo session)
    {
        var daemonDir = Path.Combine(projectPath, ".unifocl");
        Directory.CreateDirectory(daemonDir);
        var daemonPath = Path.Combine(daemonDir, "daemon.session.json");
        File.WriteAllText(
            daemonPath,
            JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static List<string> ParseCommandArgs(string input, string trigger)
    {
        var raw = input.Length > trigger.Length ? input[trigger.Length..].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];

            if (c == '\\' && i + 1 < raw.Length && raw[i + 1] == '"')
            {
                current.Append('"');
                i++;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string ResolveAbsolutePath(string path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return baseDirectory;
        }

        if (path.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var suffix = path[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(home, suffix));
        }

        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
    }

    private static bool LooksLikeUnityProject(string projectPath)
    {
        return Directory.Exists(Path.Combine(projectPath, "Assets"))
               && Directory.Exists(Path.Combine(projectPath, "ProjectSettings"));
    }

    private static OperationResult EnsureProjectLocalConfig(string projectPath)
    {
        try
        {
            var templatesPath = Path.Combine(projectPath, "templates.json");
            if (!File.Exists(templatesPath))
            {
                var templates = JsonSerializer.Serialize(new
                {
                    templates = new Dictionary<string, string>
                    {
                        ["script"] = "Assets/Scripts/NewScript.cs",
                        ["shader"] = "Assets/Shaders/NewShader.shader",
                        ["material"] = "Assets/Materials/NewMaterial.mat"
                    }
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(templatesPath, templates + Environment.NewLine);
            }

            var bridgeDir = Path.Combine(projectPath, ".unifocl");
            Directory.CreateDirectory(bridgeDir);

            var bridgePath = Path.Combine(bridgeDir, "bridge.json");
            if (!File.Exists(bridgePath))
            {
                var bridge = JsonSerializer.Serialize(new
                {
                    projectPath,
                    daemon = new { host = "127.0.0.1", port = 18080 },
                    protocol = "v1",
                    updatedAtUtc = DateTimeOffset.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(bridgePath, bridge + Environment.NewLine);
            }

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to write local config ({ex.Message})");
        }
    }

    private static OperationResult WriteDefaultUnityManifest(string projectPath)
    {
        try
        {
            var manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
            if (!File.Exists(manifestPath))
            {
                var manifest = JsonSerializer.Serialize(new
                {
                    dependencies = new Dictionary<string, string>
                    {
                        ["com.unity.collab-proxy"] = "2.7.2",
                        ["com.unity.ide.rider"] = "3.0.35",
                        ["com.unity.ide.visualstudio"] = "2.0.24",
                        ["com.unity.test-framework"] = "1.4.5",
                        ["com.unity.timeline"] = "1.8.9",
                        ["com.unity.ugui"] = "1.0.0"
                    }
                }, new JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(manifestPath, manifest + Environment.NewLine);
            }

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to write Packages/manifest.json ({ex.Message})");
        }
    }

    private static OperationResult WriteProjectVersion(string projectPath, string unityVersion)
    {
        try
        {
            var projectVersionPath = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
            var content =
                $"m_EditorVersion: {unityVersion}{Environment.NewLine}m_EditorVersionWithRevision: {unityVersion} (placeholder){Environment.NewLine}";
            File.WriteAllText(projectVersionPath, content);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to write ProjectVersion.txt ({ex.Message})");
        }
    }

    private static ProcessResult RunProcess(string fileName, string arguments, string workingDirectory)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ProcessResult(-1, string.Empty, "failed to start process");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message);
        }
    }

    private static string GuessRepoFolderName(string gitUrl)
    {
        var trimmed = gitUrl.TrimEnd('/');
        var lastSegment = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        if (lastSegment.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            lastSegment = lastSegment[..^4];
        }

        return string.IsNullOrWhiteSpace(lastSegment) ? "cloned-project" : lastSegment;
    }

    private static string SummarizeProcessError(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        if (string.IsNullOrWhiteSpace(text))
        {
            return $"exit code {result.ExitCode}";
        }

        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return firstLine is null ? $"exit code {result.ExitCode}" : firstLine;
    }
}
