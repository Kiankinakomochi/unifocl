using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Executes a /mutate batch: a JSON array of MutateOp records sent as a single exec call.
/// Context (hierarchy vs inspector) is inferred per-op — no session mode switching required.
/// </summary>
internal sealed class MutateBatchService
{
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int SceneRootNodeId = int.MaxValue;

    private readonly HierarchyDaemonClient _hierarchyClient = new();

    // ── Command entry point ──────────────────────────────────────────────────

    public async Task HandleCommandAsync(string rawInput, CliSessionState session, Action<string> log)
    {
        if (!TryExtractPayload(rawInput, out var jsonText, out var dryRun, out var continueOnError))
        {
            log("[red]mutate[/]: no JSON payload found — expected /mutate (--dry-run) (--continue-on-error) <json-array>");
            log("[grey]mutate[/]: example: /mutate [[{\"op\":\"create\",\"parent\":\"/\",\"type\":\"canvas\",\"name\":\"MyCanvas\"}]]");
            return;
        }

        List<MutateOp>? ops;
        try
        {
            ops = JsonSerializer.Deserialize<List<MutateOp>>(jsonText, JsonOptions);
        }
        catch (JsonException ex)
        {
            log($"[red]mutate[/]: JSON parse error — {ex.Message}");
            return;
        }

        if (ops is null || ops.Count == 0)
        {
            log("[yellow]mutate[/]: empty op list");
            return;
        }

        var port = DaemonControlService.GetPort(session);
        if (port is null)
        {
            log("[red]mutate[/]: no attached daemon — open a project first with /open <path>");
            return;
        }

        using var dryRunScope = CliDryRunScope.Push(dryRun);
        var result = await ExecuteBatchAsync(port.Value, ops, continueOnError, dryRun);
        EmitResults(result, log);
    }

    // ── Batch execution ──────────────────────────────────────────────────────

    private async Task<MutateBatchResult> ExecuteBatchAsync(
        int port,
        List<MutateOp> ops,
        bool continueOnError,
        bool dryRun)
    {
        var results = new List<MutateOpResult>(ops.Count);
        HierarchySnapshotDto? snapshot = null;

        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            var needsSnapshot = op.Op is "create" or "rename" or "remove" or "move" or "toggle_active";

            if (needsSnapshot && snapshot is null)
            {
                snapshot = await _hierarchyClient.GetSnapshotAsync(port);
            }

            var opResult = await ExecuteOpAsync(port, op, i, snapshot);
            results.Add(opResult);

            // Invalidate snapshot after structural changes so next path-lookup is fresh.
            if (opResult.Ok && op.Op is "create" or "rename" or "remove" or "move")
            {
                snapshot = null;
            }

            if (!opResult.Ok && !continueOnError)
            {
                break;
            }
        }

