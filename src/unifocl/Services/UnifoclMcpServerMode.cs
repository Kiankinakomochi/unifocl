using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;

internal static class UnifoclMcpServerMode
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    internal static readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Forwards a custom tool call to the running Unity daemon via HTTP.
    /// Resolves the daemon port from the runtime registry.
    /// </summary>
    internal static async Task<string> ForwardToUnityAsync(string toolName, string argsJson, bool dryRun, CancellationToken ct)
    {
        // Try host-mode registry first ($CWD/.unifocl-runtime/daemons/)
        var runtimeRoot = Path.Combine(Environment.CurrentDirectory, ".unifocl-runtime");
        var runtime = new DaemonRuntime(runtimeRoot);
        var daemon = runtime.GetAll().FirstOrDefault();

        // Fall back to global registry (~/.unifocl-runtime) when MCP is launched outside the project CWD
        if (daemon is null)
        {
            var globalRuntimeRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".unifocl-runtime");
            var globalRuntime = new DaemonRuntime(globalRuntimeRoot);
            daemon = globalRuntime.GetAll().FirstOrDefault();
        }

        // Fall back to bridge-mode session file (<projectPath>/.unifocl/daemon.session.json)
        if (daemon is null)
        {
            var projectPath = UnifoclManifestService.ResolveActiveProjectPath();
            if (!string.IsNullOrEmpty(projectPath))
            {
                var sessionFile = Path.Combine(projectPath, ".unifocl", "daemon.session.json");
                if (File.Exists(sessionFile))
                {
                    try
                    {
                        var sessionJson = await File.ReadAllTextAsync(sessionFile, ct);
                        var session = JsonSerializer.Deserialize<DaemonSessionInfo>(sessionJson);
                        if (session is not null)
                            daemon = new DaemonInstance(session.Port, 0, DateTime.UtcNow, null, false, projectPath, DateTime.UtcNow);
                    }
                    catch { /* ignore malformed session file */ }
                }
            }
        }

        if (daemon is null)
        {
            return $"{{\"ok\":false,\"message\":\"no active unifocl daemon found\"}}";
        }

        var payload = new
        {
            operation = "execute_custom_tool",
            tool = toolName,
            args = argsJson,
            dryRun
        };

        var json = JsonSerializer.Serialize(payload, _jsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var url = $"http://127.0.0.1:{daemon.Port}/mcp/unifocl_project_command";

        try
        {
            var response = await _http.PostAsync(url, content, ct);
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            return $"{{\"ok\":false,\"message\":\"{ex.Message.Replace("\"", "'")}\"}}";
        }
    }

    public static bool IsRequested(string[] args)
    {
        return args.Any(static arg =>
            arg.Equals("mcp-server", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("--mcp-server", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var sanitizedArgs = args
            .Where(static arg =>
                !arg.Equals("mcp-server", StringComparison.OrdinalIgnoreCase)
                && !arg.Equals("--mcp-server", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var builder = Host.CreateApplicationBuilder(sanitizedArgs);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Services.AddSingleton<UnifoclManifestService>();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        var host = builder.Build();

        var projectPath = UnifoclManifestService.ResolveActiveProjectPath();
        if (!string.IsNullOrEmpty(projectPath))
        {
            host.Services.GetRequiredService<UnifoclManifestService>().EnsureLoaded(projectPath);
        }

        await host.RunAsync(cancellationToken);
    }
}

[McpServerToolType]
public static class UnifoclCommandLookupTools
{
    [McpServerTool, Description("Lists unifocl commands by scope so agents can avoid reading full README/help text.")]
    public static CommandLookupResult ListCommands(
        [Description("Command scope filter: root, project, inspector, or all.")] string scope = "all",
        [Description("Optional case-insensitive search across trigger/signature/description.")] string? query = null,
        [Description("Max commands to return (1-400).")] int limit = 120)
    {
        var normalizedScope = NormalizeScope(scope);
        var normalizedQuery = query?.Trim();
        var commandRows = EnumerateCommands(normalizedScope);

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            commandRows = commandRows.Where(row =>
                row.Spec.Trigger.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                || row.Spec.Signature.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                || row.Spec.Description.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase));
        }

        var allMatches = commandRows
            .OrderBy(row => row.Scope, StringComparer.Ordinal)
            .ThenBy(row => row.Spec.Trigger, StringComparer.Ordinal)
            .Select(row => new CommandLookupItem(
                row.Scope,
                row.Spec.Trigger,
                row.Spec.Signature,
                row.Spec.Description))
            .ToList();

        var capped = Math.Clamp(limit, 1, 400);
        var returned = allMatches.Take(capped).ToList();
        return new CommandLookupResult(
            Scope: normalizedScope,
            Query: normalizedQuery ?? string.Empty,
            Total: allMatches.Count,
            Returned: returned.Count,
            Commands: returned);
    }

    [McpServerTool, Description("Looks up the best command match (trigger/signature/alias) and returns concise usage details.")]
    public static CommandLookupResult LookupCommand(
        [Description("Command trigger, alias, or search fragment (e.g. /open, mk, build run).")] string command,
        [Description("Command scope filter: root, project, inspector, or all.")] string scope = "all")
    {
        var normalizedScope = NormalizeScope(scope);
        var query = (command ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return new CommandLookupResult(normalizedScope, string.Empty, 0, 0, []);
        }

        var commandRows = EnumerateCommands(normalizedScope).ToList();
        var exact = commandRows.Where(row =>
                row.Spec.Trigger.Equals(query, StringComparison.OrdinalIgnoreCase)
                || row.Spec.Signature.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var startsWith = commandRows.Where(row =>
                row.Spec.Trigger.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                || row.Spec.Signature.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var contains = commandRows.Where(row =>
                row.Spec.Trigger.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Spec.Signature.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Spec.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var ordered = exact
            .Concat(startsWith)
            .Concat(contains)
            .DistinctBy(row => $"{row.Scope}|{row.Spec.Trigger}|{row.Spec.Signature}")
            .Take(20)
            .Select(row => new CommandLookupItem(
                row.Scope,
                row.Spec.Trigger,
                row.Spec.Signature,
                row.Spec.Description))
            .ToList();

        return new CommandLookupResult(
            Scope: normalizedScope,
            Query: query,
            Total: ordered.Count,
            Returned: ordered.Count,
            Commands: ordered);
    }

    private static string NormalizeScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return "all";
        }

        var normalized = scope.Trim().ToLowerInvariant();
        return normalized is "root" or "project" or "inspector" ? normalized : "all";
    }

    private static IEnumerable<(string Scope, CommandSpec Spec)> EnumerateCommands(string scope)
    {
        if (scope is "all" or "root")
        {
            foreach (var command in CliCommandCatalog.CreateRootCommands())
            {
                yield return ("root", command);
            }
        }

        if (scope is "all" or "project")
        {
            foreach (var command in CliCommandCatalog.CreateProjectCommands())
            {
                yield return ("project", command);
            }
        }

        if (scope is "all" or "inspector")
        {
            foreach (var command in CliCommandCatalog.CreateInspectorCommands())
            {
                yield return ("inspector", command);
            }
        }
    }
}

public sealed record CommandLookupItem(
    string Scope,
    string Trigger,
    string Signature,
    string Description);

public sealed record CommandLookupResult(
    string Scope,
    string Query,
    int Total,
    int Returned,
    List<CommandLookupItem> Commands);

// ── /mutate schema tool ───────────────────────────────────────────────────────

[McpServerToolType]
public static class UnifoclMutateTools
{
    private const string MutateSchemaJson = """
        {
          "command": "/mutate [--dry-run] [--continue-on-error] <json-array>",
          "description": "Batch scene mutations. Context (hierarchy vs inspector) is inferred per op — no mode switching needed. Safe to call from any --mode. Requires an open project (/open) and a running daemon.",
          "flags": {
            "--dry-run": "Preview changes without applying. Diff is returned in the response.",
            "--continue-on-error": "Do not stop on first failure; run all ops and collect errors."
          },
          "ops": {
            "create": {
              "fields": { "type": "required", "parent": "optional (default '/')", "name": "optional", "count": "optional (default 1)" },
              "types": ["canvas","empty","image","text","button","scrollview","panel","inputfield","rawimage","toggle","slider","dropdown","scrollbar","eventSystem"],
              "example": {"op":"create","parent":"/","type":"canvas","name":"HUD_Canvas"}
            },
            "rename": {
              "fields": { "target": "required (hierarchy path)", "name": "required (new name)" },
              "example": {"op":"rename","target":"/Canvas","name":"HUD_Canvas"}
            },
            "remove": {
              "fields": { "target": "required" },
              "example": {"op":"remove","target":"/TempObject"}
            },
            "move": {
              "fields": { "target": "required", "parent": "required (new parent path, '/' for root)" },
              "example": {"op":"move","target":"/Panel","parent":"/HUD_Canvas"}
            },
            "toggle_active": {
              "fields": { "target": "required", "active": "optional bool — omit to flip current state" },
              "example": {"op":"toggle_active","target":"/HUD_Canvas","active":false}
            },
            "add_component": {
              "fields": { "target": "required", "type": "required (component type name)" },
              "note": "Requires Bridge mode (Unity Editor open with com.unifocl.cli package). Not available in Host/batch mode.",
              "example": {"op":"add_component","target":"/HUD_Canvas","type":"CanvasScaler"}
            },
            "remove_component": {
              "fields": { "target": "required", "component": "required (name or 0-based index)" },
              "example": {"op":"remove_component","target":"/HUD_Canvas","component":"GraphicRaycaster"}
            },
            "set_field": {
              "fields": { "target": "required", "component": "required", "field": "required", "value": "required (serialized string)" },
              "example": {"op":"set_field","target":"/HUD_Canvas","component":"Canvas","field":"renderMode","value":"ScreenSpaceOverlay"}
            },
            "toggle_field": {
              "fields": { "target": "required", "component": "required", "field": "required (must be bool)" },
              "example": {"op":"toggle_field","target":"/HUD_Canvas","component":"CanvasScaler","field":"enabled"}
            },
            "toggle_component": {
              "fields": { "target": "required", "component": "required", "enabled": "optional bool — omit to flip" },
              "example": {"op":"toggle_component","target":"/HUD_Canvas","component":"CanvasScaler","enabled":true}
            }
          },
          "response": {
            "data.allOk": "bool — true if all ops succeeded",
            "data.total": "int",
            "data.succeeded": "int",
            "data.failed": "int",
            "data.results": "array of {index, op, target, ok, message?, createdId?}",
            "data.dryRun": "bool"
          },
          "path_format": "Use '/' for scene root. '/Name' for a top-level object. '/Parent/Child' for nested. Names are case-sensitive and matched by exact name in the loaded scene.",
          "mode_compatibility": "create/rename/remove/move/toggle_active work in Host mode (batch daemon). add_component/remove_component/set_field/toggle_field/toggle_component require Bridge mode.",
          "session_tip": "Chain with --session-seed to keep project context across exec calls without repeating --project."
        }
        """;

    [McpServerTool, Description(
        "Returns the complete /mutate command schema: supported ops, required/optional fields, " +
        "type catalog, path format, response shape, and mode compatibility notes. " +
        "Call this once before issuing any /mutate batch to understand the full op set without " +
        "running /help or reading docs.")]
    public static string GetMutateSchema() => MutateSchemaJson;

    [McpServerTool, Description(
        "Validates a /mutate JSON payload and returns per-op diagnostics without executing. " +
        "Use to pre-check a batch before sending it to exec.")]
    public static MutateValidationResult ValidateMutateBatch(
        [Description("JSON array of MutateOp objects as you would pass to /mutate.")] string opsJson)
    {
        List<MutateOp>? ops;
        try
        {
            ops = System.Text.Json.JsonSerializer.Deserialize<List<MutateOp>>(
                opsJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch (System.Text.Json.JsonException ex)
        {
            return new MutateValidationResult(false, [$"JSON parse error: {ex.Message}"], []);
        }

        if (ops is null || ops.Count == 0)
        {
            return new MutateValidationResult(false, ["op array is empty"], []);
        }

        var errors = new List<string>();
        var items = new List<MutateValidationItem>();
        var validOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "create", "rename", "remove", "move", "toggle_active",
            "add_component", "remove_component", "set_field", "toggle_field", "toggle_component"
        };

        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            var opErrors = new List<string>();

            if (string.IsNullOrWhiteSpace(op.Op))
            {
                opErrors.Add("'op' is required");
            }
            else if (!validOps.Contains(op.Op))
            {
                opErrors.Add($"unknown op '{op.Op}'");
            }
            else
            {
                switch (op.Op.ToLowerInvariant())
                {
                    case "create":
                        if (string.IsNullOrWhiteSpace(op.Type)) opErrors.Add("'type' required");
                        break;
                    case "rename":
                        if (string.IsNullOrWhiteSpace(op.Target)) opErrors.Add("'target' required");
                        if (string.IsNullOrWhiteSpace(op.Name)) opErrors.Add("'name' required");
                        break;
                    case "remove":
                    case "toggle_active":
                        if (string.IsNullOrWhiteSpace(op.Target)) opErrors.Add("'target' required");
                        break;
                    case "move":
                        if (string.IsNullOrWhiteSpace(op.Target)) opErrors.Add("'target' required");
                        if (string.IsNullOrWhiteSpace(op.Parent)) opErrors.Add("'parent' required");
                        break;
                    case "add_component":
                        if (string.IsNullOrWhiteSpace(op.Target)) opErrors.Add("'target' required");
                        if (string.IsNullOrWhiteSpace(op.Type)) opErrors.Add("'type' required");
                        break;
                    case "remove_component":
                    case "toggle_component":
                        if (string.IsNullOrWhiteSpace(op.Target)) opErrors.Add("'target' required");
                        if (string.IsNullOrWhiteSpace(op.Component)) opErrors.Add("'component' required");
                        break;
                    case "set_field":
                    case "toggle_field":
                        if (string.IsNullOrWhiteSpace(op.Target)) opErrors.Add("'target' required");
                        if (string.IsNullOrWhiteSpace(op.Component)) opErrors.Add("'component' required");
                        if (string.IsNullOrWhiteSpace(op.Field)) opErrors.Add("'field' required");
                        if (op.Op.Equals("set_field", StringComparison.OrdinalIgnoreCase)
                            && string.IsNullOrWhiteSpace(op.Value))
                        {
                            opErrors.Add("'value' required for set_field");
                        }
                        break;
                }
            }

            errors.AddRange(opErrors.Select(e => $"[{i}] {op.Op}: {e}"));
            items.Add(new MutateValidationItem(i, op.Op, op.Target ?? op.Parent, opErrors.Count == 0, opErrors));
        }

        return new MutateValidationResult(errors.Count == 0, errors, items);
    }
}

public sealed record MutateValidationItem(
    int Index,
    string Op,
    string? Target,
    bool Valid,
    List<string> Errors);

public sealed record MutateValidationResult(
    bool Valid,
    List<string> Errors,
    List<MutateValidationItem> Items);

// ── Tool manifest: deferred category loading ──────────────────────────────────

/// <summary>
/// An MCP server tool dynamically registered from the unifocl manifest.
/// One instance is created per manifest tool entry when a category is loaded.
/// </summary>
internal sealed class ManifestMcpServerTool : McpServerTool
{
    private readonly Tool _protocolTool;

    public string CategoryName { get; }

    public override Tool ProtocolTool => _protocolTool;
    public override IReadOnlyList<object> Metadata => [];

    public ManifestMcpServerTool(UnifoclToolManifest tool, string categoryName)
    {
        CategoryName = categoryName;
        _protocolTool = new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema
        };
    }

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> ctx, CancellationToken ct)
    {
        var arguments = ctx.Params?.Arguments;
        var dryRun = false;

        // Extract dryRun from the args dict before forwarding — it is a transport-level
        // flag, not a user-declared parameter, so it must not appear in argsJson.
        Dictionary<string, JsonElement>? filteredArgs = null;
        if (arguments is not null)
        {
            if (arguments.TryGetValue("dryRun", out var dryRunElement)
                && dryRunElement.ValueKind == JsonValueKind.True)
            {
                dryRun = true;
            }

            // Strip dryRun from the forwarded args so the user method doesn't receive it
            // as an unexpected named parameter.
            filteredArgs = new Dictionary<string, JsonElement>(arguments.Count, StringComparer.Ordinal);
            foreach (var kv in arguments)
            {
                if (!string.Equals(kv.Key, "dryRun", StringComparison.Ordinal))
                {
                    filteredArgs[kv.Key] = kv.Value;
                }
            }
        }

        var argsJson = filteredArgs is not null
            ? JsonSerializer.Serialize(filteredArgs, UnifoclMcpServerMode._jsonOpts)
            : "{}";

        var result = await UnifoclMcpServerMode.ForwardToUnityAsync(ProtocolTool.Name, argsJson, dryRun, ct);
        return new CallToolResult { Content = [new TextContentBlock { Text = result }] };
    }
}

[McpServerToolType]
public static class UnifoclCategoryTools
{
    [McpServerTool, Description(
        "Returns all tool categories available in the unifocl manifest for the active Unity project. " +
        "Call this first to discover which categories exist before calling load_category.")]
    public static GetCategoriesResult GetCategories(McpServer server, UnifoclManifestService manifest)
    {
        if (!manifest.IsManifestLoaded)
        {
            manifest.EnsureLoaded(UnifoclManifestService.ResolveActiveProjectPath());
        }

        var infos = manifest.GetCategoryInfos();
        var categories = new List<CategoryInfo>(infos.Count);
        foreach (var (name, toolCount, active) in infos)
        {
            categories.Add(new CategoryInfo(name, toolCount, active));
        }

        string? hint = null;
        if (!manifest.IsManifestLoaded)
        {
            hint = "No manifest loaded. Open a Unity project first: " +
                   "exec '/open <project-path>' --agentic --project <project-path>, " +
                   "then call get_categories again. " +
                   "If a project is already open and you added new [UnifoclCommand] methods, " +
                   "call reload_manifest after Unity finishes recompiling.";
        }

        return new GetCategoriesResult(manifest.IsManifestLoaded, categories, hint);
    }

    [McpServerTool, Description(
        "Loads a tool category from the unifocl manifest, registering all tools in that category " +
        "as live MCP tools. The MCP client will receive a tools/list_changed notification. " +
        "Call get_categories first to discover available category names.")]
    public static LoadCategoryResult LoadCategory(
        [Description("Exact category name as returned by get_categories.")] string categoryName,
        McpServer server,
        UnifoclManifestService manifest)
    {
        if (!manifest.IsManifestLoaded)
        {
            manifest.EnsureLoaded(UnifoclManifestService.ResolveActiveProjectPath());
        }

        var wasAdded = manifest.LoadCategory(categoryName);
        if (!wasAdded)
        {
            var infos = manifest.GetCategoryInfos();
            var exists = false;
            foreach (var info in infos)
            {
                if (string.Equals(info.Name, categoryName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                return new LoadCategoryResult(false, $"category '{categoryName}' not found in manifest", 0);
            }

            return new LoadCategoryResult(true, $"category '{categoryName}' was already loaded", 0);
        }

        var tools = manifest.GetToolsForCategory(categoryName);
        var added = 0;
        foreach (var tool in tools)
        {
            var mcpTool = new ManifestMcpServerTool(tool, categoryName);
            server.ServerOptions.ToolCollection!.TryAdd(mcpTool);
            added++;
        }

        return new LoadCategoryResult(true, $"category '{categoryName}' loaded: {added} tool(s) registered", added);
    }

    [McpServerTool, Description(
        "Unloads a tool category, removing all its tools from the active MCP tool list. " +
        "The MCP client will receive a tools/list_changed notification.")]
    public static UnloadCategoryResult UnloadCategory(
        [Description("Exact category name to unload.")] string categoryName,
        McpServer server,
        UnifoclManifestService manifest)
    {
        var wasRemoved = manifest.UnloadCategory(categoryName);
        if (!wasRemoved)
        {
            return new UnloadCategoryResult(false, $"category '{categoryName}' was not loaded", 0);
        }

        var toRemove = server.ServerOptions.ToolCollection!
            .ToArray()
            .OfType<ManifestMcpServerTool>()
            .Where(t => string.Equals(t.CategoryName, categoryName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var tool in toRemove)
        {
            server.ServerOptions.ToolCollection!.Remove(tool);
        }

        return new UnloadCategoryResult(true, $"category '{categoryName}' unloaded: {toRemove.Count} tool(s) removed", toRemove.Count);
    }

    [McpServerTool, Description(
        "Force-reloads the tool manifest from disk for the active Unity project, " +
        "even if the manifest was already loaded. " +
        "Call this after Unity recompiles (e.g. you added a new [UnifoclCommand] method) " +
        "to pick up the updated tool list. Returns updated category and tool counts.")]
    public static ReloadManifestResult ReloadManifest(McpServer server, UnifoclManifestService manifest)
    {
        var projectPath = UnifoclManifestService.ResolveActiveProjectPath();
        if (string.IsNullOrEmpty(projectPath))
            return new ReloadManifestResult(false,
                "No active project path found. Open a project first via exec '/open <path>' --agentic --project <path>.",
                0, 0);

        manifest.ForceReload(projectPath);

        var infos = manifest.GetCategoryInfos();
        var totalTools = 0;
        foreach (var (_, toolCount, _) in infos) totalTools += toolCount;

        return new ReloadManifestResult(
            manifest.IsManifestLoaded,
            $"Manifest reloaded: {infos.Count} category/categories, {totalTools} tool(s) available.",
            infos.Count, totalTools);
    }
}

public sealed record CategoryInfo(string Name, int ToolCount, bool Active);
public sealed record GetCategoriesResult(bool ManifestLoaded, List<CategoryInfo> Categories, string? Hint = null);
public sealed record LoadCategoryResult(bool Ok, string Message, int ToolsAdded);
public sealed record UnloadCategoryResult(bool Ok, string Message, int ToolsRemoved);
public sealed record ReloadManifestResult(bool Ok, string Message, int CategoryCount, int TotalTools);

// ── Agentic workflow guide tool ───────────────────────────────────────────────

[McpServerToolType]
public static class UnifoclAgentWorkflowTools
{
    private const string QuickStartJson = """
        {
          "overview": "unifocl controls Unity projects via CLI. Commands run through exec calls with structured JSON output.",
          "preferred_method": "Use the 'exec' MCP tool directly — it handles session state and returns clean JSON. No shell escaping needed.",
          "fallback_method": "unifocl exec '<command>' --agentic --format json --project <path> --session-seed <seed>",
          "common_patterns": {
            "open_project": "exec commands=[\"/open /path/to/project\"] project=\"/path/to/project\"",
            "dump_hierarchy": "exec commands=[\"/dump hierarchy --depth 2\"]",
            "mutate_scene": "exec commands=[\"/mutate [{\\\"op\\\":\\\"create\\\",\\\"type\\\":\\\"canvas\\\",\\\"name\\\":\\\"HUD\\\"}]\"]",
            "inspect_object": "exec commands=[\"inspect /Canvas\", \"set renderMode ScreenSpaceOverlay\"]"
          },
          "modes_summary": "project (default, asset ops) | inspector (per-object mutations) | hierarchy (TUI-only, avoid in agentic)",
          "more_detail": "Call get_agent_workflow_guide(section='<name>') for: exec_flags, modes, mutate, categories, session, discovery, or 'all'"
        }
        """;

    private const string ExecFlagsJson = """
        {
          "exec_flags": {
            "--agentic": "Enable structured JSON output. Required for all agent usage.",
            "--format": "Output format: json (default) or yaml.",
            "--project <path>": "Absolute path to the Unity project directory. Starts daemon if needed.",
            "--mode <mode>": "Initial context: project | inspector | hierarchy. Defaults to 'project' when --project is set.",
            "--session-seed <key>": "Persistence key. Saves/restores mode, inspector target, daemon port, and project path across exec calls. Use a stable per-workflow key (e.g. 'wf-build-ui'). Avoids repeating --project on every call.",
            "--attach-port <port>": "Attach to a specific daemon port instead of auto-discovering.",
            "--request-id <id>": "Custom request identifier echoed in response meta. Useful for correlation.",
            "--dry-run": "Preview mutations without applying (supported by /mutate and inspector set commands)."
          }
        }
        """;

    private const string ModesJson = """
        {
          "mode_guidance": {
            "project": "Default after /open. Used for asset operations (mk script/material/prefab, load scene, upm).",
            "inspector": "Used for per-object mutations: rename, remove, move, comp add/remove, set/toggle fields. Target is preserved in session state via --session-seed.",
            "hierarchy": "TUI-only. Contextual commands (rm/rn/mv) do NOT execute in agentic exec. Use inspector mode or /mutate instead."
          }
        }
        """;

    private const string MutateJson = """
        {
          "batch_mutations": {
            "description": "Prefer /mutate for multi-object scene builds. It infers hierarchy vs inspector context per op and avoids multi-call overhead.",
            "example": "exec '/mutate [{\"op\":\"create\",\"parent\":\"/\",\"type\":\"canvas\",\"name\":\"HUD\"},{\"op\":\"rename\",\"target\":\"/OldName\",\"name\":\"NewName\"}]' --agentic --format json --project /path/to/project"
          },
          "full_schema": "Call get_mutate_schema() for the complete op catalog, required/optional fields, path format, and response shape."
        }
        """;

    private const string CategoriesJson = """
        {
          "custom_commands": {
            "description": "Custom tools defined with [UnifoclCommand] on static C# methods in Unity editor scripts.",
            "discovery_flow": [
              "1. Call get_categories — returns available categories in the loaded manifest.",
              "2. Call load_category with the category name — registers those tools as live MCP tools.",
              "3. The tools are now directly callable as MCP tools."
            ],
            "after_recompile": "After Unity recompiles (new [UnifoclCommand] methods added), call reload_manifest to refresh, then load_category again for new categories.",
            "prerequisite": "A project must be open. If get_categories returns ManifestLoaded:false, run exec '/open <path>' --agentic --project <path> first.",
            "built_in_categories": {
              "profiling": {
                "description": "Lazy-loaded profiling category. Provides capture, analysis, and live telemetry tools backed by Unity Profiler, MemoryProfiler, and ProfilerRecorder APIs.",
                "load": "load_category('profiling')",
                "tools": [
                  "profiling.capabilities — feature probe (SafeRead)",
                  "profiling.inspect — profiler state + memory stats (SafeRead)",
                  "profiling.start_recording / stop_recording — capture control (PrivilegedExec)",
                  "profiling.save_profile / load_profile — .data capture I/O (SafeWrite)",
                  "profiling.take_snapshot — memory snapshot .snap (SafeWrite)",
                  "profiling.frames — frame range stats: CPU/GPU/FPS avg/p50/p95/max (SafeRead)",
                  "profiling.threads / counters — thread enum + counter series (SafeRead)",
                  "profiling.markers — hotspot analysis by total/self time (SafeRead)",
                  "profiling.sample — raw per-sample timing + callstacks (SafeRead)",
                  "profiling.gc_alloc — GC allocation tracking (SafeRead)",
                  "profiling.compare — baseline vs candidate delta (SafeRead)",
                  "profiling.budget_check — CI pass/fail budget rules (SafeRead)",
                  "profiling.export_summary — write stats JSON to disk (SafeRead)",
                  "profiling.live_start / live_stop — ProfilerRecorder telemetry (PrivilegedExec)",
                  "profiling.recorders_list — enumerate available counters (SafeRead)",
                  "profiling.frame_timing — FrameTimingManager CPU/GPU (SafeRead)",
                  "profiling.binary_log_start / binary_log_stop — .raw streaming (PrivilegedExec)",
                  "profiling.annotate_session / annotate_frame — emit metadata (SafeWrite)",
                  "profiling.gpu_capture_begin / gpu_capture_end — RenderDoc/PIX (PrivilegedExec, optional)"
                ]
              }
            }
          }
        }
        """;

    private const string SessionJson = """
        {
          "multi_step_pattern": {
            "description": "Use --session-seed to chain stateful exec calls without repeating context flags.",
            "example": [
              "exec '/open /path/to/project' --agentic --format json --session-seed s1",
              "exec 'inspect /Canvas' --agentic --format json --session-seed s1",
              "exec 'rn HUD_Canvas' --agentic --format json --session-seed s1",
              "exec '/dump hierarchy --depth 2 --format json' --agentic --format json --session-seed s1"
            ]
          },
          "newline_batching": {
            "description": "A single exec call can run multiple commands separated by newlines. State flows across lines.",
            "example": "exec $'inspect /Canvas\\nrn HUD_Canvas\\ncomp add CanvasScaler' --agentic --format json --project /path/to/project"
          },
          "session_storage": ".unifocl-runtime/agentic/sessions/<seed>.json (relative to CWD or project root)"
        }
        """;

    private const string DiscoveryJson = """
        {
          "command_discovery": {
            "description": "Use list_commands and lookup_command to explore all built-in unifocl commands without reading the README.",
            "list_commands": {
              "scope_root": "list_commands(scope='root') — lifecycle commands: /open, /close, /init, /new, /clone, /build, /upm, /mutate, /dump, /profiler, etc.",
              "scope_project": "list_commands(scope='project') — project-mode commands: mk, load, rm, rn, set, upm, build, etc.",
              "scope_inspector": "list_commands(scope='inspector') — inspector-mode commands: set, toggle, comp add/remove, etc.",
              "query": "list_commands(query='build') — filter by keyword across all scopes."
            },
            "lookup_command": "lookup_command('/open') — exact match + fuzzy fallback; returns signature and description for the best match.",
            "when_to_use": "Call list_commands(scope='root') at session start to understand available lifecycle commands before issuing exec calls."
          }
        }
        """;

    private const string WorkflowGuideJson = """
        {
          "exec_flags": {
            "--agentic": "Enable structured JSON output. Required for all agent usage.",
            "--format": "Output format: json (default) or yaml.",
            "--project <path>": "Absolute path to the Unity project directory. Starts daemon if needed.",
            "--mode <mode>": "Initial context: project | inspector | hierarchy. Defaults to 'project' when --project is set.",
            "--session-seed <key>": "Persistence key. Saves/restores mode, inspector target, daemon port, and project path across exec calls. Use a stable per-workflow key (e.g. 'wf-build-ui'). Avoids repeating --project on every call.",
            "--attach-port <port>": "Attach to a specific daemon port instead of auto-discovering.",
            "--request-id <id>": "Custom request identifier echoed in response meta. Useful for correlation.",
            "--dry-run": "Preview mutations without applying (supported by /mutate and inspector set commands)."
          },
          "multi_step_pattern": {
            "description": "Use --session-seed to chain stateful exec calls without repeating context flags.",
            "example": [
              "exec '/open /path/to/project' --agentic --format json --session-seed s1",
              "exec 'inspect /Canvas' --agentic --format json --session-seed s1",
              "exec 'rn HUD_Canvas' --agentic --format json --session-seed s1",
              "exec '/dump hierarchy --depth 2 --format json' --agentic --format json --session-seed s1"
            ]
          },
          "newline_batching": {
            "description": "A single exec call can run multiple commands separated by newlines. State flows across lines.",
            "example": "exec $'inspect /Canvas\\nrn HUD_Canvas\\ncomp add CanvasScaler' --agentic --format json --project /path/to/project"
          },
          "batch_mutations": {
            "description": "Prefer /mutate for multi-object scene builds. It infers hierarchy vs inspector context per op and avoids multi-call overhead.",
            "example": "exec '/mutate [{\"op\":\"create\",\"parent\":\"/\",\"type\":\"canvas\",\"name\":\"HUD\"},{\"op\":\"rename\",\"target\":\"/OldName\",\"name\":\"NewName\"}]' --agentic --format json --project /path/to/project"
          },
          "mode_guidance": {
            "project": "Default after /open. Used for asset operations (mk script/material/prefab, load scene, upm).",
            "inspector": "Used for per-object mutations: rename, remove, move, comp add/remove, set/toggle fields. Target is preserved in session state via --session-seed.",
            "hierarchy": "TUI-only. Contextual commands (rm/rn/mv) do NOT execute in agentic exec. Use inspector mode or /mutate instead."
          },
          "session_storage": ".unifocl-runtime/agentic/sessions/<seed>.json (relative to CWD or project root)",
          "ansi_note": "Some responses prefix JSON with ANSI screen-clear codes. Strip with: sed 's/\\x1b\\[[0-9;]*[mJKH]//g' or find first '{' index before parsing.",
          "custom_commands": {
            "description": "Custom tools defined with [UnifoclCommand] on static C# methods in Unity editor scripts.",
            "discovery_flow": [
              "1. Call get_categories — returns available categories in the loaded manifest.",
              "2. Call load_category with the category name — registers those tools as live MCP tools.",
              "3. The tools are now directly callable as MCP tools."
            ],
            "after_recompile": "After Unity recompiles (new [UnifoclCommand] methods added), call reload_manifest to refresh, then load_category again for new categories.",
            "prerequisite": "A project must be open. If get_categories returns ManifestLoaded:false, run exec '/open <path>' --agentic --project <path> first.",
            "built_in_categories": {
              "profiling": {
                "description": "Lazy-loaded profiling category. Provides capture, analysis, and live telemetry tools backed by Unity Profiler, MemoryProfiler, and ProfilerRecorder APIs.",
                "load": "load_category('profiling')",
                "tools": [
                  "profiling.capabilities — feature probe (SafeRead)",
                  "profiling.inspect — profiler state + memory stats (SafeRead)",
                  "profiling.start_recording / stop_recording — capture control (PrivilegedExec)",
                  "profiling.save_profile / load_profile — .data capture I/O (SafeWrite)",
                  "profiling.take_snapshot — memory snapshot .snap (SafeWrite)",
                  "profiling.frames — frame range stats: CPU/GPU/FPS avg/p50/p95/max (SafeRead)",
                  "profiling.threads / counters — thread enum + counter series (SafeRead)",
                  "profiling.markers — hotspot analysis by total/self time (SafeRead)",
                  "profiling.sample — raw per-sample timing + callstacks (SafeRead)",
                  "profiling.gc_alloc — GC allocation tracking (SafeRead)",
                  "profiling.compare — baseline vs candidate delta (SafeRead)",
                  "profiling.budget_check — CI pass/fail budget rules (SafeRead)",
                  "profiling.export_summary — write stats JSON to disk (SafeRead)",
                  "profiling.live_start / live_stop — ProfilerRecorder telemetry (PrivilegedExec)",
                  "profiling.recorders_list — enumerate available counters (SafeRead)",
                  "profiling.frame_timing — FrameTimingManager CPU/GPU (SafeRead)",
                  "profiling.binary_log_start / binary_log_stop — .raw streaming (PrivilegedExec)",
                  "profiling.annotate_session / annotate_frame — emit metadata (SafeWrite)",
                  "profiling.gpu_capture_begin / gpu_capture_end — RenderDoc/PIX (PrivilegedExec, optional)"
                ]
              }
            }
          },
          "command_discovery": {
            "description": "Use list_commands and lookup_command to explore all built-in unifocl commands without reading the README.",
            "list_commands": {
              "scope_root": "list_commands(scope='root') — lifecycle commands: /open, /close, /init, /new, /clone, /build, /upm, /mutate, /dump, /profiler, etc.",
              "scope_project": "list_commands(scope='project') — project-mode commands: mk, load, rm, rn, set, upm, build, etc.",
              "scope_inspector": "list_commands(scope='inspector') — inspector-mode commands: set, toggle, comp add/remove, etc.",
              "query": "list_commands(query='build') — filter by keyword across all scopes."
            },
            "lookup_command": "lookup_command('/open') — exact match + fuzzy fallback; returns signature and description for the best match.",
            "when_to_use": "Call list_commands(scope='root') at session start to understand available lifecycle commands before issuing exec calls."
          }
        }
        """;

    private static readonly Dictionary<string, string> _sections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["quick_start"] = QuickStartJson,
        ["exec_flags"] = ExecFlagsJson,
        ["modes"] = ModesJson,
        ["mutate"] = MutateJson,
        ["categories"] = CategoriesJson,
        ["session"] = SessionJson,
        ["discovery"] = DiscoveryJson,
        ["all"] = WorkflowGuideJson
    };

    [McpServerTool, Description(
        "Returns unifocl agentic workflow guidance. Call with no section for a quick-start summary (~200 tokens). " +
        "Request specific sections for deeper detail: exec_flags, modes, mutate, categories, session, discovery. " +
        "Replaces the need to read docs or call /help.")]
    public static string GetAgentWorkflowGuide(
        [Description("Section to retrieve: quick_start (default, minimal getting-started), " +
                     "exec_flags (all --flag details), modes (project/inspector/hierarchy semantics), " +
                     "mutate (/mutate batch guidance), categories (custom tool loading), " +
                     "session (--session-seed patterns), discovery (list_commands/lookup_command usage), " +
                     "or 'all' for the complete guide.")]
        string section = "quick_start")
    {
        var key = (section ?? "quick_start").Trim();
        if (_sections.TryGetValue(key, out var content))
            return content;

        return JsonSerializer.Serialize(new
        {
            error = $"unknown section '{key}'",
            available = _sections.Keys.ToList()
        });
    }
}
