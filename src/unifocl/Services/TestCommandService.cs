using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Spectre.Console;

/// <summary>
/// Handles test orchestration commands (test list, test run editmode/playmode).
/// Unity is launched as a direct subprocess — NOT dispatched through the daemon.
/// </summary>
internal sealed class TestCommandService
{
    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    // Default timeout for EditMode runs; PlayMode gets its own override.
    private static readonly TimeSpan DefaultEditModeTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultPlayModeTimeout = TimeSpan.FromMinutes(30);

    public async Task HandleTestCommandAsync(
        string input,
        CliSessionState session,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            log("[yellow]test[/]: open a project first with /open");
            return;
        }

        var tokens = Tokenize(input);
        // Accepts both "test <sub>" and "/test <sub>"
        var head = tokens.Count > 0 ? tokens[0].TrimStart('/').ToLowerInvariant() : string.Empty;
        if (head != "test")
        {
            log("[x] usage: test <list|run editmode|run playmode>");
            return;
        }

        if (tokens.Count < 2)
        {
            log("[x] usage: test <list|run editmode|run playmode>");
            return;
        }

        var subcommand = tokens[1].ToLowerInvariant();
        switch (subcommand)
        {
            case "list":
                await HandleListAsync(session, log, cancellationToken);
                return;

            case "run":
                if (tokens.Count < 3)
                {
                    log("[x] usage: test run <editmode|playmode> [--timeout <seconds>]");
                    return;
                }

                var platform = tokens[2].ToLowerInvariant();
                if (platform != "editmode" && platform != "playmode")
                {
                    log($"[x] unsupported test platform: {Markup.Escape(tokens[2])} (use editmode or playmode)");
                    return;
                }

                // Parse --timeout flag
                var timeoutSeconds = platform == "playmode"
                    ? (int)DefaultPlayModeTimeout.TotalSeconds
                    : (int)DefaultEditModeTimeout.TotalSeconds;

                for (var i = 3; i < tokens.Count - 1; i++)
                {
                    if (tokens[i].Equals("--timeout", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(tokens[i + 1], out var parsedSecs)
                        && parsedSecs > 0)
                    {
                        timeoutSeconds = parsedSecs;
                        break;
                    }
                }

                var testPlatform = platform == "editmode" ? TestPlatform.EditMode : TestPlatform.PlayMode;
                await HandleRunAsync(session, testPlatform, TimeSpan.FromSeconds(timeoutSeconds), log, cancellationToken);
                return;

            default:
                log($"[x] unknown test subcommand: {Markup.Escape(tokens[1])}");
                log("supported: test list | test run editmode | test run playmode");
                return;
        }
    }

    // ── Public helpers for ExecV2 direct dispatch ────────────────────────────

    /// <summary>
    /// ExecV2 entry-point for test.list. Returns a list of <see cref="TestCaseEntry"/>.
    /// </summary>
    public async Task<(bool Ok, object? Result, string? Error)> ExecListAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var editorPath = ResolveEditorPath(projectPath, out var resolveError);
        if (editorPath is null)
        {
            return (false, null, resolveError);
        }

        var artifactsDir = GetArtifactsDir(projectPath);
        Directory.CreateDirectory(artifactsDir);
        var listOutputFile = Path.Combine(artifactsDir, "test-list.txt");

        var args = BuildUnityArgs(projectPath, new[]
        {
            "-runTests",
            "-testPlatform", "EditMode",
            "-listTests",
            "-testResults", listOutputFile
        });

        var (exitCode, stdout, stderr) = await RunUnityProcessAsync(
            editorPath, args, TimeSpan.FromMinutes(5), cancellationToken);