        return new MutateBatchResult(
            AllOk: results.All(r => r.Ok),
            Total: results.Count,
            Succeeded: results.Count(r => r.Ok),
            Failed: results.Count(r => !r.Ok),
            Results: results,
            DryRun: dryRun);
    }

    // ── Op dispatch ──────────────────────────────────────────────────────────

    private async Task<MutateOpResult> ExecuteOpAsync(
        int port,
        MutateOp op,
        int index,
        HierarchySnapshotDto? snapshot)
    {
        return op.Op.ToLowerInvariant() switch
        {
            "create"           => await ExecuteCreateAsync(port, op, index, snapshot),
            "rename"           => await ExecuteRenameAsync(port, op, index, snapshot),
            "remove"           => await ExecuteRemoveAsync(port, op, index, snapshot),
            "move"             => await ExecuteMoveAsync(port, op, index, snapshot),
            "toggle_active"    => await ExecuteToggleActiveAsync(port, op, index, snapshot),
            "add_component"    => await ExecuteInspectorAsync(port, op, index, "add-component"),
            "remove_component" => await ExecuteInspectorAsync(port, op, index, "remove-component"),
            "set_field"        => await ExecuteInspectorAsync(port, op, index, "set-field"),
            "toggle_field"     => await ExecuteInspectorAsync(port, op, index, "toggle-field"),
            "toggle_component" => await ExecuteInspectorAsync(port, op, index, "toggle-component"),
            _ => new MutateOpResult(index, op.Op, op.Target, false,
                     $"unknown op '{op.Op}'. Valid: create, rename, remove, move, toggle_active, " +
                     "add_component, remove_component, set_field, toggle_field, toggle_component")
        };
    }

    // ── Hierarchy ops ────────────────────────────────────────────────────────

    private async Task<MutateOpResult> ExecuteCreateAsync(
        int port, MutateOp op, int index, HierarchySnapshotDto? snapshot)
    {
        if (string.IsNullOrWhiteSpace(op.Type))
        {
            return Fail(index, op, "'type' is required for create");
        }

        var parentId = ResolveParentId(op.Parent, snapshot, out var parentError);
        if (parentError is not null)
        {
            return Fail(index, op, parentError);
        }

        var response = await _hierarchyClient.ExecuteAsync(port,
            new HierarchyCommandRequestDto("mk", parentId, null, op.Name, false, op.Type, op.Count ?? 1));

        return response.Ok
            ? new MutateOpResult(index, op.Op, op.Parent ?? "/", true, null, response.NodeId, response.AssignedName)
            : Fail(index, op, response.Message);
    }

    private async Task<MutateOpResult> ExecuteRenameAsync(
        int port, MutateOp op, int index, HierarchySnapshotDto? snapshot)
    {
        if (string.IsNullOrWhiteSpace(op.Target))
        {
            return Fail(index, op, "'target' is required for rename");
        }

        if (string.IsNullOrWhiteSpace(op.Name))
        {
            return Fail(index, op, "'name' (new name) is required for rename");
        }

        var node = ResolveNode(op.Target, snapshot);
        if (node is null)
        {
            return Fail(index, op, $"target not found in hierarchy: {op.Target}");
        }

        var response = await _hierarchyClient.ExecuteAsync(port,
            new HierarchyCommandRequestDto("rename", null, node.Id, op.Name, false));

        return response.Ok
            ? new MutateOpResult(index, op.Op, op.Target ?? op.Parent, true, AssignedName: response.AssignedName)
            : Fail(index, op, response.Message);
    }

    private async Task<MutateOpResult> ExecuteRemoveAsync(
        int port, MutateOp op, int index, HierarchySnapshotDto? snapshot)
    {
        if (string.IsNullOrWhiteSpace(op.Target))
        {
            return Fail(index, op, "'target' is required for remove");
        }

        var node = ResolveNode(op.Target, snapshot);
        if (node is null)
        {
            return Fail(index, op, $"target not found in hierarchy: {op.Target}");
        }

        var response = await _hierarchyClient.ExecuteAsync(port,
            new HierarchyCommandRequestDto("rm", null, node.Id, null, false));

        return response.Ok ? Ok(index, op) : Fail(index, op, response.Message);
    }

    private async Task<MutateOpResult> ExecuteMoveAsync(
        int port, MutateOp op, int index, HierarchySnapshotDto? snapshot)
    {
        if (string.IsNullOrWhiteSpace(op.Target))
        {
            return Fail(index, op, "'target' is required for move");
        }

        var targetNode = ResolveNode(op.Target, snapshot);
        if (targetNode is null)
        {
            return Fail(index, op, $"target not found: {op.Target}");
        }

        var parentId = ResolveParentId(op.Parent, snapshot, out var parentError);
        if (parentError is not null)
        {
            return Fail(index, op, parentError);
        }

        var response = await _hierarchyClient.ExecuteAsync(port,
            new HierarchyCommandRequestDto("mv", parentId, targetNode.Id, null, false));

        return response.Ok
            ? new MutateOpResult(index, op.Op, op.Target ?? op.Parent, true, AssignedName: response.AssignedName)
            : Fail(index, op, response.Message);
    }

    private async Task<MutateOpResult> ExecuteToggleActiveAsync(
        int port, MutateOp op, int index, HierarchySnapshotDto? snapshot)
    {
        if (string.IsNullOrWhiteSpace(op.Target))
        {
            return Fail(index, op, "'target' is required for toggle_active");
        }

        var node = ResolveNode(op.Target, snapshot);
        if (node is null)
        {
            return Fail(index, op, $"target not found: {op.Target}");
        }

        var response = await _hierarchyClient.ExecuteAsync(port,
            new HierarchyCommandRequestDto("toggle", null, node.Id,
                op.Active.HasValue ? op.Active.Value.ToString().ToLowerInvariant() : null,
                false));

        return response.Ok ? Ok(index, op) : Fail(index, op, response.Message);
    }

    // ── Inspector ops ────────────────────────────────────────────────────────

    private async Task<MutateOpResult> ExecuteInspectorAsync(
        int port, MutateOp op, int index, string bridgeAction)
    {
        if (string.IsNullOrWhiteSpace(op.Target))
        {
            return Fail(index, op, $"'target' is required for {op.Op}");
        }

        string? componentName = null;
        int? componentIndex = null;

        if (!string.IsNullOrWhiteSpace(op.Component))
        {
            if (int.TryParse(op.Component, out var idx))
            {
                componentIndex = idx;
            }
            else
            {
                componentName = op.Component;
            }
        }

        // For add_component, 'type' is the component type; map it to ComponentName for the bridge.
        if (bridgeAction == "add-component" && !string.IsNullOrWhiteSpace(op.Type))
        {
            componentName = op.Type;
        }

        var request = new InspectorBatchBridgeRequest(
            bridgeAction,
            op.Target,
            componentIndex,
            componentName,
            op.Field,
            op.Value,
            null,
            op.Enabled.HasValue ? op.Enabled.Value.ToString().ToLowerInvariant() : null);

        var responseJson = await SendInspectorRequestAsync(port, request);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return Fail(index, op,
                $"{op.Op} failed — daemon did not respond. " +
                "Inspector mutations require Bridge mode (Unity Editor open with Bridge package installed). " +
                "Hierarchy mutations (create/rename/remove/move) work in Host mode.");
        }

        try
        {
            var response = JsonSerializer.Deserialize<InspectorBatchMutationResponse>(responseJson, JsonOptions);
            if (response?.Ok != true)
            {
                return Fail(index, op, response?.Message ?? "inspector mutation failed");
            }

            var addedComponentIndex = bridgeAction == "add-component" ? response.AssignedIndex : null;
            return new MutateOpResult(index, op.Op, op.Target ?? op.Parent, true, ComponentIndex: addedComponentIndex);
        }
        catch
        {
            return Fail(index, op, "failed to parse inspector response");
        }
    }

    private async Task<string?> SendInspectorRequestAsync(int port, InspectorBatchBridgeRequest request)
    {
        if (DaemonMutationActionCatalog.IsInspectorMutation(request.Action) && request.Intent is null)
        {
            request = request with
            {
                Intent = MutationIntentFactory.CreateInspectorIntent(
                    request.Action,
                    request.TargetPath,
                    request.ComponentIndex,
                    request.ComponentName,
                    request.FieldName,
                    request.Value)
            };
        }

        // JsonUtility.FromJson (Unity side) deserializes JSON null as 0 for int fields,
        // but the Unity-side sentinel for "no component index specified" is -1.
        // Normalize null → -1 here so Unity's ResolveComponent falls through to name lookup.
        if (request.ComponentIndex is null)
        {
            request = request with { ComponentIndex = -1 };
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                var json = JsonSerializer.Serialize(request, JsonOptions);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await Http.PostAsync($"http://127.0.0.1:{port}/inspect", content, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                return (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
            }
            catch when (attempt < 2)
            {
                await Task.Delay(180);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    // ── Path resolution ──────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a scene-hierarchy path (e.g. "/Canvas/Panel") to its node.
    /// Returns null if the snapshot is unavailable or the path is not found.
    /// </summary>
    private static HierarchyNodeDto? ResolveNode(string path, HierarchySnapshotDto? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        var normalizedPath = path.Trim().TrimStart('/');
        if (string.IsNullOrEmpty(normalizedPath))
        {
            // "/" means scene root
            return snapshot.Root;
        }

        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return WalkNode(snapshot.Root.Children, segments, 0);
    }

    private static HierarchyNodeDto? WalkNode(
        List<HierarchyNodeDto> children, string[] segments, int depth)
    {
        if (depth >= segments.Length)
        {
            return null;
        }

        var seg = segments[depth];
        foreach (var child in children)
        {
            if (!child.Name.Equals(seg, StringComparison.Ordinal))
            {
                continue;
            }

            if (depth == segments.Length - 1)
            {
                return child;
            }

            return WalkNode(child.Children, segments, depth + 1);
        }

        return null;
    }

    private static int ResolveParentId(
        string? parentPath, HierarchySnapshotDto? snapshot, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(parentPath) || parentPath == "/")
        {
            return SceneRootNodeId;
        }

        var node = ResolveNode(parentPath, snapshot);
        if (node is null)
        {
            error = $"parent not found in hierarchy: {parentPath}";
            return 0;
        }

        return node.Id;
    }

    // ── Result helpers ───────────────────────────────────────────────────────

    private static MutateOpResult Ok(int index, MutateOp op) =>
        new(index, op.Op, op.Target ?? op.Parent, true);

    private static MutateOpResult Fail(int index, MutateOp op, string? message) =>
        new(index, op.Op, op.Target ?? op.Parent, false, message);

    // ── Output ───────────────────────────────────────────────────────────────

    private static void EmitResults(MutateBatchResult result, Action<string> log)
    {
        var label = result.DryRun ? "[grey]mutate[/] (dry-run)" : "[grey]mutate[/]";
        log($"{label}: {result.Succeeded}/{result.Total} ops succeeded");

        foreach (var r in result.Results)
        {
            if (r.Ok)
            {
                var extra = r.CreatedId.HasValue ? $" id={r.CreatedId.Value}" : string.Empty;
                if (r.AssignedName is not null)
                {
                    extra += $" name=\"{r.AssignedName}\"";
                }
                if (r.ComponentIndex.HasValue)
                {
                    extra += $" index={r.ComponentIndex.Value}";
                }
                log($"  [green]+[/]  {r.Op} {r.Target}{extra}");
            }
            else
            {
                log($"  [red]x[/]  {r.Op} {r.Target} — {r.Message}");
            }
        }
    }

    // ── Input parsing ────────────────────────────────────────────────────────

    /// <summary>
    /// Parses raw /mutate command text into flags + JSON payload.
    /// Works on raw text without tokenization to preserve JSON structure.
    /// </summary>
    internal static bool TryExtractPayload(
        string rawInput,
        out string jsonText,
        out bool dryRun,
        out bool continueOnError)
    {
        dryRun = false;
        continueOnError = false;
        jsonText = string.Empty;

        var rest = rawInput.Trim().TrimStart('/');
        if (rest.StartsWith("mutate", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest["mutate".Length..].TrimStart();
        }

        // Parse flags before the JSON starts
        while (rest.Length > 0 && rest.StartsWith("--", StringComparison.Ordinal))
        {
            if (rest.StartsWith("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
                rest = rest["--dry-run".Length..].TrimStart();
            }
            else if (rest.StartsWith("--continue-on-error", StringComparison.OrdinalIgnoreCase))
            {
                continueOnError = true;
                rest = rest["--continue-on-error".Length..].TrimStart();
            }
            else
            {
                break;
            }
        }

        var arrayStart = rest.IndexOf('[');
        var objectStart = rest.IndexOf('{');

        int jsonStart;
        if (arrayStart >= 0 && (objectStart < 0 || arrayStart <= objectStart))
        {
            jsonStart = arrayStart;
        }
        else if (objectStart >= 0)
        {
            jsonStart = objectStart;
        }
        else
        {
            return false;
        }

        var extracted = rest[jsonStart..];
        // Wrap bare object in array for uniform parsing
        if (extracted.TrimStart().StartsWith('{'))
        {
            extracted = "[" + extracted + "]";
        }

        jsonText = extracted;
        return !string.IsNullOrWhiteSpace(jsonText);
    }

    // ── Private DTOs (bridge request/response for batch, mirrors InspectorModeService internals) ──

    private sealed record InspectorBatchBridgeRequest(
        string Action,
        string TargetPath,
        int? ComponentIndex,
        string? ComponentName,
        string? FieldName,
        string? Value,
        string? Query,
        string? EnabledValue = null,
        MutationIntentDto? Intent = null);

    private sealed record InspectorBatchMutationResponse(
        bool Ok,
        string? Message,
        string? Content,
        [property: JsonPropertyName("assignedIndex")] int? AssignedIndex = null);
}
