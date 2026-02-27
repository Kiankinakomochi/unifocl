using Spectre.Console;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

var commands = new List<CommandSpec>
{
    // Project/session lifecycle
    new("/open <path>", "Open project (starts/attaches daemon, loads project)", "/open"),
    new("/new <project-name> [unity-version]", "Bootstrap a new Unity project", "/new"),
    new("/clone <git-url>", "Clone repo and set local CLI bridge config", "/clone"),
    new("/recent [n]", "List recently opened projects", "/recent"),
    new("/close", "Detach from current project (return to boot)", "/close"),
    new("/switch <recent-index|path>", "Convenience wrapper for recent + open", "/switch"),
    new("/status", "Show daemon/editor/project/session status", "/status"),
    new("/doctor", "Run diagnostics for environment and tooling", "/doctor"),
    new("/logs [daemon|unity] [-f]", "Tail or follow daemon/unity logs", "/logs"),

    // Daemon control
    new("/daemon start [--port 8080] [--unity <path>] [--headless]", "Start always-warm daemon", "/daemon start"),
    new("/daemon stop", "Stop daemon", "/daemon stop"),
    new("/daemon restart", "Restart daemon", "/daemon restart"),
    new("/daemon ps", "Show instances, ports, uptime, project", "/daemon ps"),
    new("/daemon attach <port>", "Attach CLI to existing daemon", "/daemon attach"),
    new("/daemon detach", "Detach CLI and keep daemon alive", "/daemon detach"),

    // Discovery
    new("/scan [--root <dir>] [--depth n]", "Find Unity projects under a directory", "/scan"),
    new("/info <path>", "Read project metadata (Unity version/name/paths)", "/info"),
    new("/unity detect", "List installed Unity editors", "/unity detect"),
    new("/unity set <path>", "Set default Unity editor path", "/unity set"),

    // Configuration
    new("/config get <key>", "Read configuration value", "/config get"),
    new("/config set <key> <value>", "Write configuration value", "/config set"),
    new("/config list", "List current configuration", "/config list"),
    new("/config reset", "Reset configuration to defaults", "/config reset"),

    // Onboarding
    new("/init", "Run first-run setup wizard", "/init"),
    new("/install-hook", "Install/validate Unity editor bridge", "/install-hook"),
    new("/help [topic]", "Show help by topic", "/help"),
    new("/examples", "Show common next-step flows", "/examples"),
    new("/keybinds", "Show modal keybinds/shortcuts", "/keybinds"),
    new("/shortcuts", "Alias for keybinds", "/shortcuts"),

    // Utilities
    new("/update", "Check for CLI updates", "/update"),
    new("/version", "Show CLI and protocol version", "/version"),
    new("/protocol", "Show supported JSON schema capabilities", "/protocol"),
    new("/exit", "Exit unifocl", "/exit"),
    new("/clear", "Clear and redraw boot screen", "/clear")
};

var streamLog = new List<string>();
var session = new SessionState();
SeedBootLog(streamLog);
RenderInitialLog(streamLog);

while (true)
{
    var rawInput = ReadInput(commands, streamLog);
    if (rawInput is null)
    {
        AnsiConsole.MarkupLine("[grey]Input stream closed. Session ended.[/]");
        return;
    }

    var input = rawInput.Trim();
    if (string.IsNullOrWhiteSpace(input))
    {
        continue;
    }

    if (!input.StartsWith('/'))
    {
        AppendLog(streamLog, "[grey]system[/]: boot mode is slash-first; type / to see available commands");
        continue;
    }

    if (input == "/")
    {
        continue;
    }

    var matched = MatchCommand(input, commands);
    if (matched is null)
    {
        AppendLog(streamLog, $"[grey]system[/]: unknown command [white]{Markup.Escape(input)}[/]");
        continue;
    }

    if (matched.Trigger == "/exit")
    {
        AnsiConsole.MarkupLine("[grey]Session closed.[/]");
        return;
    }

    if (matched.Trigger == "/clear")
    {
        streamLog.Clear();
        SeedBootLog(streamLog);
        AnsiConsole.Clear();
        RenderInitialLog(streamLog);
        continue;
    }

    AppendLog(streamLog, $"[bold deepskyblue1]unifocl[/] [grey]>[/] [white]{Markup.Escape(input)}[/]");
    if (TryHandleLifecycleCommand(input, matched, session, streamLog))
    {
        continue;
    }

    AppendLog(streamLog, $"[deepskyblue1]stub[/]: {Markup.Escape(matched.Signature)}");
    WriteMockCommandStream(input, streamLog);
}

