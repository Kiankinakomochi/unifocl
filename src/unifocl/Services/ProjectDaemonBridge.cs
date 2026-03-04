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
            "rename-asset" => HandleRenameAsset(request),
            "remove-asset" => HandleRemoveAsset(request),
            "load-asset" => HandleLoadAsset(request),
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
        if (extension.Equals(".unity", StringComparison.OrdinalIgnoreCase))
        {
            return new ProjectCommandResponseDto(
                false,
                $"{StubbedBridgePrefix} scene load is unavailable without Unity editor bridge: {assetPath}",
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
            $"unsupported asset type: {extension} (supported: .unity, .cs)",
            null,
            null);
    }

    private static bool IsValidAssetPath(string? assetPath)
    {
        return !string.IsNullOrWhiteSpace(assetPath)
               && assetPath.StartsWith("Assets/", StringComparison.Ordinal)
               && !assetPath.Contains("..", StringComparison.Ordinal);
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
}
