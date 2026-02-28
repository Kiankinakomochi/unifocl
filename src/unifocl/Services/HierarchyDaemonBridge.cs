using System.Text.Json;

internal sealed class HierarchyDaemonBridge
{
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private HierarchyNodeState _root;
    private int _snapshotVersion;
    private int _nextId;

    public HierarchyDaemonBridge(string? projectPath)
    {
        _root = BuildSeedHierarchy(projectPath);
        _snapshotVersion = 1;
        _nextId = ComputeMaxId(_root) + 1;
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
            response = BuildSnapshotJson();
            return true;
        }

        if (command.StartsWith("HIERARCHY_FIND ", StringComparison.Ordinal))
        {
            var searchPayload = command["HIERARCHY_FIND ".Length..];
            response = ExecuteFindJson(searchPayload);
            return true;
        }

        if (!command.StartsWith("HIERARCHY_CMD ", StringComparison.Ordinal))
        {
            return false;
        }

        var payload = command["HIERARCHY_CMD ".Length..];
        response = ExecuteCommandJson(payload);
        return true;
    }

    private string BuildSnapshotJson()
    {
        lock (_sync)
        {
            var snapshot = new HierarchySnapshotDto("Arena", _snapshotVersion, ToDto(_root));
            return JsonSerializer.Serialize(snapshot, _jsonOptions);
        }
    }

    private string ExecuteCommandJson(string payload)
    {
        HierarchyCommandRequestDto? request;
        try
        {
            request = JsonSerializer.Deserialize<HierarchyCommandRequestDto>(payload, _jsonOptions);
        }
        catch
        {
            return JsonSerializer.Serialize(new HierarchyCommandResponseDto(false, "invalid hierarchy command payload", null, null), _jsonOptions);
        }

        if (request is null)
        {
            return JsonSerializer.Serialize(new HierarchyCommandResponseDto(false, "missing hierarchy command payload", null, null), _jsonOptions);
        }

        lock (_sync)
        {
            var action = request.Action?.Trim() ?? string.Empty;
            if (action.Equals("mk", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteCreate(request);
            }

            if (action.Equals("toggle", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteToggle(request);
            }

            return JsonSerializer.Serialize(new HierarchyCommandResponseDto(false, $"unsupported action: {action}", null, null), _jsonOptions);
        }
    }

    private string ExecuteFindJson(string payload)
    {
        HierarchySearchRequestDto? request;
        try
        {
            request = JsonSerializer.Deserialize<HierarchySearchRequestDto>(payload, _jsonOptions);
        }
        catch
        {
            return JsonSerializer.Serialize(new HierarchySearchResponseDto(false, [], "invalid hierarchy search payload"), _jsonOptions);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Query))
        {
            return JsonSerializer.Serialize(new HierarchySearchResponseDto(false, [], "search query is required"), _jsonOptions);
        }

        var results = new List<HierarchySearchResultDto>();
        var limit = Math.Clamp(request.Limit, 1, 50);
        lock (_sync)
        {
            var origin = request.ParentId is int parentId
                ? FindNode(_root, parentId) ?? _root
                : _root;
            var originPath = BuildPath(_root, origin.Id);
            CollectMatches(origin, originPath, request.Query, results);
        }

        var top = results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
        return JsonSerializer.Serialize(new HierarchySearchResponseDto(true, top, null), _jsonOptions);
    }

    private string ExecuteCreate(HierarchyCommandRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return JsonSerializer.Serialize(new HierarchyCommandResponseDto(false, "mk requires a name", null, null), _jsonOptions);
        }

        var parentId = request.ParentId ?? _root.Id;
        var parent = FindNode(_root, parentId);
        if (parent is null)
        {
            return JsonSerializer.Serialize(new HierarchyCommandResponseDto(false, $"parent id not found: {parentId}", null, null), _jsonOptions);
        }

        var created = new HierarchyNodeState(_nextId++, request.Name.Trim(), true, new List<HierarchyNodeState>());
        parent.Children.Add(created);
        _snapshotVersion++;

        return JsonSerializer.Serialize(new HierarchyCommandResponseDto(true, "created", created.Id, created.Active), _jsonOptions);
    }

    private string ExecuteToggle(HierarchyCommandRequestDto request)
    {
        if (request.TargetId is null)
        {
            return JsonSerializer.Serialize(new HierarchyCommandResponseDto(false, "toggle requires target id", null, null), _jsonOptions);
        }

        var node = FindNode(_root, request.TargetId.Value);
        if (node is null)
        {
            return JsonSerializer.Serialize(new HierarchyCommandResponseDto(false, $"target id not found: {request.TargetId.Value}", null, null), _jsonOptions);
        }

        node.Active = !node.Active;
        _snapshotVersion++;
        return JsonSerializer.Serialize(new HierarchyCommandResponseDto(true, "toggled", node.Id, node.Active), _jsonOptions);
    }

    private static HierarchyNodeDto ToDto(HierarchyNodeState node)
    {
        return new HierarchyNodeDto(
            node.Id,
            node.Name,
            node.Active,
            node.Children.Select(ToDto).ToList());
    }

    private static HierarchyNodeState? FindNode(HierarchyNodeState node, int id)
    {
        if (node.Id == id)
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var found = FindNode(child, id);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static void CollectMatches(HierarchyNodeState node, string path, string query, List<HierarchySearchResultDto> results)
    {
        if (FuzzyMatcher.TryScore(query, path, out var pathScore))
        {
            results.Add(new HierarchySearchResultDto(node.Id, path, node.Active, pathScore));
        }
        else if (FuzzyMatcher.TryScore(query, node.Name, out var nameScore))
        {
            results.Add(new HierarchySearchResultDto(node.Id, path, node.Active, nameScore));
        }

        foreach (var child in node.Children)
        {
            var childPath = path.EndsWith("/", StringComparison.Ordinal) ? path + child.Name : path + "/" + child.Name;
            CollectMatches(child, childPath, query, results);
        }
    }

    private static int ComputeMaxId(HierarchyNodeState node)
    {
        var max = node.Id;
        foreach (var child in node.Children)
        {
            var childMax = ComputeMaxId(child);
            if (childMax > max)
            {
                max = childMax;
            }
        }

        return max;
    }

    private static HierarchyNodeState BuildSeedHierarchy(string? projectPath)
    {
        _ = projectPath;
        var rootName = "Player";

        return new HierarchyNodeState(
            100,
            rootName,
            true,
            new List<HierarchyNodeState>
            {
                new(101, "WeaponMount", true, new List<HierarchyNodeState>
                {
                    new(102, "LeftBlaster", true, new List<HierarchyNodeState>()),
                    new(103, "RightBlaster", false, new List<HierarchyNodeState>())
                }),
                new(104, "Mesh", true, new List<HierarchyNodeState>
                {
                    new(105, "PlayerModel", true, new List<HierarchyNodeState>())
                }),
                new(106, "Audio", false, new List<HierarchyNodeState>()),
                new(107, "CapsuleCollider", true, new List<HierarchyNodeState>()),
                new(108, "Rigidbody", true, new List<HierarchyNodeState>())
            });
    }

    private static string BuildPath(HierarchyNodeState root, int targetId)
    {
        var segments = new List<string>();
        if (!TryCollectPath(root, targetId, segments))
        {
            return "/" + root.Name;
        }

        segments.Reverse();
        return "/" + string.Join('/', segments);
    }

    private static bool TryCollectPath(HierarchyNodeState node, int targetId, List<string> segments)
    {
        if (node.Id == targetId)
        {
            segments.Add(node.Name);
            return true;
        }

        foreach (var child in node.Children)
        {
            if (TryCollectPath(child, targetId, segments))
            {
                segments.Add(node.Name);
                return true;
            }
        }

        return false;
    }

    private sealed class HierarchyNodeState
    {
        public HierarchyNodeState(int id, string name, bool active, List<HierarchyNodeState> children)
        {
            Id = id;
            Name = name;
            Active = active;
            Children = children;
        }

        public int Id { get; }
        public string Name { get; }
        public bool Active { get; set; }
        public List<HierarchyNodeState> Children { get; }
    }
}
