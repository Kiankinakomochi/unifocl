using System.Text.Json;

internal sealed class HierarchyDaemonBridge
{
    private const int RootNodeId = 1;
    private const int MaxNodeCount = 4000;
    private const int MaxTreeDepth = 10;

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _projectPath;
    private readonly string _assetsRoot;

    public HierarchyDaemonBridge(string? projectPath)
    {
        _projectPath = string.IsNullOrWhiteSpace(projectPath)
            ? Directory.GetCurrentDirectory()
            : projectPath;
        _assetsRoot = Path.GetFullPath(Path.Combine(_projectPath, "Assets"));
    }

    public bool TryHandle(string? commandLine, out string response)
    {
        response = string.Empty;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var command = commandLine.Trim();
        if (string.Equals(command, "HIERARCHY_GET", StringComparison.Ordinal))
        {
            var snapshot = BuildSnapshot();
            response = JsonSerializer.Serialize(snapshot, _jsonOptions);
            return true;
        }

        if (command.StartsWith("HIERARCHY_FIND ", StringComparison.Ordinal))
        {
            response = JsonSerializer.Serialize(HandleSearch(command["HIERARCHY_FIND ".Length..]), _jsonOptions);
            return true;
        }

        if (command.StartsWith("HIERARCHY_CMD ", StringComparison.Ordinal))
        {
            response = JsonSerializer.Serialize(HandleCommand(command["HIERARCHY_CMD ".Length..]), _jsonOptions);
            return true;
        }

        return false;
    }

    private HierarchySnapshotDto BuildSnapshot()
    {
        _ = TryBuildTree(out var root, out _);
        return new HierarchySnapshotDto("HostMode/Assets", ComputeSnapshotVersion(), root);
    }

