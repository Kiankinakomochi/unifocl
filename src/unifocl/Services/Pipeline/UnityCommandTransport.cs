using System.Diagnostics;
using System.Text.Json;

/// <summary>
/// Runs each Pipeline command as its own <c>unity command</c> process. Process start costs about
/// 0.6 s, so this suits one-shot CLI calls; agent sessions use <see cref="UnityMcpTransport"/>.
/// </summary>
internal sealed class UnityCommandTransport : IPipelineTransport
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    // Headroom for the CLI to report the Editor's own timeout before we kill the process.
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(10);

    private readonly string _unityCli;
    private readonly string _projectPath;

    public UnityCommandTransport(string unityCli, string projectPath)
    {
        _unityCli = unityCli;
        _projectPath = projectPath;
    }

    public async Task<PipelineCallResult> ExecAsync(
        string group,
        string name,
        IReadOnlyDictionary<string, object?>? args = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var argv = BuildArguments(group, name, args, _projectPath, effectiveTimeout);
        var run = await RunAsync(_unityCli, argv, effectiveTimeout + KillGrace, cancellationToken);
        if (run.TimedOut)
        {
            return PipelineCallResult.Failure(
                $"`unity command {group} {name}` did not finish within {effectiveTimeout.TotalSeconds:0} s.");
        }

        return run.ExitCode == 0
            ? PipelineCallResult.Success(PipelineOutput.ParseResult(run.Stdout))
            : PipelineCallResult.Failure(
                PipelineOutput.ParseError(run.Stderr, $"`unity command {group} {name}` exited with {run.ExitCode}."),
                run.ExitCode);
    }

    /// <summary>
    /// The Editor instances the Unity CLI can see for this project (<c>unity status</c>). Empty when
    /// none is running or Pipeline isn't installed in it.
    /// </summary>
    public async Task<IReadOnlyList<PipelineEditorInstance>> StatusAsync(CancellationToken cancellationToken = default)
    {
        var run = await RunAsync(
            _unityCli,
            ["status", "--project-path", _projectPath, "--no-banner", "--non-interactive", "--format", "json"],
            TimeSpan.FromSeconds(20),
            cancellationToken);
        return ParseStatus(run.Stdout);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The argv for one call. Arguments follow the command, then the CLI's own options.</summary>
    internal static List<string> BuildArguments(
        string group,
        string name,
        IReadOnlyDictionary<string, object?>? args,
        string projectPath,
        TimeSpan timeout)
    {
        var argv = new List<string> { "command", group, name };
        if (args is not null)
        {
            foreach (var (key, value) in args)
            {
                if (value is null)
                {
                    continue;
                }

                argv.Add("--" + key);
                argv.Add(PipelineOutput.FormatArgument(value));
            }
        }

        argv.AddRange([
            "--project-path", projectPath,
            "--timeout", Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--no-banner",
            "--non-interactive",
            "--format", "json",
        ]);
        return argv;
    }

    internal static IReadOnlyList<PipelineEditorInstance> ParseStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("instances", out var instances)
                || instances.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<PipelineEditorInstance>();
            foreach (var item in instances.EnumerateArray())
            {
                result.Add(new PipelineEditorInstance(
                    GetString(item, "projectPath") ?? GetString(item, "project") ?? string.Empty,
                    GetInt(item, "port"),
                    GetInt(item, "pid"),
                    GetString(item, "state") ?? "unknown",
                    GetString(item, "editorVersion") ?? GetString(item, "version") ?? string.Empty,
                    item.TryGetProperty("batchmode", out var batch) && batch.ValueKind == JsonValueKind.True));
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;

    internal readonly record struct ProcessRun(int ExitCode, string Stdout, string Stderr, bool TimedOut);

    internal static async Task<ProcessRun> RunAsync(
        string fileName,
        IReadOnlyList<string> argv,
        TimeSpan limit,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in argv)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Keep the CLI's output machine-readable whatever the user's environment says.
        startInfo.Environment["UNITY_NO_BANNER"] = "1";
        startInfo.Environment["UNITY_NO_PAGER"] = "1";
        startInfo.Environment["NO_COLOR"] = "1";

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited between the timeout and the kill.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessRun(-1, string.Empty, string.Empty, TimedOut: true);
        }

        return new ProcessRun(process.ExitCode, await stdout, await stderr, TimedOut: false);
    }
}