        // Unity -listTests writes test names to stdout (one per line)
        var entries = ParseTestListOutput(stdout, stderr);
        return (true, entries, null);
    }

    /// <summary>
    /// ExecV2 entry-point for test.run. Returns a <see cref="TestRunResult"/>.
    /// </summary>
    public async Task<(bool Ok, object? Result, string? Error)> ExecRunAsync(
        string projectPath,
        TestPlatform platform,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var editorPath = ResolveEditorPath(projectPath, out var resolveError);
        if (editorPath is null)
        {
            return (false, null, resolveError);
        }

        var artifactsDir = GetArtifactsDir(projectPath);
        Directory.CreateDirectory(artifactsDir);
        var resultsFile = Path.Combine(artifactsDir,
            platform == TestPlatform.EditMode ? "test-results-editmode.xml" : "test-results-playmode.xml");
        var platformFlag = platform == TestPlatform.EditMode ? "EditMode" : "PlayMode";

        var args = BuildUnityArgs(projectPath, new[]
        {
            "-runTests",
            "-testPlatform", platformFlag,
            "-testResults", resultsFile,
            "-batchmode",
            "-nographics"
        });

        var stopwatch = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await RunUnityProcessAsync(editorPath, args, timeout, cancellationToken);
        stopwatch.Stop();

        var result = ParseNUnitResults(resultsFile, stopwatch.Elapsed.TotalMilliseconds, artifactsDir);
        return (exitCode == 0 || result.Failed == 0, result, null);
    }

    // ── Private handlers ─────────────────────────────────────────────────────

    private async Task HandleListAsync(
        CliSessionState session,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var projectPath = session.CurrentProjectPath!;
        log("[grey]test[/]: resolving Unity editor...");

        var editorPath = ResolveEditorPath(projectPath, out var resolveError);
        if (editorPath is null)
        {
            log($"[red]test[/]: {Markup.Escape(resolveError ?? "could not resolve Unity editor")}");
            return;
        }

        log($"[grey]test[/]: editor: {Markup.Escape(editorPath)}");
        log("[grey]test[/]: listing tests (EditMode)...");

        var artifactsDir = GetArtifactsDir(projectPath);
        Directory.CreateDirectory(artifactsDir);
        var listOutputFile = Path.Combine(artifactsDir, "test-list.txt");

        var args = BuildUnityArgs(projectPath, new[]
        {
            "-runTests",
            "-testPlatform", "EditMode",
            "-listTests",
            "-testResults", listOutputFile
        });

        var (exitCode, stdout, stderr) = await RunUnityProcessAsync(
            editorPath, args, TimeSpan.FromMinutes(5), cancellationToken);

        var entries = ParseTestListOutput(stdout, stderr);
        log($"[green]test[/]: found {entries.Count} test(s)");
        foreach (var entry in entries)
        {
            log($"  [white]{Markup.Escape(entry.TestName)}[/] [grey]({Markup.Escape(entry.Assembly)})[/]");
        }
    }

    private async Task HandleRunAsync(
        CliSessionState session,
        TestPlatform platform,
        TimeSpan timeout,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var projectPath = session.CurrentProjectPath!;
        var platformLabel = platform == TestPlatform.EditMode ? "EditMode" : "PlayMode";
        log($"[grey]test[/]: resolving Unity editor...");

        var editorPath = ResolveEditorPath(projectPath, out var resolveError);
        if (editorPath is null)
        {
            log($"[red]test[/]: {Markup.Escape(resolveError ?? "could not resolve Unity editor")}");
            return;
        }

        log($"[grey]test[/]: editor: {Markup.Escape(editorPath)}");

        var artifactsDir = GetArtifactsDir(projectPath);
        Directory.CreateDirectory(artifactsDir);
        var resultsFile = Path.Combine(artifactsDir,
            platform == TestPlatform.EditMode ? "test-results-editmode.xml" : "test-results-playmode.xml");

        var args = BuildUnityArgs(projectPath, new[]
        {
            "-runTests",
            "-testPlatform", platformLabel,
            "-testResults", resultsFile,
            "-batchmode",
            "-nographics"
        });

        log($"[grey]test[/]: running {Markup.Escape(platformLabel)} tests (timeout: {(int)timeout.TotalSeconds}s)...");

        var stopwatch = Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await RunUnityProcessAsync(editorPath, args, timeout, cancellationToken);
        stopwatch.Stop();

        var result = ParseNUnitResults(resultsFile, stopwatch.Elapsed.TotalMilliseconds, artifactsDir);

        var statusColor = result.Failed == 0 ? "green" : "red";
        log($"[{statusColor}]test[/]: {platformLabel} — total={result.Total} passed={result.Passed} failed={result.Failed} skipped={result.Skipped} ({result.DurationMs:0}ms)");
        log($"[grey]test[/]: artifacts: {Markup.Escape(result.ArtifactsPath)}");

        if (result.Failed > 0)
        {
            foreach (var failure in result.Failures)
            {
                log($"  [red]FAIL[/] {Markup.Escape(failure.TestName)}");
                if (!string.IsNullOrWhiteSpace(failure.Message))
                {
                    log($"       {Markup.Escape(failure.Message)}");
                }
            }
        }
    }

    // ── Subprocess helpers ────────────────────────────────────────────────────

    private static string? ResolveEditorPath(string projectPath, out string? error)
    {
        error = null;
        if (UnityEditorPathService.TryResolveEditorForProject(projectPath, out var editorPath, out _, out var resolveError))
        {
            return editorPath;
        }

        error = resolveError ?? "could not resolve Unity editor path";
        return null;
    }

    private static string GetArtifactsDir(string projectPath)
        => Path.Combine(projectPath, "Logs", "unifocl-test");

    private static string[] BuildUnityArgs(string projectPath, string[] unityFlags)
    {
        var allArgs = new List<string>
        {
            "-projectPath", projectPath
        };
        allArgs.AddRange(unityFlags);
        return allArgs.ToArray();
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunUnityProcessAsync(
        string editorPath,
        string[] args,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stdoutSb = new System.Text.StringBuilder();
        var stderrSb = new System.Text.StringBuilder();

        var psi = new ProcessStartInfo
        {
            FileName = editorPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdoutSb.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderrSb.AppendLine(e.Data);
        };
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);
        process.EnableRaisingEvents = true;

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        linkedCts.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            tcs.TrySetResult(-1);
        });

        var exitCode = await tcs.Task.ConfigureAwait(false);
        return (exitCode, stdoutSb.ToString(), stderrSb.ToString());
    }

    // ── Output parsers ────────────────────────────────────────────────────────

    /// <summary>
    /// Unity -listTests writes one test name per line to stdout.
    /// Assembly information is derived from the dotted test name prefix when available.
    /// </summary>
    private static List<TestCaseEntry> ParseTestListOutput(string stdout, string stderr)
    {
        var entries = new List<TestCaseEntry>();
        var combined = stdout + "\n" + stderr;
        foreach (var rawLine in combined.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Skip Unity log noise: lines starting with known prefixes
            if (line.StartsWith("Loading", StringComparison.Ordinal)
                || line.StartsWith("Initialize", StringComparison.Ordinal)
                || line.StartsWith("Activating", StringComparison.Ordinal)
                || line.StartsWith("DisplayProgressbar", StringComparison.Ordinal)
                || line.StartsWith("[", StringComparison.Ordinal)
                || line.Contains("Unity ", StringComparison.Ordinal)
                || line.Contains("WARNING", StringComparison.Ordinal)
                || line.Contains("ERROR", StringComparison.Ordinal))
            {
                continue;
            }

            // Heuristic: test names contain dots and parentheses
            if (!line.Contains('.'))
            {
                continue;
            }

            var assembly = TryExtractAssembly(line);
            entries.Add(new TestCaseEntry(line, assembly));
        }

        return entries;
    }

    private static string TryExtractAssembly(string testName)
    {
        // Full test name format: <Assembly>.<Namespace>.<Class>.<Method>(<args>)
        // The first segment before the first dot is typically the assembly name.
        var firstDot = testName.IndexOf('.');
        return firstDot > 0 ? testName[..firstDot] : "Unknown";
    }

    /// <summary>
    /// Parse NUnit v3 XML results file produced by Unity's test runner.
    /// If the file does not exist (e.g. Unity crashed), returns a zero result.
    /// </summary>
    private static TestRunResult ParseNUnitResults(string resultsFile, double durationMs, string artifactsDir)
    {
        var failures = new List<TestFailureEntry>();

        if (!File.Exists(resultsFile))
        {
            return new TestRunResult(0, 0, 0, 0, durationMs, artifactsDir, failures);
        }

        try
        {
            var doc = XDocument.Load(resultsFile);
            var testRun = doc.Root;
            if (testRun is null)
            {
                return new TestRunResult(0, 0, 0, 0, durationMs, artifactsDir, failures);
            }

            var total = ParseIntAttr(testRun, "total");
            var passed = ParseIntAttr(testRun, "passed");
            var failed = ParseIntAttr(testRun, "failed");
            var skipped = ParseIntAttr(testRun, "skipped");
            var xmlDuration = ParseDoubleAttr(testRun, "duration");
            var actualDurationMs = xmlDuration > 0 ? xmlDuration * 1000.0 : durationMs;

            // Collect failing test cases
            foreach (var testCase in testRun.Descendants("test-case"))
            {
                var result = testCase.Attribute("result")?.Value ?? string.Empty;
                if (!result.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var testName = testCase.Attribute("fullname")?.Value
                    ?? testCase.Attribute("name")?.Value
                    ?? "Unknown";
                var caseDurationMs = ParseDoubleAttr(testCase, "duration") * 1000.0;

                var failureEl = testCase.Element("failure");
                var message = failureEl?.Element("message")?.Value?.Trim() ?? string.Empty;
                var stackTrace = failureEl?.Element("stack-trace")?.Value?.Trim() ?? string.Empty;

                failures.Add(new TestFailureEntry(testName, message, stackTrace, caseDurationMs));
            }

            return new TestRunResult(total, passed, failed, skipped, actualDurationMs, artifactsDir, failures);
        }
        catch
        {
            return new TestRunResult(0, 0, 0, 0, durationMs, artifactsDir, failures);
        }
    }

    private static int ParseIntAttr(XElement el, string name)
        => int.TryParse(el.Attribute(name)?.Value, out var v) ? v : 0;

    private static double ParseDoubleAttr(XElement el, string name)
        => double.TryParse(el.Attribute(name)?.Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var v) ? v : 0.0;

    // ── Tokenizer ─────────────────────────────────────────────────────────────

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
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
}
