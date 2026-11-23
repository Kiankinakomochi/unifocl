using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>The outcome of <see cref="UnityEditorLifecycle.OpenAsync"/>.</summary>
/// <param name="Ok">Whether an Editor for the project is now ready for commands.</param>
/// <param name="Pid">The Editor's process id, or 0 when unknown.</param>
/// <param name="AlreadyOpen">Whether the Editor was running before the call.</param>
/// <param name="ErrorCode">The Unity CLI's error code, such as <c>OPEN_EDITOR_EXITED_BEFORE_READY</c>.</param>
/// <param name="Error">What went wrong, for the user.</param>
/// <param name="CompileErrors">Compiler errors from the Editor log when the Editor exited before it was ready.</param>
internal sealed record EditorOpenResult(
    bool Ok,
    int Pid,
    bool AlreadyOpen,
    string? ErrorCode,
    string? Error,
    IReadOnlyList<string> CompileErrors);

/// <summary>The outcome of <see cref="UnityEditorLifecycle.CloseAsync"/>.</summary>
/// <param name="Ok">Whether no Editor has the project open any more.</param>
/// <param name="Unsaved">Scenes whose unsaved changes the Editor discarded on exit.</param>
/// <param name="Error">What went wrong, for the user.</param>
internal sealed record EditorCloseResult(bool Ok, IReadOnlyList<string> Unsaved, string? Error);

/// <summary>
/// Starts, finds and stops the Unity Editor for a project through the official Unity CLI
/// (<c>unity open</c>, <c>unity status</c>, <c>unity close</c>).
/// </summary>
/// <remarks>
/// An Editor unifocl starts runs in batchmode, the way the old Host mode did, and quits on its own after
/// <see cref="IdleTimeoutSeconds"/> without a unifocl command (see <c>UnifoclHostSession</c> in the Editor
/// package). Its log goes to <c>Library/unifocl/editor.log</c>, which is where startup compile errors are
/// read from.
/// </remarks>
internal static class UnityEditorLifecycle
{
    public const int DefaultIdleTimeoutSeconds = 600;

    /// <summary>Overrides <see cref="DefaultIdleTimeoutSeconds"/>, in seconds.</summary>
    public const string IdleTimeoutEnvironmentVariable = "UNIFOCL_EDITOR_IDLE_TIMEOUT_SECONDS";