    private HierarchySearchResponseDto HandleSearch(string payload)
    {
        HierarchySearchRequestDto? request;
        try
        {
            request = JsonSerializer.Deserialize<HierarchySearchRequestDto>(payload, _jsonOptions);
        }
        catch (JsonException)
        {
            return new HierarchySearchResponseDto(false, [], "invalid hierarchy search payload");
        }

        if (request is null)
        {
            return new HierarchySearchResponseDto(false, [], "missing hierarchy search payload");
        }

        _ = TryBuildTree(out _, out var index);
        var limit = request.Limit <= 0 ? 20 : Math.Min(request.Limit, 200);
        var query = request.Query?.Trim() ?? string.Empty;
        if (request.ParentId is int requestedParentId && !index.ContainsKey(requestedParentId))
        {
            return new HierarchySearchResponseDto(false, [], $"parent not found: {requestedParentId}");
        }

        var parentPathPrefix = request.ParentId is int parentId && index.TryGetValue(parentId, out var parentNode)
            ? parentNode.Path
            : null;
        if (query.Length == 0)
        {
            var defaultResults = index.Values
                .Where(node => node.Id != RootNodeId)
                .Where(node => string.IsNullOrWhiteSpace(parentPathPrefix)
                    || node.Path.StartsWith(parentPathPrefix!, StringComparison.OrdinalIgnoreCase))
                .OrderBy(node => node.Path, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(node => new HierarchySearchResultDto(node.Id, "/" + node.Path, true, 1d))
                .ToList();
            return new HierarchySearchResponseDto(true, defaultResults, null);
        }
        var scored = new List<HierarchySearchResultDto>();
        foreach (var node in index.Values)
        {
            if (node.Id == RootNodeId)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(parentPathPrefix)
                && !node.Path.StartsWith(parentPathPrefix!, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (FuzzyMatcher.TryScore(query, node.Path, out var pathScore))
            {
                scored.Add(new HierarchySearchResultDto(node.Id, "/" + node.Path, true, pathScore));
                continue;
            }

            if (FuzzyMatcher.TryScore(query, node.Name, out var nameScore))
            {
                scored.Add(new HierarchySearchResultDto(node.Id, "/" + node.Path, true, nameScore));
            }
        }

        var top = scored
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
        return new HierarchySearchResponseDto(true, top, null);
    }

    private HierarchyCommandResponseDto HandleCommand(string payload)
    {
        HierarchyCommandRequestDto? request;
        try
        {
            request = JsonSerializer.Deserialize<HierarchyCommandRequestDto>(payload, _jsonOptions);
        }
        catch (JsonException)
        {
            return new HierarchyCommandResponseDto(false, "invalid hierarchy command payload", null, null);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Action))
        {
            return new HierarchyCommandResponseDto(false, "missing hierarchy command payload", null, null);
        }

        return request.Action.Trim().ToLowerInvariant() switch
        {
            "toggle" => HandleToggle(request),
            "rm" => HandleRemove(request),
            "rename" => HandleRename(request),
            "mv" => HandleMove(request),
            "mk" => HandleMake(request),
            _ => new HierarchyCommandResponseDto(false, $"unsupported hierarchy action in host mode: {request.Action}", null, null)
        };
    }

    private HierarchyCommandResponseDto HandleToggle(HierarchyCommandRequestDto request)
    {
        if (request.TargetId is null)
        {
            return new HierarchyCommandResponseDto(false, "toggle requires targetId", null, null);
        }

        if (!TryBuildTree(out _, out var index) || !index.ContainsKey(request.TargetId.Value))
        {
            return new HierarchyCommandResponseDto(false, $"target not found: {request.TargetId}", null, null);
        }

        return new HierarchyCommandResponseDto(true, "host-mode hierarchy nodes are always active", request.TargetId, true);
    }

    private HierarchyCommandResponseDto HandleRemove(HierarchyCommandRequestDto request)
    {
        if (request.TargetId is null)
        {
            return new HierarchyCommandResponseDto(false, "rm requires targetId", null, null);
        }

        if (!TryBuildTree(out _, out var index) || !index.TryGetValue(request.TargetId.Value, out var node))
        {
            return new HierarchyCommandResponseDto(false, $"target not found: {request.TargetId}", null, null);
        }

        if (node.Id == RootNodeId)
        {
            return new HierarchyCommandResponseDto(false, "cannot remove Assets root", null, null);
        }

        try
        {
            if (node.IsDirectory)
            {
                Directory.Delete(node.AbsolutePath, recursive: true);
            }
            else
            {
                File.Delete(node.AbsolutePath);
            }

            DeleteMetaIfPresent(node.AbsolutePath);
            return new HierarchyCommandResponseDto(true, "removed node in host mode", null, null);
        }
        catch (Exception ex)
        {
            return new HierarchyCommandResponseDto(false, $"failed to remove target: {ex.Message}", null, null);
        }
    }

    private HierarchyCommandResponseDto HandleRename(HierarchyCommandRequestDto request)
    {
        if (request.TargetId is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return new HierarchyCommandResponseDto(false, "rename requires targetId and name", null, null);
        }

        if (!TryBuildTree(out _, out var index) || !index.TryGetValue(request.TargetId.Value, out var node))
        {
            return new HierarchyCommandResponseDto(false, $"target not found: {request.TargetId}", null, null);
        }

        if (node.Id == RootNodeId)
        {
            return new HierarchyCommandResponseDto(false, "cannot rename Assets root", null, null);
        }

        var parentPath = Directory.GetParent(node.AbsolutePath)?.FullName;
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return new HierarchyCommandResponseDto(false, "failed to resolve parent directory", null, null);
        }

        if (!TryNormalizeNodeName(request.Name, out var safeName, out var normalizeError))
        {
            return new HierarchyCommandResponseDto(false, normalizeError, null, null);
        }

        if (!node.IsDirectory && string.IsNullOrWhiteSpace(Path.GetExtension(safeName)))
        {
            var ext = Path.GetExtension(node.AbsolutePath);
            safeName += ext;
        }

        var destinationPath = Path.GetFullPath(Path.Combine(parentPath, safeName));
        if (!IsPathWithinAssets(destinationPath))
        {
            return new HierarchyCommandResponseDto(false, "rename target escaped Assets root", null, null);
        }

        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            return new HierarchyCommandResponseDto(false, $"rename target already exists: {safeName}", null, null);
        }