static string? ReadInput(List<CommandSpec> commands, List<string> streamLog)
{
    if (Console.IsInputRedirected)
    {
        AnsiConsole.Markup("[bold deepskyblue1]unifocl[/] [grey]>[/] ");
        return Console.ReadLine();
    }

    return ReadInteractiveInput(commands, streamLog);
}

static string? ReadInteractiveInput(List<CommandSpec> commands, List<string> streamLog)
{
    var input = new StringBuilder();
    var renderedLines = RenderComposerFrame(input.ToString(), commands);

    while (true)
    {
        var key = Console.ReadKey(intercept: true);

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                Console.WriteLine();
                return input.ToString();
            case ConsoleKey.Backspace:
                if (input.Length > 0)
                {
                    input.Remove(input.Length - 1, 1);
                }
                break;
            case ConsoleKey.Escape:
                input.Clear();
                break;
            default:
                if (!char.IsControl(key.KeyChar))
                {
                    input.Append(key.KeyChar);
                }
                break;
        }

        ClearComposerFrame(renderedLines);
        renderedLines = RenderComposerFrame(input.ToString(), commands);
    }
}

static int RenderComposerFrame(string input, List<CommandSpec> commands)
{
    var lines = new List<string>
    {
        "[grey]Input[/]",
        $"[bold deepskyblue1]unifocl[/] [grey]>[/] [bold white]{Markup.Escape(input)}[/]"
    };

    if (input.StartsWith('/'))
    {
        lines.Add(string.Empty);
        lines.AddRange(GetSuggestionLines(input, commands));
    }
    else
    {
        lines.Add("[dim]Type / to open command palette. Use your mouse wheel to scroll log history.[/]");
    }

    foreach (var line in lines)
    {
        AnsiConsole.MarkupLine(line);
    }

    return lines.Count;
}

static void ClearComposerFrame(int renderedLines)
{
    for (var i = 0; i < renderedLines; i++)
    {
        Console.Write("\u001b[1A");
        Console.Write("\r\u001b[2K");
    }
}

static void RenderInitialLog(List<string> streamLog)
{
    foreach (var line in streamLog)
    {
        AnsiConsole.MarkupLine(line);
    }
}