    public static int IdleTimeoutSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable(IdleTimeoutEnvironmentVariable), out var seconds) && seconds > 0
            ? seconds
            : DefaultIdleTimeoutSeconds;

    /// <summary>The Editor argument carrying the idle timeout to <c>UnifoclHostSession</c>.</summary>
    public const string IdleTimeoutArgument = "-unifoclIdleTimeoutSeconds";

    private static readonly Regex CompileErrorLine = new(@"error CS\d{4}:", RegexOptions.Compiled);

    public static string EditorLogPath(string projectPath) =>
        Path.Combine(Path.GetFullPath(projectPath), "Library", "unifocl", "editor.log");

    /// <summary>The running Editor that has <paramref name="projectPath"/> open, if there is one.</summary>
    public static async Task<PipelineEditorInstance?> FindAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var unityCli = UnityCliLocator.Locate();
        if (unityCli is null)
        {
            return null;
        }

        var instances = await new UnityCommandTransport(unityCli, projectPath).StatusAsync(cancellationToken);
        return Match(instances, projectPath);
    }

    internal static PipelineEditorInstance? Match(IReadOnlyList<PipelineEditorInstance> instances, string projectPath)
    {
        var wanted = Normalize(projectPath);
        return instances.FirstOrDefault(instance => string.Equals(Normalize(instance.ProjectPath), wanted, PathComparison));
    }

    /// <summary>
    /// Opens the project in a batchmode Editor and waits until it takes commands. An Editor that already
    /// has the project open, GUI or batchmode, is used as it is.
    /// </summary>
    public static async Task<EditorOpenResult> OpenAsync(
        string projectPath,
        bool ignoreCompileErrors,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var unityCli = UnityCliLocator.Locate();
        if (unityCli is null)
        {
            return new EditorOpenResult(false, 0, false, "UNITY_CLI_NOT_FOUND", UnityCliLocator.InstallHint, []);
        }

        var logPath = EditorLogPath(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var startedAtUtc = DateTime.UtcNow;
        var run = await UnityCommandTransport.RunAsync(
            unityCli,
            BuildOpenArguments(projectPath, ignoreCompileErrors, timeout, logPath),
            timeout + TimeSpan.FromSeconds(30),
            cancellationToken);
        if (run.TimedOut)
        {
            return new EditorOpenResult(false, 0, false, "OPEN_TIMEOUT", $"the Editor did not become ready within {timeout.TotalSeconds:0}s", []);
        }

        var result = ParseOpen(run.Stdout, run.Stderr);
        if (result.Ok || result.ErrorCode != "OPEN_EDITOR_EXITED_BEFORE_READY")
        {
            return result;
        }

        var compileErrors = ReadCompileErrors(logPath, startedAtUtc);
        return result with
        {
            CompileErrors = compileErrors,
            Error = compileErrors.Count > 0
                ? $"the Editor exited because scripts have compile errors ({compileErrors.Count}); see {logPath}"
                : $"{result.Error} (log: {logPath})",
        };
    }

    internal static List<string> BuildOpenArguments(string projectPath, bool ignoreCompileErrors, TimeSpan timeout, string logPath)
    {
        var editorArgs = new StringBuilder("-nographics -vcsMode None");
        if (ignoreCompileErrors)
        {
            editorArgs.Append(" -ignoreCompileErrors");
        }

        editorArgs.Append(" -logFile ").Append(QuoteEditorArgument(logPath));
        editorArgs.Append(' ').Append(IdleTimeoutArgument).Append(' ').Append(IdleTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return
        [
            "open", Path.GetFullPath(projectPath),
            "--batchmode",
            "--until-ready",
            "--timeout", Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--args", editorArgs.ToString(),
            "--no-banner",
            "--non-interactive",
            "--format", "json",
        ];
    }

    internal static EditorOpenResult ParseOpen(string stdout, string stderr)
    {
        if (!TryParseEnvelope(stdout, out var success, out var data, out var errorCode, out var errorMessage))
        {
            var detail = PipelineOutput.ParseError(stderr, string.Empty);
            return new EditorOpenResult(false, 0, false, null, string.IsNullOrWhiteSpace(detail) ? "unity open returned no result" : detail, []);
        }

        var alreadyOpen = data is { } d && d.TryGetProperty("alreadyOpen", out var open) && open.ValueKind == JsonValueKind.True;
        var pid = GetInt(data, "pid") is var p and > 0 ? p : GetInt(data, "launchedPid");
        return success
            ? new EditorOpenResult(true, pid, alreadyOpen, null, null, [])
            : new EditorOpenResult(false, pid, alreadyOpen, errorCode, errorMessage ?? "unity open failed", []);
    }

    /// <summary>
    /// Closes the Editor that has the project open. The Unity CLI exits it without saving; the result
    /// lists the scenes whose changes were dropped.
    /// </summary>
    public static async Task<EditorCloseResult> CloseAsync(string projectPath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var unityCli = UnityCliLocator.Locate();
        if (unityCli is null)
        {
            return new EditorCloseResult(false, [], UnityCliLocator.InstallHint);
        }

        var run = await UnityCommandTransport.RunAsync(
            unityCli,
            [
                "close", Path.GetFullPath(projectPath),
                "--timeout", Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--force",
                "--no-banner", "--non-interactive", "--format", "json",
            ],
            timeout + TimeSpan.FromSeconds(15),
            cancellationToken);
        if (run.TimedOut)
        {
            return new EditorCloseResult(false, [], $"unity close did not finish within {timeout.TotalSeconds:0}s");
        }

        return ParseClose(run.Stdout, run.Stderr);
    }

    internal static EditorCloseResult ParseClose(string stdout, string stderr)
    {
        if (!TryParseEnvelope(stdout, out var success, out var data, out _, out var errorMessage))
        {
            var detail = PipelineOutput.ParseError(stderr, string.Empty);
            return new EditorCloseResult(false, [], string.IsNullOrWhiteSpace(detail) ? "unity close returned no result" : detail);
        }

        var unsaved = new List<string>();
        if (data is { } d && d.TryGetProperty("unsaved", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            unsaved.AddRange(list.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!));
        }

        return new EditorCloseResult(success, unsaved, success ? null : errorMessage ?? "unity close failed");
    }

    /// <summary>Whether the project's Editor answers unifocl's bridge commands.</summary>
    public static async Task<bool> PingAsync(string projectPath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var transport = PipelineTransports.For(projectPath, PipelineTransportKind.McpSession, out _);
        if (transport is null)
        {
            return false;
        }

        var result = await transport.ExecAsync(EditorLinkRoutes.Group, "ping", timeout: timeout, cancellationToken: cancellationToken);
        return result.Ok;
    }

    internal static List<string> ExtractCompileErrors(IEnumerable<string> lines) =>
        lines.Where(line => CompileErrorLine.IsMatch(line)).Select(line => line.Trim()).Distinct().Take(20).ToList();

    private static IReadOnlyList<string> ReadCompileErrors(string logPath, DateTime startedAtUtc)
    {
        try
        {
            // A log older than this launch belongs to an earlier Editor and says nothing about this one.
            if (!File.Exists(logPath) || File.GetLastWriteTimeUtc(logPath) < startedAtUtc)
            {
                return [];
            }

            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return ExtractCompileErrors(lines);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    // `unity open --args` splits the string like a shell would, so a path with spaces is quoted.
    private static string QuoteEditorArgument(string value) =>
        value.Contains(' ') || value.Contains('"') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    private static bool TryParseEnvelope(
        string stdout,
        out bool success,
        out JsonElement? data,
        out string? errorCode,
        out string? errorMessage)
    {
        success = false;
        data = null;
        errorCode = null;
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var flag))
            {
                return false;
            }

            success = flag.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("data", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                data = payload.Clone();
            }

            if (root.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0
                && errors[0] is { ValueKind: JsonValueKind.Object } first)
            {
                errorCode = first.TryGetProperty("code", out var code) ? code.GetString() : null;
                errorMessage = first.TryGetProperty("message", out var message) ? message.GetString() : null;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int GetInt(JsonElement? element, string property) =>
        element is { } e && e.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Normalize(string path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