        try
        {
            if (node.IsDirectory)
            {
                Directory.Move(node.AbsolutePath, destinationPath);
            }
            else
            {
                File.Move(node.AbsolutePath, destinationPath);
            }

            MoveMetaIfPresent(node.AbsolutePath, destinationPath);
            return new HierarchyCommandResponseDto(true, "renamed node in host mode", request.TargetId, null);
        }
        catch (Exception ex)
        {
            return new HierarchyCommandResponseDto(false, $"failed to rename target: {ex.Message}", null, null);
        }
    }

    private HierarchyCommandResponseDto HandleMove(HierarchyCommandRequestDto request)
    {
        if (request.TargetId is null || request.ParentId is null)
        {
            return new HierarchyCommandResponseDto(false, "mv requires targetId and parentId", null, null);
        }

        if (!TryBuildTree(out _, out var index)
            || !index.TryGetValue(request.TargetId.Value, out var target)
            || !index.TryGetValue(request.ParentId.Value, out var parent))
        {
            return new HierarchyCommandResponseDto(false, "target/parent not found", null, null);
        }

        if (target.Id == RootNodeId)
        {
            return new HierarchyCommandResponseDto(false, "cannot move Assets root", null, null);
        }

        if (!parent.IsDirectory)
        {
            return new HierarchyCommandResponseDto(false, "destination parent must be a directory", null, null);
        }

        var parentDirectory = parent.AbsolutePath;
        if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
        {
            return new HierarchyCommandResponseDto(false, "destination parent directory does not exist", null, null);
        }

        var destinationPath = Path.GetFullPath(Path.Combine(parentDirectory, Path.GetFileName(target.AbsolutePath)));
        if (!IsPathWithinAssets(destinationPath))
        {
            return new HierarchyCommandResponseDto(false, "move destination escaped Assets root", null, null);
        }

        if (destinationPath.Equals(target.AbsolutePath, StringComparison.Ordinal))
        {
            return new HierarchyCommandResponseDto(false, "target already exists at destination", null, null);
        }

        if (target.IsDirectory && IsSubPath(destinationPath, target.AbsolutePath))
        {
            return new HierarchyCommandResponseDto(false, "cannot move a directory into itself or its descendants", null, null);
        }

        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            return new HierarchyCommandResponseDto(false, "destination path already exists", null, null);
        }

        try
        {
            if (target.IsDirectory)
            {
                Directory.Move(target.AbsolutePath, destinationPath);
            }
            else
            {
                File.Move(target.AbsolutePath, destinationPath);
            }

            MoveMetaIfPresent(target.AbsolutePath, destinationPath);
            return new HierarchyCommandResponseDto(true, "moved node in host mode", request.TargetId, null);
        }
        catch (Exception ex)
        {
            return new HierarchyCommandResponseDto(false, $"failed to move target: {ex.Message}", null, null);
        }
    }

    private HierarchyCommandResponseDto HandleMake(HierarchyCommandRequestDto request)
    {
        var type = string.IsNullOrWhiteSpace(request.Type) ? "Empty" : request.Type.Trim();
        var count = request.Count.GetValueOrDefault(1);
        count = Math.Clamp(count, 1, 100);
        if (!TryBuildTree(out _, out var index))
        {
            return new HierarchyCommandResponseDto(false, "failed to build host-mode hierarchy index", null, null);
        }

        var parentId = request.ParentId.GetValueOrDefault(RootNodeId);
        if (!index.TryGetValue(parentId, out var parentNode))
        {
            parentNode = index[RootNodeId];
        }

        var parentDirectory = parentNode.IsDirectory
            ? parentNode.AbsolutePath
            : (Directory.GetParent(parentNode.AbsolutePath)?.FullName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
        {
            return new HierarchyCommandResponseDto(false, "mk parent directory not found", null, null);
        }

        var rawBaseName = string.IsNullOrWhiteSpace(request.Name) ? type : request.Name!.Trim();
        if (!TryNormalizeNodeName(rawBaseName, out var baseName, out var normalizeError))
        {
            return new HierarchyCommandResponseDto(false, normalizeError, null, null);
        }

        if (type.Equals("EmptyParent", StringComparison.OrdinalIgnoreCase))
        {
            return HandleMakeEmptyParent(request, index, parentDirectory, baseName);
        }

        var spec = ResolveMakeSpec(type);
        var firstCreatedPath = string.Empty;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var suffix = count == 1 ? string.Empty : $"_{i + 1}";
                var stem = baseName + suffix;
                var fileName = spec.IsDirectory ? stem : $"{stem}{spec.Extension}";
                var createdPath = ResolveUniquePath(Path.Combine(parentDirectory, fileName), spec.IsDirectory);
                createdPath = Path.GetFullPath(createdPath);
                if (!IsPathWithinAssets(createdPath))
                {
                    return new HierarchyCommandResponseDto(false, "mk target escaped Assets root", null, null);
                }

                if (spec.IsDirectory)
                {
                    Directory.CreateDirectory(createdPath);
                }
                else
                {
                    var template = $"// host-mode hierarchy placeholder generated by unifocl ({type}){Environment.NewLine}{spec.TemplateHint}";
                    File.WriteAllText(createdPath, template + Environment.NewLine);
                }

                if (string.IsNullOrWhiteSpace(firstCreatedPath))
                {
                    firstCreatedPath = createdPath;
                }
            }

            if (!TryBuildTree(out _, out var updatedIndex))
            {
                return new HierarchyCommandResponseDto(true, $"created {count} node(s) in host mode", null, null);
            }

            var nodeId = ResolveNodeIdByAbsolutePath(updatedIndex, firstCreatedPath);
            return new HierarchyCommandResponseDto(true, $"created {count} node(s) in host mode", nodeId, null);
        }
        catch (Exception ex)
        {
            return new HierarchyCommandResponseDto(false, $"failed to create nodes: {ex.Message}", null, null);
        }
    }

    private bool TryBuildTree(out HierarchyNodeDto root, out Dictionary<int, HostHierarchyNode> index)
    {
        index = new Dictionary<int, HostHierarchyNode>();
        var assetsRoot = Path.Combine(_projectPath, "Assets");
        if (!Directory.Exists(assetsRoot))
        {
            root = new HierarchyNodeDto(RootNodeId, "Assets", true, []);
            index[RootNodeId] = new HostHierarchyNode(RootNodeId, "Assets", assetsRoot, true);
            return false;
        }

        var usedIds = new HashSet<int> { RootNodeId };
        var nodeCount = 0;
        root = BuildNode(assetsRoot, "Assets", RootNodeId, usedIds, index, depth: 0, ref nodeCount);
        return true;
    }

    private HierarchyNodeDto BuildNode(
        string absolutePath,
        string relativePath,
        int nodeId,
        HashSet<int> usedIds,
        Dictionary<int, HostHierarchyNode> index,
        int depth,
        ref int nodeCount)
    {
        var name = relativePath.Equals("Assets", StringComparison.OrdinalIgnoreCase)
            ? "Assets"
            : Path.GetFileName(relativePath);
        index[nodeId] = new HostHierarchyNode(nodeId, relativePath.Replace('\\', '/'), absolutePath, true, name);
        nodeCount++;

        if (depth >= MaxTreeDepth || nodeCount >= MaxNodeCount)
        {
            return new HierarchyNodeDto(nodeId, name, true, []);
        }

        var children = new List<HierarchyNodeDto>();
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(absolutePath);
        }
        catch
        {
            directories = [];
        }

        foreach (var childDirectory in directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (nodeCount >= MaxNodeCount)
            {
                break;
            }

            var childRelative = CombineRelativePath(relativePath, Path.GetFileName(childDirectory));
            var childId = AllocateNodeId(childRelative, usedIds);
            children.Add(BuildNode(childDirectory, childRelative, childId, usedIds, index, depth + 1, ref nodeCount));
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(absolutePath);
        }
        catch
        {
            files = [];
        }

        foreach (var childFile in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (nodeCount >= MaxNodeCount)
            {
                break;
            }

            if (childFile.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var childRelative = CombineRelativePath(relativePath, Path.GetFileName(childFile));
            var childId = AllocateNodeId(childRelative, usedIds);
            var childName = Path.GetFileName(childFile);
            index[childId] = new HostHierarchyNode(childId, childRelative, childFile, false, childName);
            children.Add(new HierarchyNodeDto(childId, childName, true, []));
            nodeCount++;
        }

        return new HierarchyNodeDto(nodeId, name, true, children);
    }

    private static int ComputeSnapshotVersion()
    {
        return (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % int.MaxValue);
    }

    private static int AllocateNodeId(string path, HashSet<int> usedIds)
    {
        var hash = StableHash(path);
        var candidate = Math.Max(hash, RootNodeId + 1);
        while (!usedIds.Add(candidate))
        {
            candidate++;
            if (candidate == int.MaxValue)
            {
                candidate = RootNodeId + 1;
            }
        }

        return candidate;
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            var hash = offset;
            foreach (var ch in value.ToLowerInvariant())
            {
                hash ^= ch;
                hash *= prime;
            }

            return (int)(hash & 0x7fffffff);
        }
    }

    private static string CombineRelativePath(string parent, string leaf)
    {
        if (string.IsNullOrWhiteSpace(parent))
        {
            return leaf.Replace('\\', '/');
        }

        return $"{parent.TrimEnd('/', '\\')}/{leaf.TrimStart('/', '\\')}".Replace('\\', '/');
    }

    private static void DeleteMetaIfPresent(string path)
    {
        var metaPath = path + ".meta";
        if (File.Exists(metaPath))
        {
            File.Delete(metaPath);
        }
    }

    private static void MoveMetaIfPresent(string sourcePath, string destinationPath)
    {
        var sourceMeta = sourcePath + ".meta";
        var destinationMeta = destinationPath + ".meta";
        if (!File.Exists(sourceMeta))
        {
            return;
        }

        if (File.Exists(destinationMeta))
        {
            File.Delete(destinationMeta);
        }

        File.Move(sourceMeta, destinationMeta);
    }

    private static string ResolveUniquePath(string candidatePath, bool isDirectory)
    {
        if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
        {
            return candidatePath;
        }

        var directory = Path.GetDirectoryName(candidatePath) ?? string.Empty;
        var stem = isDirectory
            ? Path.GetFileName(candidatePath)
            : Path.GetFileNameWithoutExtension(candidatePath);
        var extension = isDirectory ? string.Empty : Path.GetExtension(candidatePath);
        var suffix = 1;
        while (true)
        {
            var resolved = Path.Combine(directory, $"{stem}_{suffix}{extension}");
            if (!File.Exists(resolved) && !Directory.Exists(resolved))
            {
                return resolved;
            }

            suffix++;
        }
    }

    private static int? ResolveNodeIdByAbsolutePath(Dictionary<int, HostHierarchyNode> index, string absolutePath)
    {
        foreach (var item in index)
        {
            if (item.Value.AbsolutePath.Equals(absolutePath, StringComparison.Ordinal))
            {
                return item.Key;
            }
        }

        return null;
    }

    private HierarchyCommandResponseDto HandleMakeEmptyParent(
        HierarchyCommandRequestDto request,
        Dictionary<int, HostHierarchyNode> index,
        string parentDirectory,
        string baseName)
    {
        if (request.TargetId is null || !index.TryGetValue(request.TargetId.Value, out var targetNode))
        {
            return new HierarchyCommandResponseDto(false, "EmptyParent requires targetId", null, null);
        }

        if (targetNode.Id == RootNodeId)
        {
            return new HierarchyCommandResponseDto(false, "cannot wrap Assets root with EmptyParent", null, null);
        }

        var containerPath = ResolveUniquePath(Path.Combine(parentDirectory, baseName), isDirectory: true);
        containerPath = Path.GetFullPath(containerPath);
        if (!IsPathWithinAssets(containerPath))
        {
            return new HierarchyCommandResponseDto(false, "EmptyParent target escaped Assets root", null, null);
        }

        try
        {
            Directory.CreateDirectory(containerPath);
            var movedPath = Path.Combine(containerPath, Path.GetFileName(targetNode.AbsolutePath));
            if (targetNode.IsDirectory)
            {
                Directory.Move(targetNode.AbsolutePath, movedPath);
            }
            else
            {
                File.Move(targetNode.AbsolutePath, movedPath);
            }

            MoveMetaIfPresent(targetNode.AbsolutePath, movedPath);
            if (!TryBuildTree(out _, out var updatedIndex))
            {
                return new HierarchyCommandResponseDto(true, "created EmptyParent wrapper in host mode", null, null);
            }

            var nodeId = ResolveNodeIdByAbsolutePath(updatedIndex, containerPath);
            return new HierarchyCommandResponseDto(true, "created EmptyParent wrapper in host mode", nodeId, null);
        }
        catch (Exception ex)
        {
            return new HierarchyCommandResponseDto(false, $"failed to create EmptyParent wrapper: {ex.Message}", null, null);
        }
    }

    private static bool TryNormalizeNodeName(string? raw, out string safeName, out string error)
    {
        safeName = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "name must not be empty";
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed is "." or "..")
        {
            error = "name must not be '.' or '..'";
            return false;
        }

        if (trimmed.Contains('/', StringComparison.Ordinal) || trimmed.Contains('\\', StringComparison.Ordinal))
        {
            error = "name must not contain path separators";
            return false;
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "name contains invalid file-name characters";
            return false;
        }

        safeName = trimmed;
        return true;
    }

    private bool IsPathWithinAssets(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.Equals(_assetsRoot, StringComparison.Ordinal)
               || fullPath.StartsWith(_assetsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsSubPath(string candidatePath, string parentPath)
    {
        var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(parent, StringComparison.Ordinal)
               || candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static HostMakeSpec ResolveMakeSpec(string type)
    {
        if (type.Equals("Empty", StringComparison.OrdinalIgnoreCase)
            || type.Equals("EmptyChild", StringComparison.OrdinalIgnoreCase))
        {
            return new HostMakeSpec(true, string.Empty, "directory placeholder");
        }

        if (type.Equals("Text", StringComparison.OrdinalIgnoreCase)
            || type.Equals("TMP", StringComparison.OrdinalIgnoreCase))
        {
            return new HostMakeSpec(false, ".txt", "ui text placeholder");
        }

        if (type.Equals("Sprite", StringComparison.OrdinalIgnoreCase))
        {
            return new HostMakeSpec(false, ".sprite", "sprite placeholder");
        }

        return new HostMakeSpec(false, ".prefab", "prefab placeholder");
    }

    private sealed record HostHierarchyNode(int Id, string Path, string AbsolutePath, bool IsDirectory, string? Name = null)
    {
        public string Name { get; } = string.IsNullOrWhiteSpace(Name)
            ? System.IO.Path.GetFileName(Path)
            : Name!;
    }

    private sealed record HostMakeSpec(bool IsDirectory, string Extension, string TemplateHint);
}