static void SeedBootLog(List<string> streamLog)
{
    streamLog.Add("[bold deepskyblue1]unifocl[/]");
    streamLog.Add("[bold green]Welcome to unifocl[/]");
    streamLog.Add(string.Empty);

    var logo = """
                       .;?tXOb*&%@$$$$@%&*bOXt];.                       
                   .!/LaB$$$$%#hpm0QQ0Zph#B$$$$BaL/i.                   
                 +vk@$$@oQu\)1(/jnvccvnrf)I]\n0o@$$@kv+.                 
              "td@$$W0t})nLkWB$$$$$$$$$@Q\vhkQx)}tQW$$@dt,              
            ,u#$$BZ\]fZM$$$$$$$$$$$$$@L)XW$$$$$$WZf]|ZB$$#u,            
          't#$$8U?108$$$$$$$$$$$$$$$d(uW$$$$$$$$$$$801-Y8$$Mf.          
         iq$$$C++-$$$$$$$$$$$$$$$$8n|k$$$$$$$$$$$$$$$$b(~C$$$q~         
        (&$$*[+q%ib$$$$$$$$$$$$$$k{U@$$$$$$$$$$$$$$$$$$$q_[*$$&(        
       j@$$m:j%$$X{$$$$$$$$@@$$$C{k$$$$$$$$$$$@@$$$$$$$$$%j:Z$$@j       
      t$$$L`J$$$$B?Q$$$$$$$$$$$u(&$$$$$$$$$$$$$$$$$$$$$$$$$J^L$$$t      
     -%$$w'C$$$$$$h~*$$$$$$$$@ff@$$$BhqZ0QLCCCCCCL0Zwdh*&B$$Q`m$$B-     
    'k$$M,j$$$$$$$$Q?8$$$$$$$fr$$&Zt-/LpkkkhhhhhkdpwZ0CXvuucL-,M$$k'    
    )$$$f:8$$$$$$$$$z)%$$$$@vt*L):   .I(L#$$$$$$$$$$$$$$$@8#b0,f$$$(    
    m$$W`v$$$$$$$$$$$v(%$$$#:-^          `-vpB$$$$$$$$$$$$$$$$v`W$$w    
   ^M$$m k$$$$$$$$$$$$X)&$$#`              .n|*$$$$$$$$$$$$$$$k m$$M^   
   I%$$X`W$$$$$$$$$$$$$L{o$#`              `Wq[p$$$$$$$$$$$$$$&`Y$$%I   
   I%$$X`W$$$$$$$$$$$$$$p}wW`              `*$o}Q$$$$$$$$$$$$$&`X$$%l   
   ^M$$m k$$$$$$$$$$$$$$$#/n'              '*$$&1Y$$$$$$$$$$$$k m$$M^   
    m$$W`c$$$$$$$$$$$$$$$$@bc?^          ^-:#$$$8)X$$$$$$$$$$$c`W$$m    
    )$$$f"zLpoW%@$$$$$$$$$$$$$W0\l.   ;)L*/c@$$$$%{Y$$$$$$$$$%;t$$$)    
    'k$$M,1pQYvuvvzYJLQ0OZZZOOOZ0X)_jw&$$fr$$$$$$$&?0$$$$$$$$x"#$$k'    
     -%$$m`0$$$$B8M*ohkbddppdbbha*M@$$$@tj@$$$$$$$$o~h$$$$$$0'Z$$%-     
      /$$$L^L$$$$$$$$$$$$$$$$$$$$$$$$$%\n$$$$$$$$$$$O-%$$$$Q^U$$$t      
       j@$$O:xB$$$$$$$$$$$$$$$$$$$$$$#1Y$$$$$$$$$$$$$(v$$Bn:0$$@r       
        (&$$o]-p$$$$$$$$$$$$$$$$$$$$w[m$$$$$$$$$$$$$$oi&b??a$$&|        
         ~q$$@J~|k$$$$$$$$$$$$$$$$Bv(*$$$$$$$$$$$$$$$$}~+U@$$pi         
          .f#$$8X?)Z8$$$$$$$$$$$$d)zB$$$$$$$$$$$$$$%m(?z&$$#f'          
            ,n#$$BZ|]jZW$$$$$$$*r\h$$$$$$$$$$$$$Wmj](0%$$#u,            
              ,/d@$$MQt{(nQkW*c/q$$$$$$$$$BWh0n({tQM$$$dt,              
                 +vk@$$@o0u\)+I/nuzXXzvxt|))\nQa@$$@kv+.                 
                   .!/LhB$$$$BMhqZQLLQZqk*8$$$$BaL/!.                   
                       .;]fXOk#&B$$$$$$B&#kZYf]I.                       
                              '^:I!ii!l;"'                  
""";

    foreach (var line in logo.Split('\n'))
    {
        streamLog.Add($"[grey]{Markup.Escape(line)}[/]");
    }

    streamLog.Add(string.Empty);
    streamLog.Add("[grey]No project attached.[/]");
    streamLog.Add(string.Empty);
}

static IEnumerable<string> GetSuggestionLines(string query, List<CommandSpec> commands)
{
    var normalized = query.Trim().ToLowerInvariant();
    var matches = commands
        .Where(c => c.Signature.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || c.Description.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || c.Trigger.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith(c.Trigger, StringComparison.OrdinalIgnoreCase))
        .Take(14)
        .ToList();

    if (matches.Count == 0)
    {
        return new[] { $"[dim]no matches for {Markup.Escape(query)}[/]" };
    }

    return matches.Select(match =>
        $"[grey]{Markup.Escape(match.Signature)}[/] [dim]- {Markup.Escape(match.Description)}[/]");
}

