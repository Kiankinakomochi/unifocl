using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed partial class ProjectViewService
{
    private static bool TryLoadManifest(
        string projectPath,
        out string manifestPath,
        out JsonObject root,
        out JsonObject dependencies,
        out string error)
    {
        manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
        root = new JsonObject();
        dependencies = new JsonObject();
        error = string.Empty;

        try
        {
            if (File.Exists(manifestPath))
            {
                var parsed = JsonNode.Parse(File.ReadAllText(manifestPath));
                if (parsed is not JsonObject parsedObject)
                {
                    error = "manifest.json root must be a JSON object";
                    return false;
                }

                root = parsedObject;
            }

            if (root["dependencies"] is JsonObject existingDependencies)
            {
                dependencies = existingDependencies;
            }
            else
            {
                dependencies = new JsonObject();
                root["dependencies"] = dependencies;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to read Packages/manifest.json ({ex.Message})";
            return false;
        }
    }

    private static bool TrySaveManifest(string manifestPath, JsonObject root, out string error)
    {
        error = string.Empty;
        try
        {
            var parent = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var content = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(manifestPath, content + Environment.NewLine);
            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to write Packages/manifest.json ({ex.Message})";
            return false;
        }
    }

    private static async Task<UpmLockValidationResult> ValidatePackagesLockAfterMutationAsync(
        string projectPath,
        PackagesLockState beforeLock,
        DateTime mutationAtUtc,
        string requestedPackageId,
        UpmInstallSpec? expectedSpec,
        bool expectRemoval)
    {
        var manifestCheck = await ValidateManifestMutationAsync(
            projectPath,
            mutationAtUtc,
            requestedPackageId,
            expectedSpec,
            expectRemoval);
        if (!manifestCheck.Ok)
        {
            return UpmLockValidationResult.Fail(manifestCheck.Message);
        }

        // Manifest is the source of truth. Lock file metadata is best-effort enrichment only.
        var timeout = ResolveEnvMilliseconds("UNIFOCL_UPM_LOCK_ENRICH_TIMEOUT_MS", 1_500, min: 0, max: 10_000);
        if (timeout <= 0)
        {
            return UpmLockValidationResult.Success(
                manifestCheck.ResolvedVersion,
                manifestCheck.ResolvedSource,
                null,
                manifestCheck.DirectCount,
                0);
        }

        var grace = ResolveEnvMilliseconds("UNIFOCL_UPM_LOCK_GRACE_MS", 150, min: 50, max: 2_000);
        var poll = ResolveEnvMilliseconds("UNIFOCL_UPM_LOCK_POLL_MS", 150, min: 50, max: 1_000);

        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (DateTime.UtcNow <= deadline)
        {
            var current = CapturePackagesLockState(projectPath);
            if (DidPackagesLockChange(beforeLock, current))
            {
                await Task.Delay(grace);
                if (!TryReadPackagesLockGraph(projectPath, out var graph, out var readError))
                {
                    await Task.Delay(150);
                    if (!TryReadPackagesLockGraph(projectPath, out graph, out readError))
                    {
                        return UpmLockValidationResult.Success(
                            manifestCheck.ResolvedVersion,
                            manifestCheck.ResolvedSource,
                            null,
                            manifestCheck.DirectCount,
                            0);
                    }
                }

                var resolvedGraph = ValidateResolvedGraph(graph, requestedPackageId, expectedSpec, expectRemoval);
                if (resolvedGraph.Ok)
                {
                    return resolvedGraph;
                }

                return UpmLockValidationResult.Success(
                    manifestCheck.ResolvedVersion,
                    manifestCheck.ResolvedSource,
                    null,
                    manifestCheck.DirectCount,
                    graph.TransitiveCount);
            }

            await Task.Delay(poll);
        }

        return UpmLockValidationResult.Success(
            manifestCheck.ResolvedVersion,
            manifestCheck.ResolvedSource,
            null,
            manifestCheck.DirectCount,
            0);
    }

    private static async Task<ManifestMutationValidationResult> ValidateManifestMutationAsync(
        string projectPath,
        DateTime mutationAtUtc,
        string requestedPackageId,
        UpmInstallSpec? expectedSpec,
        bool expectRemoval)
    {
        var timeout = ResolveEnvMilliseconds("UNIFOCL_UPM_MANIFEST_TIMEOUT_MS", 4_000, min: 500, max: 30_000);
        var poll = ResolveEnvMilliseconds("UNIFOCL_UPM_MANIFEST_POLL_MS", 100, min: 25, max: 1_000);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);

        while (DateTime.UtcNow <= deadline)
        {
            if (TryLoadManifest(projectPath, out _, out _, out var dependencies, out _))
            {
                if (expectRemoval)
                {
                    if (!dependencies.ContainsKey(requestedPackageId))
                    {
                        return ManifestMutationValidationResult.Success(
                            dependencies.Count,
                            null,
                            "manifest");
                    }
                }
                else if (TryGetManifestDependencyValue(dependencies, requestedPackageId, out var value))
                {
                    var expectedValue = expectedSpec?.ManifestValue ?? string.Empty;
                    var valueMatches = string.IsNullOrWhiteSpace(expectedValue)
                                       || string.Equals(value, expectedValue, StringComparison.Ordinal);
                    if (valueMatches)
                    {
                        var resolvedVersion = expectedSpec?.TargetType.Equals("registry", StringComparison.OrdinalIgnoreCase) == true
                            ? value
                            : expectedSpec?.DisplayVersion;
                        var resolvedSource = string.IsNullOrWhiteSpace(expectedSpec?.Source)
                            ? ResolveManifestSource(value)
                            : expectedSpec!.Source;
                        return ManifestMutationValidationResult.Success(
                            dependencies.Count,
                            resolvedVersion,
                            resolvedSource);
                    }
                }
            }

            await Task.Delay(poll);
        }

        var logHints = ReadUpmLogErrorHints(mutationAtUtc, maxLines: 4);
        var hintSuffix = logHints.Count == 0
            ? string.Empty
            : $" upm.log hints: {string.Join(" | ", logHints)}";
        if (expectRemoval)
        {
            return ManifestMutationValidationResult.Fail(
                $"manifest confirmation timed out after {timeout}ms: package is still present: {requestedPackageId}.{hintSuffix}");
        }

        return ManifestMutationValidationResult.Fail(
            $"manifest confirmation timed out after {timeout}ms: package was not observed with expected value: {requestedPackageId}.{hintSuffix}");
    }

    private static bool TryGetManifestDependencyValue(JsonObject dependencies, string packageId, out string value)
    {
        value = string.Empty;
        if (!dependencies.TryGetPropertyValue(packageId, out var node) || node is not JsonValue jsonValue)
        {
            return false;
        }

        if (!jsonValue.TryGetValue<string>(out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        value = rawValue;
        return true;
    }

    private static UpmLockValidationResult ValidateResolvedGraph(
        PackagesLockGraph graph,
        string requestedPackageId,
        UpmInstallSpec? expectedSpec,
        bool expectRemoval)
    {
        if (expectRemoval)
        {
            if (!graph.PackagesById.TryGetValue(requestedPackageId, out var removalEntry) || removalEntry.Depth > 0)
            {
                return UpmLockValidationResult.Success(null, null, null, graph.DirectCount, graph.TransitiveCount);
            }

            return UpmLockValidationResult.Fail($"removed package is still present as a direct dependency in packages-lock.json: {requestedPackageId}");
        }

        if (!graph.PackagesById.TryGetValue(requestedPackageId, out var entry))
        {
            return UpmLockValidationResult.Fail($"requested package was not resolved in packages-lock.json: {requestedPackageId}");
        }

        if (entry.Depth != 0)
        {
            return UpmLockValidationResult.Fail($"resolved package depth is {entry.Depth}, expected 0 for direct dependency: {requestedPackageId}");
        }

        if (expectedSpec is not null)
        {
            if (expectedSpec.TargetType.Equals("registry", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(entry.Version)
                    || !entry.Version.Equals(expectedSpec.DisplayVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return UpmLockValidationResult.Fail($"registry package version mismatch: expected {expectedSpec.DisplayVersion}, resolved {entry.Version}");
                }
            }

            if (expectedSpec.TargetType.Equals("git", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(entry.Hash))
            {
                return UpmLockValidationResult.Fail("git package resolved without commit hash in packages-lock.json");
            }
        }

        return UpmLockValidationResult.Success(
            entry.Version,
            entry.Source,
            entry.Hash,
            graph.DirectCount,
            graph.TransitiveCount);
    }

    private static PackagesLockState CapturePackagesLockState(string projectPath)
    {
        var lockPath = Path.Combine(projectPath, "Packages", "packages-lock.json");
        if (!File.Exists(lockPath))
        {
            return new PackagesLockState(false, DateTime.MinValue, 0);
        }

        var info = new FileInfo(lockPath);
        return new PackagesLockState(true, info.LastWriteTimeUtc, info.Length);
    }

    private static bool DidPackagesLockChange(PackagesLockState before, PackagesLockState after)
    {
        if (!before.Exists && after.Exists)
        {
            return true;
        }

        if (!before.Exists || !after.Exists)
        {
            return false;
        }

        return after.LastWriteTimeUtc > before.LastWriteTimeUtc || after.Length != before.Length;
    }

    private static bool TryReadPackagesLockGraph(string projectPath, out PackagesLockGraph graph, out string? error)
    {
        graph = new PackagesLockGraph([], new Dictionary<string, PackagesLockEntry>(StringComparer.OrdinalIgnoreCase), 0, 0);
        error = null;

        var lockPath = Path.Combine(projectPath, "Packages", "packages-lock.json");
        if (!File.Exists(lockPath))
        {
            error = "Packages/packages-lock.json not found";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
            if (!document.RootElement.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object)
            {
                error = "packages-lock.json does not contain dependencies object";
                return false;
            }

            var all = new List<PackagesLockEntry>();
            var map = new Dictionary<string, PackagesLockEntry>(StringComparer.OrdinalIgnoreCase);
            var directCount = 0;
            var transitiveCount = 0;

            foreach (var property in dependencies.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var version = TryGetString(property.Value, "version") ?? string.Empty;
                var sourceRaw = TryGetString(property.Value, "source") ?? "unknown";
                var source = NormalizeLockSource(sourceRaw);
                var hash = TryGetString(property.Value, "hash")
                           ?? TryGetString(property.Value, "revision")
                           ?? string.Empty;
                var depth = TryGetInt(property.Value, "depth") ?? 0;

                if (depth <= 0)
                {
                    directCount++;
                }
                else
                {
                    transitiveCount++;
                }

                var entry = new PackagesLockEntry(property.Name, version, source, hash, depth);
                all.Add(entry);
                map[property.Name] = entry;
            }

            graph = new PackagesLockGraph(all, map, directCount, transitiveCount);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static List<string> ReadUpmLogErrorHints(DateTime sinceUtc, int maxLines)
    {
        var path = ResolveUpmLogPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return [];
        }

        try
        {
            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc < sinceUtc.AddMinutes(-2))
            {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= 0)
            {
                return [];
            }

            var readLength = (int)Math.Min(stream.Length, 256 * 1024);
            stream.Seek(-readLength, SeekOrigin.End);
            var buffer = new byte[readLength];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead <= 0)
            {
                return [];
            }

            var tail = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
            return tail
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.Contains("[error]", StringComparison.OrdinalIgnoreCase)
                               || line.Contains("[warning]", StringComparison.OrdinalIgnoreCase))
                .TakeLast(Math.Max(1, maxLines))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string? ResolveUpmLogPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "Unity", "Editor", "upm.log");
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Logs", "Unity", "upm.log");
        }

        return Path.Combine(home, ".config", "unity3d", "upm.log");
    }

    private static int ResolveEnvMilliseconds(string name, int defaultValue, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (!int.TryParse(raw, out var parsed))
        {
            return defaultValue;
        }

        return Math.Clamp(parsed, min, max);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (!property.TryGetInt32(out var value))
        {
            return null;
        }

        return value;
    }

    private static string NormalizeLockSource(string rawSource)
    {
        if (rawSource.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            return "Git";
        }

        if (rawSource.Equals("registry", StringComparison.OrdinalIgnoreCase))
        {
            return "Registry";
        }

        if (rawSource.Equals("builtIn", StringComparison.OrdinalIgnoreCase)
            || rawSource.Equals("builtin", StringComparison.OrdinalIgnoreCase))
        {
            return "BuiltIn";
        }

        if (rawSource.Equals("embedded", StringComparison.OrdinalIgnoreCase)
            || rawSource.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            return "Local";
        }

        return rawSource;
    }

    private static string ResolveManifestSource(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "Unknown";
        }

        if (version.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return "Local";
        }

        if (version.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
            || version.Contains(".git", StringComparison.OrdinalIgnoreCase)
            || version.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || version.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "Git";
        }

        return "Registry";
    }
}
