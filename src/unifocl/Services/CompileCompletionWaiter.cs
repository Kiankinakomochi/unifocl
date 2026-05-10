using System.Net.Http;
using System.Text.Json;

/// <summary>
/// Polls the on-disk markers Unity writes during a compile + domain reload
/// cycle so the CLI can observe completion without depending on the daemon
/// HTTP socket staying up. The daemon socket is torn down during domain
/// reload after a successful compile, so any HTTP-only completion check
/// fails with "Connection refused" exactly when we need an answer most.
///
/// Algorithm:
///   1. Wait until the result file appears at &lt;projectRoot&gt;/Temp/unifocl/compile-results/{requestId}.json
///      AND both lock files are gone:
///        Temp/compiling.lock      — present while CompilationPipeline is busy
///        Temp/domainreload.lock   — present while Unity is reloading the AppDomain
///   2. Once both conditions hold, require them to keep holding for a grace
///      period (LockGracePeriodMs). This catches the brief window between
///      compilationFinished and beforeAssemblyReload (~50 ms measured)
///      where neither lock exists.
///   3. After the grace period, ping the daemon's /compile/status to
///      confirm it has come back online. If it has not, keep polling.
/// </summary>
internal sealed class CompileCompletionWaiter
{
    private static readonly HttpClient HealthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    private const int DefaultPollIntervalMs = 200;
    private const int LockGracePeriodMs = 500;
    private const int DefaultTimeoutMs = 120_000;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public enum WaitOutcome
    {
        Completed,
        TimedOut,
        Cancelled,
    }

    public sealed record Result(WaitOutcome Outcome, CompilePersistedResultDto? Payload, string? Diagnostic);

    public async Task<Result> WaitAsync(
        string projectRoot,
        string requestId,
        int? daemonPort,
        int timeoutMs = DefaultTimeoutMs,
        int pollIntervalMs = DefaultPollIntervalMs,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        string resultDir = Path.Combine(projectRoot, "Temp", "unifocl", "compile-results");
        string resultFile = Path.Combine(resultDir, $"{requestId}.json");
        string compilingLock = Path.Combine(projectRoot, "Temp", "compiling.lock");
        string domainReloadLock = Path.Combine(projectRoot, "Temp", "domainreload.lock");

        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        DateTime? idleSince = null;
        var lastReportedPhase = string.Empty;

        while (DateTime.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new Result(WaitOutcome.Cancelled, null, "wait cancelled");
            }

            bool resultExists = File.Exists(resultFile);
            bool compileLocked = File.Exists(compilingLock);
            bool reloadLocked = File.Exists(domainReloadLock);
            bool busy = compileLocked || reloadLocked;

            string phase = (resultExists, compileLocked, reloadLocked) switch
            {
                (false, true, _) => "compiling",
                (false, _, true) => "domain-reload",
                (true, true, _) => "post-compile",
                (true, _, true) => "domain-reload",
                (false, false, false) => "queued",
                (true, false, false) => "settling",
            };

            if (phase != lastReportedPhase)
            {
                onProgress?.Invoke(phase);
                lastReportedPhase = phase;
            }

            if (resultExists && !busy)
            {
                idleSince ??= DateTime.UtcNow;

                if ((DateTime.UtcNow - idleSince.Value).TotalMilliseconds >= LockGracePeriodMs)
                {
                    if (daemonPort is int port)
                    {
                        bool ready = await IsDaemonReadyAsync(port, cancellationToken).ConfigureAwait(false);
                        if (!ready)
                        {
                            // Locks gone but daemon not back yet — keep waiting
                            // rather than declaring completion prematurely.
                            await Task.Delay(pollIntervalMs, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }

                    CompilePersistedResultDto? payload = TryReadResultFile(resultFile);
                    return new Result(WaitOutcome.Completed, payload, null);
                }
            }
            else
            {
                idleSince = null;
            }

            try
            {
                await Task.Delay(pollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return new Result(WaitOutcome.Cancelled, null, "wait cancelled");
            }
        }

        // Final read in case the result file appeared in the last interval.
        CompilePersistedResultDto? finalPayload = TryReadResultFile(resultFile);
        if (finalPayload is not null && !File.Exists(compilingLock) && !File.Exists(domainReloadLock))
        {
            return new Result(WaitOutcome.Completed, finalPayload, null);
        }

        return new Result(
            WaitOutcome.TimedOut,
            finalPayload,
            $"compile did not settle within {timeoutMs / 1000.0:F1}s");
    }

    private static CompilePersistedResultDto? TryReadResultFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CompilePersistedResultDto>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> IsDaemonReadyAsync(int port, CancellationToken ct)
    {
        try
        {
            using var resp = await HealthClient.GetAsync(
                $"http://127.0.0.1:{port}/compile/status",
                HttpCompletionOption.ResponseContentRead,
                ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