static CommandSpec? MatchCommand(string input, List<CommandSpec> commands)
{
    var normalized = input.Trim().ToLowerInvariant();

    return commands
        .OrderByDescending(c => c.Trigger.Length)
        .FirstOrDefault(c => normalized == c.Trigger
                             || normalized.StartsWith(c.Trigger + " ", StringComparison.OrdinalIgnoreCase));
}

static void WriteMockCommandStream(string input, List<string> streamLog)
{
    var lines = new[]
    {
        "[grey]stream[/]: validating command payload",
        "[grey]stream[/]: checking daemon reachability",
        "[grey]stream[/]: loading mock capability graph",
        "[grey]stream[/]: command stub executed",
        "[green]stream[/]: done"
    };

    foreach (var line in lines)
    {
        AppendLog(streamLog, $"{line} [dim]({Markup.Escape(input)})[/]");
        Thread.Sleep(110);
    }
}

static void AppendLog(List<string> streamLog, string line)
{
    streamLog.Add(line);
    AnsiConsole.MarkupLine(line);
}

static bool TryHandleLifecycleCommand(string input, CommandSpec matched, SessionState session, List<string> streamLog)
{
    return matched.Trigger switch
    {
        "/open" => HandleOpen(input, matched, session, streamLog),
        "/new" => HandleNew(input, matched, session, streamLog),
        "/clone" => HandleClone(input, matched, session, streamLog),
        _ => false
    };
}

static bool HandleOpen(string input, CommandSpec matched, SessionState session, List<string> streamLog)
{
    var args = ParseCommandArgs(input, matched.Trigger);
    if (args.Count < 1)
    {
        AppendLog(streamLog, "[red]error[/]: usage /open <path>");
        return true;
    }

    var projectPath = ResolveAbsolutePath(args[0], Directory.GetCurrentDirectory());
    TryOpenProject(projectPath, session, streamLog);
    return true;
}

static bool HandleNew(string input, CommandSpec matched, SessionState session, List<string> streamLog)
{
    var args = ParseCommandArgs(input, matched.Trigger);
    if (args.Count < 1)
    {
        AppendLog(streamLog, "[red]error[/]: usage /new <project-name> [unity-version]");
        return true;
    }

    var projectName = args[0].Trim();
    var unityVersion = args.Count > 1 ? args[1].Trim() : "6000.0.0f1";
    if (string.IsNullOrWhiteSpace(projectName))
    {
        AppendLog(streamLog, "[red]error[/]: project name cannot be empty");
        return true;
    }

    var projectPath = ResolveAbsolutePath(projectName, Directory.GetCurrentDirectory());
    AppendLog(streamLog, $"[grey]new[/]: step 1/5 create project directory -> [white]{Markup.Escape(projectPath)}[/]");

    if (Directory.Exists(projectPath) && Directory.EnumerateFileSystemEntries(projectPath).Any())
    {
        AppendLog(streamLog, "[red]error[/]: target directory already exists and is not empty");
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
        AppendLog(streamLog, $"[red]error[/]: failed to create Unity folder structure ({Markup.Escape(ex.Message)})");
        return true;
    }

    AppendLog(streamLog, "[grey]new[/]: step 2/5 write Unity package manifest");
    var manifestResult = WriteDefaultUnityManifest(projectPath);
    if (!manifestResult.Ok)
    {
        AppendLog(streamLog, $"[red]error[/]: {Markup.Escape(manifestResult.Error)}");
        return true;
    }

    AppendLog(streamLog, $"[grey]new[/]: step 3/5 set Unity editor version [white]{Markup.Escape(unityVersion)}[/]");
    var versionResult = WriteProjectVersion(projectPath, unityVersion);
    if (!versionResult.Ok)
    {
        AppendLog(streamLog, $"[red]error[/]: {Markup.Escape(versionResult.Error)}");
        return true;
    }

    AppendLog(streamLog, "[grey]new[/]: step 4/5 generate local templates and bridge config");
    var configResult = EnsureProjectLocalConfig(projectPath);
    if (!configResult.Ok)
    {
        AppendLog(streamLog, $"[red]error[/]: {Markup.Escape(configResult.Error)}");
        return true;
    }

    AppendLog(streamLog, "[grey]new[/]: step 5/5 open bootstrapped project");
    if (TryOpenProject(projectPath, session, streamLog))
    {
        AppendLog(streamLog, "[green]new[/]: Unity project scaffold ready");
    }
    else
    {
        AppendLog(streamLog, "[yellow]new[/]: project scaffolded, but auto-open failed");
    }
    return true;
}

