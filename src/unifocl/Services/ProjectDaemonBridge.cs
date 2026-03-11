using System.Text.Json;

internal sealed class ProjectDaemonBridge
{
    public const string StubbedBridgePrefix = "stubbed bridge:";

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _projectPath;

    public ProjectDaemonBridge(string? projectPath)
    {
        _projectPath = string.IsNullOrWhiteSpace(projectPath)
            ? Directory.GetCurrentDirectory()
            : projectPath;
    }

    public bool TryHandle(string? commandLine, out string response)
    {
        response = string.Empty;
        if (string.IsNullOrWhiteSpace(commandLine) || !commandLine.StartsWith("PROJECT_CMD ", StringComparison.Ordinal))
        {
            return false;
        }

        var payload = commandLine["PROJECT_CMD ".Length..];
        ProjectCommandRequestDto? request;
        try
        {
            request = JsonSerializer.Deserialize<ProjectCommandRequestDto>(payload, _jsonOptions);
        }
        catch (JsonException)
        {
            response = SerializeError($"{StubbedBridgePrefix} invalid project command payload");
            return true;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Action))
        {
            response = SerializeError($"{StubbedBridgePrefix} missing project command payload");
            return true;
        }

        var result = request.Action switch
        {
            "mk-script" => HandleCreateScript(request),
            "mk-asset" => HandleCreateAsset(request),
            "rename-asset" => HandleRenameAsset(request),
            "remove-asset" => HandleRemoveAsset(request),
            "load-asset" => HandleLoadAsset(request),
            "upm-list" => HandleUpmList(request),
            "upm-install" => RequireBridgeMode("upm-install"),
            "upm-remove" => RequireBridgeMode("upm-remove"),
            "build-run" => RequireBridgeMode("build-run"),
            "build-exec" => RequireBridgeMode("build-exec"),
            "build-scenes-get" => RequireBridgeMode("build-scenes-get"),
            "build-scenes-set" => RequireBridgeMode("build-scenes-set"),
            "build-addressables" => RequireBridgeMode("build-addressables"),
            "build-targets" => HandleBuildTargetsStub(),
            "build-cancel" => HandleBuildCancelStub(),
            _ => new ProjectCommandResponseDto(false, $"{StubbedBridgePrefix} unsupported action: {request.Action}", null, null)
        };
        response = JsonSerializer.Serialize(result, _jsonOptions);
        return true;
    }

    private ProjectCommandResponseDto HandleCreateScript(ProjectCommandRequestDto request)
    {
        if (!IsValidAssetPath(request.AssetPath) || string.IsNullOrWhiteSpace(request.Content))
        {
            return new ProjectCommandResponseDto(false, $"{StubbedBridgePrefix} mk-script requires assetPath and content", null, null);
        }

        var absolutePath = ResolveAssetPath(request.AssetPath!);
        if (File.Exists(absolutePath))
        {
            return new ProjectCommandResponseDto(false, $"asset already exists: {request.AssetPath}", null, null);
        }

        try
        {
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(absolutePath, request.Content);
            return new ProjectCommandResponseDto(true, "script created (stubbed bridge fallback)", "script", null);
        }
        catch (Exception ex)
        {
            return new ProjectCommandResponseDto(false, $"failed to create script: {ex.Message}", null, null);
        }
    }

    private ProjectCommandResponseDto HandleCreateAsset(ProjectCommandRequestDto request)
    {
        if (!IsValidAssetPath(request.AssetPath) || string.IsNullOrWhiteSpace(request.Content))
        {
            return new ProjectCommandResponseDto(false, $"{StubbedBridgePrefix} mk-asset requires assetPath and content", null, null);
        }

        MkAssetRequestPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<MkAssetRequestPayload>(request.Content, _jsonOptions);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Type))
        {
            return new ProjectCommandResponseDto(false, $"{StubbedBridgePrefix} mk-asset requires type", null, null);
        }

        var count = payload.Count <= 0 ? 1 : Math.Min(payload.Count, 100);
        var created = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var relativePath = BuildStubAssetPath(
                request.AssetPath!,
                payload.Type,
                payload.Name,
                i,
                count);
            relativePath = EnsureUniqueStubAssetPath(relativePath);
            var absolutePath = ResolveAssetPath(relativePath);
            try
            {
                if (relativePath.EndsWith("/", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(absolutePath);
                    created.Add(relativePath.TrimEnd('/'));
                    continue;
                }

                var directory = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(absolutePath))
                {
                    return new ProjectCommandResponseDto(false, $"asset already exists: {relativePath}", null, null);
                }

                File.WriteAllText(absolutePath, $"// generated by unifocl stub bridge: {payload.Type}");
                created.Add(relativePath);
            }
            catch (Exception ex)
            {
                return new ProjectCommandResponseDto(false, $"failed to create asset: {ex.Message}", null, null);
            }
        }

        var responseContent = JsonSerializer.Serialize(new MkAssetResponsePayload(created), _jsonOptions);
        return new ProjectCommandResponseDto(true, "asset(s) created (stubbed bridge fallback)", "asset", responseContent);
    }

    private ProjectCommandResponseDto HandleRenameAsset(ProjectCommandRequestDto request)
    {
        if (!IsValidAssetPath(request.AssetPath) || !IsValidAssetPath(request.NewAssetPath))
        {
            return new ProjectCommandResponseDto(false, $"{StubbedBridgePrefix} rename-asset requires assetPath and newAssetPath", null, null);
        }

        var sourcePath = ResolveAssetPath(request.AssetPath!);
        var targetPath = ResolveAssetPath(request.NewAssetPath!);
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            return new ProjectCommandResponseDto(false, $"asset not found: {request.AssetPath}", null, null);
        }

        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            return new ProjectCommandResponseDto(false, $"target already exists: {request.NewAssetPath}", null, null);
        }

        try
        {
            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            if (Directory.Exists(sourcePath))
            {
                Directory.Move(sourcePath, targetPath);
            }
            else
            {
                File.Move(sourcePath, targetPath);
            }

            MoveMetaIfPresent(sourcePath, targetPath);
            return new ProjectCommandResponseDto(true, "asset renamed (stubbed bridge fallback)", null, null);
        }
        catch (Exception ex)
        {
            return new ProjectCommandResponseDto(false, $"failed to rename asset: {ex.Message}", null, null);
        }
    }

    private ProjectCommandResponseDto HandleRemoveAsset(ProjectCommandRequestDto request)
    {
        if (!IsValidAssetPath(request.AssetPath))
        {
            return new ProjectCommandResponseDto(false, $"{StubbedBridgePrefix} remove-asset requires assetPath", null, null);
        }

        var absolutePath = ResolveAssetPath(request.AssetPath!);
        if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
        {
            return new ProjectCommandResponseDto(false, $"asset not found: {request.AssetPath}", null, null);
        }

        try
        {
            if (Directory.Exists(absolutePath))
            {
                Directory.Delete(absolutePath, recursive: true);
            }
            else
            {
                File.Delete(absolutePath);
            }

            DeleteMetaIfPresent(absolutePath);
            return new ProjectCommandResponseDto(true, "asset removed (stubbed bridge fallback)", null, null);
        }
        catch (Exception ex)
        {
            return new ProjectCommandResponseDto(false, $"failed to remove asset: {ex.Message}", null, null);
        }
    }

    private ProjectCommandResponseDto HandleLoadAsset(ProjectCommandRequestDto request)
    {
        if (!IsValidAssetPath(request.AssetPath))
        {
            return new ProjectCommandResponseDto(false, $"{StubbedBridgePrefix} load-asset requires assetPath", null, null);
        }

        var assetPath = request.AssetPath!;
        var absolutePath = ResolveAssetPath(assetPath);
        if (!File.Exists(absolutePath))
        {
            return new ProjectCommandResponseDto(false, $"asset not found: {request.AssetPath}", null, null);
        }

        var extension = Path.GetExtension(assetPath);
        if (extension.Equals(".unity", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            var kind = extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase) ? "prefab" : "scene";
            return new ProjectCommandResponseDto(
                false,
                $"{StubbedBridgePrefix} {kind} load is unavailable without Bridge mode: {assetPath}",
                null,
                null);
        }

        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return new ProjectCommandResponseDto(
                true,
                "script path resolved (stubbed bridge fallback; Unity script open unavailable)",
                "script",
                null);
        }

        return new ProjectCommandResponseDto(
            false,
            $"unsupported asset type: {extension} (supported: .unity, .prefab, .cs)",
            null,
            null);
    }

    private ProjectCommandResponseDto HandleUpmList(ProjectCommandRequestDto request)
    {
        UpmListRequestPayload? payload = null;
        if (!string.IsNullOrWhiteSpace(request.Content))
        {
            try
            {
                payload = JsonSerializer.Deserialize<UpmListRequestPayload>(request.Content, _jsonOptions);
            }
            catch (JsonException)
            {
                payload = null;
            }
        }

        var includeOutdated = payload?.IncludeOutdated ?? false;
        var includeBuiltin = payload?.IncludeBuiltin ?? false;
        var includeGit = payload?.IncludeGit ?? false;
        var manifestPath = Path.Combine(_projectPath, "Packages", "manifest.json");
        if (!File.Exists(manifestPath))
        {
            var empty = JsonSerializer.Serialize(new UpmListResponsePayload([]), _jsonOptions);
            return new ProjectCommandResponseDto(true, "upm package list loaded from manifest", "upm-list", empty);
        }

        List<UpmListPackagePayload> packages;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object)
            {
                packages = [];
            }
            else
            {
                packages = dependencies
                    .EnumerateObject()
                    .Where(property => property.Value.ValueKind == JsonValueKind.String)
                    .Select(property =>
                    {
                        var version = property.Value.GetString() ?? string.Empty;
                        var source = ResolveUpmSource(version);
                        return new UpmListPackagePayload(
                            property.Name,
                            property.Name,
                            version,
                            source,
                            null,
                            false,
                            false,
                            false);
                    })
                    .Where(package =>
                        (!includeGit || string.Equals(package.Source, "Git", StringComparison.OrdinalIgnoreCase))
                        && (includeBuiltin || !string.Equals(package.Source, "BuiltIn", StringComparison.OrdinalIgnoreCase))
                        && (!includeOutdated || package.IsOutdated))
                    .OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            return new ProjectCommandResponseDto(false, $"failed to read package manifest: {ex.Message}", null, null);
        }

        var responsePayload = JsonSerializer.Serialize(new UpmListResponsePayload(packages), _jsonOptions);
        return new ProjectCommandResponseDto(true, "upm package list loaded from manifest", "upm-list", responsePayload);
    }

    private ProjectCommandResponseDto HandleBuildTargetsStub()
    {
        var payload = JsonSerializer.Serialize(
            new BuildTargetsResponsePayload(
            [
                new BuildTargetPayload("Win64", true, "StandaloneWindows64"),
                new BuildTargetPayload("Android", true, "Android"),
                new BuildTargetPayload("iOS", true, "iOS"),
                new BuildTargetPayload("WebGL", true, "WebGL"),
                new BuildTargetPayload("macOS", true, "StandaloneOSX"),
                new BuildTargetPayload("Linux", true, "StandaloneLinux64")
            ]),
            _jsonOptions);
        return new ProjectCommandResponseDto(
            false,
            $"{StubbedBridgePrefix} build-targets metadata is synthetic in Host mode; attach Bridge mode for authoritative build support state",
            "build-targets",
            payload);
    }

    private ProjectCommandResponseDto HandleBuildCancelStub()
    {
        return new ProjectCommandResponseDto(
            true,
            "build cancel acknowledged (stubbed host mode had no active build worker)",
            "build",
            null);
    }

    private ProjectCommandResponseDto RequireBridgeMode(string action)
    {
        return new ProjectCommandResponseDto(
            false,
            $"{StubbedBridgePrefix} {action} requires Bridge mode",
            null,
            null);
    }

    private static bool IsValidAssetPath(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath) || assetPath.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return assetPath.Equals("Assets", StringComparison.Ordinal)
               || assetPath.Equals("Assets/", StringComparison.Ordinal)
               || assetPath.StartsWith("Assets/", StringComparison.Ordinal);
    }

    private string ResolveAssetPath(string assetPath)
    {
        return Path.GetFullPath(Path.Combine(_projectPath, assetPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static void MoveMetaIfPresent(string sourcePath, string targetPath)
    {
        var sourceMeta = sourcePath + ".meta";
        var targetMeta = targetPath + ".meta";
        if (!File.Exists(sourceMeta))
        {
            return;
        }

        if (File.Exists(targetMeta))
        {
            File.Delete(targetMeta);
        }

        File.Move(sourceMeta, targetMeta);
    }

    private static void DeleteMetaIfPresent(string absolutePath)
    {
        var metaPath = absolutePath + ".meta";
        if (File.Exists(metaPath))
        {
            File.Delete(metaPath);
        }
    }

    private string SerializeError(string message)
    {
        return JsonSerializer.Serialize(new ProjectCommandResponseDto(false, message, null, null), _jsonOptions);
    }

    private static string BuildStubAssetPath(string cwd, string type, string? baseName, int index, int count)
    {
        var ext = ResolveStubExtension(type);
        var resolvedBase = string.IsNullOrWhiteSpace(baseName)
            ? $"New{type}"
            : baseName.Trim();
        var finalName = count > 1 ? $"{resolvedBase}_{index + 1}" : resolvedBase;
        if (type.Equals("Folder", StringComparison.OrdinalIgnoreCase))
        {
            return $"{cwd.TrimEnd('/', '\\')}/{finalName}/".Replace('\\', '/');
        }

        return $"{cwd.TrimEnd('/', '\\')}/{finalName}{ext}".Replace('\\', '/');
    }

    private static string ResolveStubExtension(string type)
    {
        return ProjectMkCatalog.ResolveDefaultExtension(type);
    }

    private string EnsureUniqueStubAssetPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var isFolder = normalized.EndsWith("/", StringComparison.Ordinal);
        var trimmed = isFolder ? normalized.TrimEnd('/') : normalized;
        var directory = Path.GetDirectoryName(trimmed)?.Replace('\\', '/') ?? "Assets";
        var fileName = Path.GetFileName(trimmed);
        var stem = isFolder ? fileName : Path.GetFileNameWithoutExtension(fileName);
        var extension = isFolder ? string.Empty : Path.GetExtension(fileName);

        var candidateStem = stem;
        var suffix = 1;
        while (true)
        {
            var candidate = isFolder
                ? $"{directory}/{candidateStem}/"
                : $"{directory}/{candidateStem}{extension}";
            var absolutePath = ResolveAssetPath(candidate.TrimEnd('/'));
            if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
            {
                return candidate.Replace('\\', '/');
            }

            candidateStem = $"{stem}_{suffix++}";
        }
    }

    private sealed record MkAssetRequestPayload(string Type, int Count, string? Name);
    private sealed record MkAssetResponsePayload(List<string> CreatedPaths);
    private sealed record UpmListRequestPayload(bool IncludeOutdated, bool IncludeBuiltin, bool IncludeGit);
    private sealed record UpmListResponsePayload(List<UpmListPackagePayload>? Packages);
    private sealed record UpmListPackagePayload(
        string? PackageId,
        string? DisplayName,
        string? Version,
        string? Source,
        string? LatestCompatibleVersion,
        bool IsOutdated,
        bool IsDeprecated,
        bool IsPreview);
    private sealed record BuildTargetsResponsePayload(List<BuildTargetPayload> Targets);
    private sealed record BuildTargetPayload(string Name, bool Installed, string? Note);

    private static string ResolveUpmSource(string version)
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
