using System.Text.Json;

/// <summary>
/// Polls the on-disk marker the editor writes when an in-editor test job finishes.
///
/// Results cannot come back in the daemon response body: running tests can trigger a domain
/// reload, which tears down the daemon's HTTP socket and every in-memory continuation with it.
/// The editor therefore writes a marker file and the CLI waits for it, the same handoff
/// <see cref="CompileCompletionWaiter"/> uses for compiles.
/// </summary>
internal sealed class TestCompletionWaiter
{
    private const int DefaultPollIntervalMs = 250;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public enum WaitOutcome
    {
        Completed,
        TimedOut,
        Cancelled,
    }

    public sealed record Result(WaitOutcome Outcome, TestJobResultDto? Payload, string? Diagnostic);

    public async Task<Result> WaitAsync(
        string projectRoot,
        string requestId,
        TimeSpan timeout,
        Action<TimeSpan>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var resultFile = Path.Combine(projectRoot, "Temp", "unifocl", "test-results", $"{requestId}.json");
        var startedAt = DateTime.UtcNow;
        var deadline = startedAt.Add(timeout);
        var nextProgressAt = startedAt.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new Result(WaitOutcome.Cancelled, null, "wait cancelled");
            }

            var payload = TryReadResultFile(resultFile);
            if (payload is not null)
            {
                return new Result(WaitOutcome.Completed, payload, null);
            }

            var now = DateTime.UtcNow;
            if (now >= nextProgressAt)
            {
                onProgress?.Invoke(now - startedAt);
                nextProgressAt = now.AddSeconds(10);
            }

            try
            {
                await Task.Delay(DefaultPollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return new Result(WaitOutcome.Cancelled, null, "wait cancelled");
            }
        }

        // The marker may have landed during the final interval.
        var finalPayload = TryReadResultFile(resultFile);
        if (finalPayload is not null)
        {
            return new Result(WaitOutcome.Completed, finalPayload, null);
        }

        return new Result(
            WaitOutcome.TimedOut,
            null,
            $"the editor did not report test completion within {timeout.TotalSeconds:F0}s. "
            + "A domain reload during the run can abort it silently — check the Unity console, "
            + "or re-run with a longer --timeout.");
    }

    private static TestJobResultDto? TryReadResultFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<TestJobResultDto>(json, JsonOpts);
        }
        catch
        {
            // A partially written file reads as "not ready yet"; the editor writes via a
            // temp-then-move, so this should be rare.
            return null;
        }
    }
}