static bool HandleClone(string input, CommandSpec matched, SessionState session, List<string> streamLog)
{
    var args = ParseCommandArgs(input, matched.Trigger);
    if (args.Count < 1)
    {
        AppendLog(streamLog, "[red]error[/]: usage /clone <git-url>");
        return true;
    }

    var gitUrl = args[0].Trim();
    if (string.IsNullOrWhiteSpace(gitUrl))
    {
        AppendLog(streamLog, "[red]error[/]: git url cannot be empty");
        return true;
    }

    AppendLog(streamLog, "[grey]clone[/]: step 1/4 validate git binary");
    var gitVersion = RunProcess("git", "--version", Directory.GetCurrentDirectory());
    if (gitVersion.ExitCode != 0)
    {
        AppendLog(streamLog, "[red]error[/]: git is not available on PATH");
        return true;
    }

    var targetFolderName = GuessRepoFolderName(gitUrl);
    var targetPath = Path.Combine(Directory.GetCurrentDirectory(), targetFolderName);
    AppendLog(streamLog, $"[grey]clone[/]: step 2/4 clone [white]{Markup.Escape(gitUrl)}[/] -> [white]{Markup.Escape(targetPath)}[/]");

    if (Directory.Exists(targetPath))
    {
        AppendLog(streamLog, "[red]error[/]: clone target already exists");
        return true;
    }

    var cloneResult = RunProcess("git", $"clone \"{gitUrl}\" \"{targetPath}\"", Directory.GetCurrentDirectory());
    if (cloneResult.ExitCode != 0)
    {
        AppendLog(streamLog, $"[red]error[/]: git clone failed ({Markup.Escape(SummarizeProcessError(cloneResult))})");
        return true;
    }

    AppendLog(streamLog, "[grey]clone[/]: step 3/4 write local templates and bridge config");
    var configResult = EnsureProjectLocalConfig(targetPath);
    if (!configResult.Ok)
    {
        AppendLog(streamLog, $"[red]error[/]: {Markup.Escape(configResult.Error)}");
        return true;
    }

    AppendLog(streamLog, "[grey]clone[/]: step 4/4 open cloned project");
    if (TryOpenProject(targetPath, session, streamLog))
    {
        AppendLog(streamLog, "[green]clone[/]: repository cloned and prepared");
    }
    else
    {
        AppendLog(streamLog, "[yellow]clone[/]: repository cloned; open skipped (not a Unity project yet)");
    }
    return true;
}

static bool TryOpenProject(string projectPath, SessionState session, List<string> streamLog)
{
    AppendLog(streamLog, $"[grey]open[/]: step 1/4 resolve project path -> [white]{Markup.Escape(projectPath)}[/]");

    if (!Directory.Exists(projectPath))
    {
        AppendLog(streamLog, "[red]error[/]: project directory does not exist");
        return false;
    }

    if (!LooksLikeUnityProject(projectPath))
    {
        AppendLog(streamLog, "[red]error[/]: path is not a Unity project (missing Assets/ or ProjectSettings/)");
        return false;
    }

    AppendLog(streamLog, "[grey]open[/]: step 2/4 validate Unity project layout");
    var bridgeResult = EnsureProjectLocalConfig(projectPath);
    if (!bridgeResult.Ok)
    {
        AppendLog(streamLog, $"[red]error[/]: {Markup.Escape(bridgeResult.Error)}");
        return false;
    }

    AppendLog(streamLog, "[grey]open[/]: step 3/4 attach or start daemon session");
    var daemonSession = EnsureDaemonSession(projectPath);
    var daemonVerb = daemonSession.Created ? "started" : "attached";
    AppendLog(streamLog, $"[grey]daemon[/]: {daemonVerb} project daemon on [white]127.0.0.1:{daemonSession.Port}[/]");

    session.CurrentProjectPath = projectPath;
    session.LastOpenedUtc = DateTimeOffset.UtcNow;
    AppendLog(streamLog, "[grey]open[/]: step 4/4 load project context");
    AppendLog(streamLog, $"[green]open[/]: attached [white]{Markup.Escape(Path.GetFileName(projectPath))}[/]");
    return true;
}

