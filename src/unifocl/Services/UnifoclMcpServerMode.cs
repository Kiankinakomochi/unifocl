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
        var runtimeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".unifocl-runtime");
        var runtime = new DaemonRuntime(runtimeRoot);
        var daemon = runtime.GetAll().FirstOrDefault();
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

        return new GetCategoriesResult(manifest.IsManifestLoaded, categories);
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
}

public sealed record CategoryInfo(string Name, int ToolCount, bool Active);
public sealed record GetCategoriesResult(bool ManifestLoaded, List<CategoryInfo> Categories);
public sealed record LoadCategoryResult(bool Ok, string Message, int ToolsAdded);
public sealed record UnloadCategoryResult(bool Ok, string Message, int ToolsRemoved);

// ── Agentic workflow guide tool ───────────────────────────────────────────────

[McpServerToolType]
public static class UnifoclAgentWorkflowTools
{
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
          "ansi_note": "Some responses prefix JSON with ANSI screen-clear codes. Strip with: sed 's/\\x1b\\[[0-9;]*[mJKH]//g' or find first '{' index before parsing."
        }
        """;

    [McpServerTool, Description(
        "Returns the unifocl agentic exec workflow guide: all exec flags (including --session-seed), " +
        "multi-step session patterns, newline batching, /mutate preference guidance, and mode semantics. " +
        "Call this once at the start of any agentic workflow to understand how to chain exec calls " +
        "without repeating context flags.")]
    public static string GetAgentWorkflowGuide() => WorkflowGuideJson;
}
