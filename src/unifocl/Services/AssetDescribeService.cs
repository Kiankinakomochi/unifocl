using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

/// <summary>
/// Composite orchestrator for asset.describe — a two-phase command:
///   Phase 1: Ask Unity daemon to export a thumbnail PNG via "export-thumbnail".
///   Phase 2: Run a local Python BLIP/CLIP script to caption the thumbnail.
/// </summary>
internal sealed class AssetDescribeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan FirstRunTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CachedRunTimeout = TimeSpan.FromSeconds(30);

    private static readonly string ScriptDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unifocl", "scripts");

    private static readonly string ScriptPath = Path.Combine(ScriptDir, "describe_asset.py");

    private static readonly string BlipModelCacheDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "huggingface", "hub", "models--Salesforce--blip-image-captioning-base");

    public async Task<ExecV2Response> DescribeAsync(
        ExecV2Request request,
        ProjectDaemonBridge projectBridge,
        CancellationToken ct)
    {
        var assetPath = GetStringArg(request.Args, "assetPath");
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return Failed(request.RequestId, "asset.describe requires args.assetPath");
        }

        var engine = GetStringArg(request.Args, "engine") ?? "blip";
        var dryRun = request.Intent?.DryRun == true;

        if (dryRun)
        {
            return await DryRunAsync(request.RequestId, assetPath, engine, projectBridge);
        }

        // ── Phase 1: Export thumbnail via daemon ────────────────────────
        var thumbnailDto = new ProjectCommandRequestDto("export-thumbnail", assetPath, null, null, request.RequestId);
        var serialized = JsonSerializer.Serialize(thumbnailDto, JsonOptions);

        if (!projectBridge.TryHandle($"PROJECT_CMD {serialized}", out var daemonResponse))
        {
            return Failed(request.RequestId, "daemon did not handle export-thumbnail");
        }

        ProjectCommandResponseDto? thumbResult;
        try
        {
            thumbResult = JsonSerializer.Deserialize<ProjectCommandResponseDto>(daemonResponse, JsonOptions);
        }
        catch
        {
            return Failed(request.RequestId, "failed to parse export-thumbnail response");
        }

        if (thumbResult is null || !thumbResult.Ok)
        {
            return Failed(request.RequestId, thumbResult?.Message ?? "export-thumbnail failed");
        }

        // Parse the thumbnail payload to get the exported path.
        string? thumbnailPath = null;
        string? assetType = null;
        long fileSizeBytes = 0;
        if (!string.IsNullOrEmpty(thumbResult.Content))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(thumbResult.Content, JsonOptions);
                thumbnailPath = payload.TryGetProperty("exportedPath", out var ep) ? ep.GetString() : null;
                assetType = payload.TryGetProperty("assetType", out var at) ? at.GetString() : null;
                fileSizeBytes = payload.TryGetProperty("fileSizeBytes", out var fs) && fs.ValueKind == JsonValueKind.Number
                    ? fs.GetInt64() : 0;
            }
            catch
            {
                // fall through — thumbnailPath stays null
            }
        }

        if (string.IsNullOrEmpty(thumbnailPath) || !File.Exists(thumbnailPath))
        {
            // No visual preview available — return metadata only.
            return new ExecV2Response(
                ExecV2Status.Completed,
                request.RequestId,
                Message: "no visual preview available; metadata returned",
                Result: new { assetPath, assetType, fileSizeBytes, description = (string?)null });
        }

        // ── Phase 2: Run Python captioning script ───────────────────────
        EnsureScriptExtracted();

        var modelCached = Directory.Exists(BlipModelCacheDir);
        var timeout = modelCached ? CachedRunTimeout : FirstRunTimeout;

        var processResult = await RunPythonScriptAsync(thumbnailPath, engine, timeout, ct);

        // Clean up thumbnail regardless of outcome.
        TryDeleteFile(thumbnailPath);

        if (processResult.ExitCode != 0)
        {
            var errorDetail = !string.IsNullOrWhiteSpace(processResult.Stderr)
                ? processResult.Stderr
                : processResult.Stdout;
            return Failed(request.RequestId, $"describe script failed (exit {processResult.ExitCode}): {Truncate(errorDetail, 500)}");
        }

        // Parse Python JSON output.
        try
        {
            var captionResult = JsonSerializer.Deserialize<JsonElement>(processResult.Stdout, JsonOptions);
            if (captionResult.TryGetProperty("error", out var errProp))
            {
                return Failed(request.RequestId, $"describe script error: {errProp.GetString()}");
            }

            var description = captionResult.TryGetProperty("description", out var descProp) ? descProp.GetString() : null;
            var usedEngine = captionResult.TryGetProperty("engine", out var engProp) ? engProp.GetString() : engine;
            var model = captionResult.TryGetProperty("model", out var modProp) ? modProp.GetString() : null;

            return new ExecV2Response(
                ExecV2Status.Completed,
                request.RequestId,
                Message: "asset described",
                Result: new
                {
                    assetPath,
                    assetType,
                    fileSizeBytes,
                    description,
                    engine = usedEngine,
                    model
                });
        }
        catch
        {
            return Failed(request.RequestId, $"failed to parse script output: {Truncate(processResult.Stdout, 300)}");
        }
    }

    private async Task<ExecV2Response> DryRunAsync(
        string requestId, string assetPath, string engine, ProjectDaemonBridge projectBridge)
    {
        // Validate asset exists via daemon healthcheck-style probe.
        var probeDto = new ProjectCommandRequestDto("export-thumbnail", assetPath, null, null, requestId);
        var serialized = JsonSerializer.Serialize(probeDto, JsonOptions);
        projectBridge.TryHandle($"PROJECT_CMD {serialized}", out var probeResponse);

        ProjectCommandResponseDto? probeResult = null;
        try { probeResult = JsonSerializer.Deserialize<ProjectCommandResponseDto>(probeResponse ?? "", JsonOptions); }
        catch { /* ignore */ }

        var assetExists = probeResult?.Ok == true;
        var uvAvailable = await IsCommandAvailableAsync("uv");
        var pythonAvailable = await IsCommandAvailableAsync("python3");
        var modelCached = Directory.Exists(BlipModelCacheDir);

        return new ExecV2Response(
            ExecV2Status.Completed,
            requestId,
            Message: "dry-run: asset.describe pre-flight check",
            Result: new
            {
                assetPath,
                assetExists,
                engine,
                uvAvailable,
                pythonAvailable,
                modelCached,
                estimatedModelSizeMb = modelCached ? 0 : 990,
                note = !uvAvailable && !pythonAvailable
                    ? "python3 or uv is required. Run 'unifocl init' to install dependencies."
                    : modelCached
                        ? "model cached; inference should complete in ~5-15s"
                        : "first run will download ~990 MB model; expect ~1-5 min depending on connection"
            });
    }

    // ──────────────────────────────────────────────────────────────────
    //  Python script management
    // ──────────────────────────────────────────────────────────────────

    private static void EnsureScriptExtracted()
    {
        if (File.Exists(ScriptPath))
        {
            // Check embedded resource version stamp.
            var stamp = ScriptPath + ".version";
            var currentVersion = typeof(AssetDescribeService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
            if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == currentVersion)
            {
                return; // already up to date
            }
        }

        Directory.CreateDirectory(ScriptDir);

        using var stream = typeof(AssetDescribeService).Assembly
            .GetManifestResourceStream("Scripts/describe_asset.py");
        if (stream is null)
        {
            throw new InvalidOperationException("embedded describe_asset.py resource not found");
        }

        using var fs = File.Create(ScriptPath);
        stream.CopyTo(fs);

        // Write version stamp.
        var version = typeof(AssetDescribeService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        File.WriteAllText(ScriptPath + ".version", version);
    }

    private static async Task<ProcessResult> RunPythonScriptAsync(
        string imagePath, string engine, TimeSpan timeout, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "uv",
            Arguments = $"run --script \"{ScriptPath}\" --image \"{imagePath}\" --engine {engine} --output json",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ProcessResult(-1, string.Empty, "failed to start uv process");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* best effort */ }
                return new ProcessResult(-1, string.Empty, "process timed out");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, $"failed to run describe script: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Utilities
    // ──────────────────────────────────────────────────────────────────

    private static async Task<bool> IsCommandAvailableAsync(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";

    private static string? GetStringArg(JsonElement? args, string key)
    {
        if (args is null || args.Value.ValueKind != JsonValueKind.Object) return null;
        return args.Value.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() : null;
    }

    private static ExecV2Response Failed(string requestId, string message)
        => new(ExecV2Status.Failed, requestId, Message: message);
}