static List<string> ParseCommandArgs(string input, string trigger)
{
    var raw = input.Length > trigger.Length ? input[trigger.Length..].Trim() : string.Empty;
    if (string.IsNullOrWhiteSpace(raw))
    {
        return new List<string>();
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

static string ResolveAbsolutePath(string path, string baseDirectory)
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

static bool LooksLikeUnityProject(string projectPath)
{
    return Directory.Exists(Path.Combine(projectPath, "Assets"))
           && Directory.Exists(Path.Combine(projectPath, "ProjectSettings"));
}

static OperationResult EnsureProjectLocalConfig(string projectPath)
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

static OperationResult WriteDefaultUnityManifest(string projectPath)
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

static OperationResult WriteProjectVersion(string projectPath, string unityVersion)
{
    try
    {
        var projectVersionPath = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
        var content = $"m_EditorVersion: {unityVersion}{Environment.NewLine}m_EditorVersionWithRevision: {unityVersion} (placeholder){Environment.NewLine}";
        File.WriteAllText(projectVersionPath, content);
        return OperationResult.Success();
    }
    catch (Exception ex)
    {
        return OperationResult.Fail($"failed to write ProjectVersion.txt ({ex.Message})");
    }
}

static DaemonSessionInfo EnsureDaemonSession(string projectPath)
{
    var daemonDir = Path.Combine(projectPath, ".unifocl");
    Directory.CreateDirectory(daemonDir);
    var daemonPath = Path.Combine(daemonDir, "daemon.session.json");

    try
    {
        if (File.Exists(daemonPath))
        {
            var existing = JsonSerializer.Deserialize<DaemonSessionInfo>(File.ReadAllText(daemonPath));
            if (existing is not null && existing.Port > 0)
            {
                return existing with { Created = false };
            }
        }
    }
    catch
    {
        // If file is corrupt we overwrite it below.
    }

    var port = 18080 + Math.Abs(projectPath.GetHashCode()) % 2000;
    var session = new DaemonSessionInfo(port, DateTimeOffset.UtcNow, true);
    File.WriteAllText(daemonPath, JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    return session;
}

static ProcessResult RunProcess(string fileName, string arguments, string workingDirectory)
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

static string GuessRepoFolderName(string gitUrl)
{
    var trimmed = gitUrl.TrimEnd('/');
    var lastSegment = trimmed[(trimmed.LastIndexOf('/') + 1)..];
    if (lastSegment.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
    {
        lastSegment = lastSegment[..^4];
    }

    return string.IsNullOrWhiteSpace(lastSegment) ? "cloned-project" : lastSegment;
}

static string SummarizeProcessError(ProcessResult result)
{
    var text = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
    if (string.IsNullOrWhiteSpace(text))
    {
        return $"exit code {result.ExitCode}";
    }

    var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
    return firstLine is null ? $"exit code {result.ExitCode}" : firstLine;
}

internal sealed record CommandSpec(string Signature, string Description, string Trigger);
internal sealed record SessionState
{
    public string? CurrentProjectPath { get; set; }
    public DateTimeOffset? LastOpenedUtc { get; set; }
}

internal sealed record OperationResult(bool Ok, string Error)
{
    public static OperationResult Success() => new(true, string.Empty);
    public static OperationResult Fail(string error) => new(false, error);
}

internal sealed record DaemonSessionInfo(int Port, DateTimeOffset StartedAtUtc, bool Created);
internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
