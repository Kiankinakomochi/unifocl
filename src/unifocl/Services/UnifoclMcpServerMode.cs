using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    [McpServerTool, Description(
        "Lists unifocl commands. Defaults to 'core' category for a lean response. " +
        "Use category='all' to see everything, or filter by specific category: " +
        "core, setup, build, validate, diag, test, upm, addressable, asset, scene, compile, eval, profiling, prefab, animation.")]
    public static CommandLookupResult ListCommands(
        [Description("Command scope filter: root, project, inspector, or all.")] string scope = "all",
        [Description("Category filter: core (default, essential commands), or a specific domain: " +
                     "build, validate, diag, test, upm, addressable, asset, scene, compile, eval, profiling, prefab, animation, setup. " +
                     "Use 'all' to list every command across all categories.")]
        string category = "core",
        [Description("Optional case-insensitive search across trigger/signature/description.")] string? query = null,
        [Description("Max commands to return (1-400).")] int limit = 120)
    {
        var normalizedScope = NormalizeScope(scope);
        var normalizedCategory = (category ?? "core").Trim().ToLowerInvariant();
        var normalizedQuery = query?.Trim();
        var commandRows = EnumerateCommands(normalizedScope);

        // Category filter (before query filter)
        if (normalizedCategory != "all")
        {
            commandRows = commandRows.Where(row =>
                string.Equals(row.Spec.Category, normalizedCategory, StringComparison.OrdinalIgnoreCase));
        }

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
                row.Spec.Description,
                row.Spec.Category))
            .ToList();

        var capped = Math.Clamp(limit, 1, 400);
        var returned = allMatches.Take(capped).ToList();

        // Populate available categories when result is empty or showing all
        List<string>? availableCategories = null;
        if (returned.Count == 0 || normalizedCategory == "all")
        {
            availableCategories = EnumerateCommands(normalizedScope)
                .Select(row => row.Spec.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();
        }

        return new CommandLookupResult(
            Scope: normalizedScope,
            Query: normalizedQuery ?? string.Empty,
            Total: allMatches.Count,
            Returned: returned.Count,
            Commands: returned,
            AvailableCategories: availableCategories);
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
                row.Spec.Description,
                row.Spec.Category))
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
    string Description,
    string Category);

public sealed record CommandLookupResult(
    string Scope,
    string Query,
    int Total,
    int Returned,
    List<CommandLookupItem> Commands,
    List<string>? AvailableCategories = null);

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
          "mode_compatibility": "All ops work in both Host mode (batch daemon) and Bridge mode (interactive editor). Note: add_component for newly created scripts requires a /close + /open cycle in Host mode (scripts compile only at startup). Run /validate scripts first to catch errors before restarting.",
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
        "Activates a tool category in one step: loads the manifest if needed, then registers all tools " +
        "from the named category as live MCP tools. Returns the list of activated tool names. " +
        "Equivalent to calling get_categories + load_category, but in a single round-trip. " +
        "If the category is already loaded, returns success with the existing tool list.")]
    public static UseCategoryResult UseCategory(
        [Description("Category name (e.g. 'profiling'). Call get_categories to discover available names, " +
                     "or try the name directly — this tool will return an error with available categories if not found.")]
        string categoryName,
        McpServer server,
        UnifoclManifestService manifest)
    {
        // Step 1: Ensure manifest is loaded
        if (!manifest.IsManifestLoaded)
        {
            var projectPath = UnifoclManifestService.ResolveActiveProjectPath();
            if (string.IsNullOrEmpty(projectPath))
            {
                return new UseCategoryResult(false,
                    "No active project. Open a project first, then retry.",
                    0, [], null);
            }
            manifest.EnsureLoaded(projectPath);
        }

        // Step 2: Check if category exists
        var infos = manifest.GetCategoryInfos();
        var categoryExists = false;
        var alreadyActive = false;
        foreach (var info in infos)
        {
            if (string.Equals(info.Name, categoryName, StringComparison.OrdinalIgnoreCase))
            {
                categoryExists = true;
                alreadyActive = info.Active;
                break;
            }
        }

        if (!categoryExists)
        {
            var available = infos.Select(i => i.Name).ToList();
            return new UseCategoryResult(false,
                $"Category '{categoryName}' not found in manifest.",
                0, [], available);
        }

        // Step 3: Load if not already active
        if (!alreadyActive)
        {
            manifest.LoadCategory(categoryName);
            var tools = manifest.GetToolsForCategory(categoryName);
            var toolNames = new List<string>();
            foreach (var tool in tools)
            {
                var mcpTool = new ManifestMcpServerTool(tool, categoryName);
                server.ServerOptions.ToolCollection!.TryAdd(mcpTool);
                toolNames.Add(tool.Name);
            }

            return new UseCategoryResult(true,
                $"Category '{categoryName}' activated: {toolNames.Count} tool(s) registered.",
                toolNames.Count, toolNames, null);
        }

        // Already loaded — return tool names
        var existingTools = manifest.GetToolsForCategory(categoryName);
        var existingNames = existingTools.Select(t => t.Name).ToList();
        return new UseCategoryResult(true,
            $"Category '{categoryName}' was already active: {existingNames.Count} tool(s) available.",
            existingNames.Count, existingNames, null);
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
public sealed record UseCategoryResult(
    bool Ok,
    string Message,
    int ToolCount,
    List<string> Tools,
    List<string>? AvailableCategories);

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
          "script_workflow": "When creating C# scripts: (1) write all .cs files first via asset.create_script, (2) run /validate scripts for offline Roslyn compile check (no editor needed), (3) fix errors before opening/restarting the project. This avoids costly /close + /open cycles for compile failures.",
          "modes_summary": "project (default, asset ops) | inspector (per-object mutations) | hierarchy (TUI-only, avoid in agentic)",
          "more_detail": "Call get_agent_workflow_guide(section='<name>') for: exec_flags, modes, mutate, categories, session, discovery, script_workflow, debug_artifact, or 'all'"
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
          },
          "host_mode_compilation": {
            "limitation": "In Host mode (-batchmode), Unity compiles scripts ONLY at startup. Mid-session script creation does NOT trigger recompilation — AssetDatabase.Refresh() only refreshes asset metadata, not scripts.",
            "workaround": "Write all .cs files first, run /validate scripts (offline Roslyn check, no editor needed), then /close + /open to compile. Never add scripts mid-session and expect add_component to find the new types.",
            "bridge_mode": "In Bridge mode (GUI editor), /compile request triggers live recompilation. No /close + /open needed."
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
              "1. Call use_category(name) — preferred single-step: loads manifest if needed, registers tools, returns tool list.",
              "   OR the two-step alternative: get_categories to list names, then load_category(name) to register.",
              "2. The tools are now directly callable as MCP tools."
            ],
            "after_recompile": "After Unity recompiles (new [UnifoclCommand] methods added), call reload_manifest to refresh, then use_category again for new categories.",
            "prerequisite": "A project must be open. If use_category returns an error about no manifest, run exec '/open <path>' --agentic --project <path> first.",
            "built_in_categories": {
              "profiling": {
                "description": "Lazy-loaded profiling category. Provides capture, analysis, and live telemetry tools backed by Unity Profiler, MemoryProfiler, and ProfilerRecorder APIs.",
                "load": "use_category('profiling')",
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
          "host_mode_compilation": {
            "limitation": "In Host mode (-batchmode), Unity compiles scripts ONLY at startup. Mid-session script creation does NOT trigger recompilation.",
            "workaround": "Write all .cs files first, run /validate scripts (offline Roslyn check), then /close + /open to compile.",
            "bridge_mode": "In Bridge mode (GUI editor), /compile request triggers live recompilation."
          },
          "script_workflow": {
            "recommended_steps": [
              "1. Create all .cs scripts upfront via asset.create_script",
              "2. Run /validate scripts — offline compile check, no editor needed",
              "3. Run /validate asmdef — verify assembly references",
              "4. Fix errors and re-validate until clean",
              "5. /open (or /close + /open) to compile scripts at startup",
              "6. Proceed with add_component, /mutate, inspector ops"
            ],
            "why": "Each /close + /open cycle takes 30s+. Catching compile errors offline avoids wasted restarts."
          },
          "session_storage": ".unifocl-runtime/agentic/sessions/<seed>.json (relative to CWD or project root)",
          "ansi_note": "Some responses prefix JSON with ANSI screen-clear codes. Strip with: sed 's/\\x1b\\[[0-9;]*[mJKH]//g' or find first '{' index before parsing.",
          "custom_commands": {
            "description": "Custom tools defined with [UnifoclCommand] on static C# methods in Unity editor scripts.",
            "discovery_flow": [
              "1. Call use_category(name) — preferred single-step: loads manifest if needed, registers tools, returns tool list.",
              "   OR the two-step alternative: get_categories to list names, then load_category(name) to register.",
              "2. The tools are now directly callable as MCP tools."
            ],
            "after_recompile": "After Unity recompiles (new [UnifoclCommand] methods added), call reload_manifest to refresh, then use_category again for new categories.",
            "prerequisite": "A project must be open. If use_category returns an error about no manifest, run exec '/open <path>' --agentic --project <path> first.",
            "built_in_categories": {
              "profiling": {
                "description": "Lazy-loaded profiling category. Provides capture, analysis, and live telemetry tools backed by Unity Profiler, MemoryProfiler, and ProfilerRecorder APIs.",
                "load": "use_category('profiling')",
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

    private const string ScriptWorkflowJson = """
        {
          "script_workflow": {
            "overview": "In Host mode (batch), Unity only compiles scripts at startup. New .cs files added mid-session are invisible to the type system until the next /close + /open cycle. Use this workflow to avoid wasted restarts.",
            "recommended_steps": [
              "1. Create all .cs scripts upfront: use asset.create_script (or write files directly to Assets/)",
              "2. Run /validate scripts — offline Roslyn compile check against Unity stubs. No running editor needed. Catches CS#### errors before paying the startup cost.",
              "3. Run /validate asmdef — verify assembly definition references, detect duplicates and cycles.",
              "4. Fix any errors found in steps 2-3. Re-run validation until clean.",
              "5. Now /open the project (or /close + /open if already open). Scripts compile on startup and new types become available.",
              "6. Proceed with add_component, /mutate, inspector ops, etc."
            ],
            "why": "Each /close + /open cycle takes 30s+ for project reimport. Catching compile errors offline saves multiple restart cycles.",
            "bridge_mode_alternative": "In Bridge mode (GUI editor), use /compile request for live recompilation after script changes. No restart needed.",
            "validate_scripts_details": {
              "what_it_checks": "Syntax errors, type resolution, missing references — same CS#### diagnostics as Unity's compiler.",
              "what_it_references": "Unity Managed DLLs (auto-discovered from installed editors), Library/ScriptAssemblies (existing asmdef outputs), template packages (UI, TextMeshPro).",
              "exec_usage": "exec commands=[\"/validate scripts\"] project=\"/path/to/project\"",
              "execv2_usage": "operation: validate.scripts (SafeRead, no approval needed)"
            }
          }
        }
        """;

    private const string DebugArtifactJson = """
        {
          "debug_artifact": {
            "overview": "Tiered debug report that captures project state as structured JSON. Use prep to set up instrumentation, then collect after reproducing the issue.",
            "tiers": {
              "0": "Environment only: PlayerSettings, compile status (~2-5KB)",
              "1": "Standard: + console errors/warnings, compile errors, 6 validators (~20-50KB)",
              "2": "Extended: + hierarchy snapshot, build report, profiler state, frame timing (~100-300KB)",
              "3": "Full: + profiler frames/GC alloc/markers, recorder output, memory snapshot (~500KB-2MB)"
            },
            "workflow": [
              "1. Prep: exec '/debug-artifact prep --tier 3' — clears console, starts profiler (T2+), starts recorder (T3). Returns next-step instructions.",
              "2. Playmode: exec '/playmode start' — enter playmode, reproduce the issue.",
              "3. Stop captures: exec '/playmode stop' then exec '/profiler stop', and if T3: exec '/recorder stop'.",
              "4. Collect: exec '/debug-artifact collect --tier 3' — snapshots all data, writes artifact JSON to .unifocl-runtime/artifacts/.",
              "5. Read the artifact file and use it to generate reports, create tickets, or triage."
            ],
            "exec_examples": {
              "prep_t2": "exec '/debug-artifact prep --tier 2' --agentic --session-seed dbg",
              "prep_t3": "exec '/debug-artifact prep --tier 3' --agentic --session-seed dbg",
              "playmode_start": "exec '/playmode start' --agentic --session-seed dbg",
              "playmode_stop": "exec '/playmode stop' --agentic --session-seed dbg",
              "profiler_stop": "exec '/profiler stop' --agentic --session-seed dbg",
              "recorder_stop": "exec '/recorder stop' --agentic --session-seed dbg",
              "collect": "exec '/debug-artifact collect --tier 3' --agentic --session-seed dbg"
            },
            "execv2_operations": {
              "debug-artifact.prep": "PrivilegedExec — starts profiler/recorder. Args: { tier: 0-3 }",
              "debug-artifact.collect": "SafeRead — snapshots current state. Args: { tier: 0-3, ticketMeta?: { title, severity, labels, repro } }"
            },
            "key_points": [
              "prep is PrivilegedExec (starts profiler/recorder), collect is SafeRead (read-only snapshot).",
              "Always stop profiler and recorder BEFORE collecting — collect reads whatever state exists at call time.",
              "T0/T1 do not need prep — they only read console logs and validation data that are always available.",
              "The artifact JSON preserves raw daemon responses. Use ticketMeta to pre-populate issue tracker fields.",
              "Artifacts are written to {projectPath}/.unifocl-runtime/artifacts/{timestamp}-debug-artifact.json."
            ],
            "output_schema": "See docs/schemas/debug-artifact.schema.json for the full JSON Schema definition."
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
        ["script_workflow"] = ScriptWorkflowJson,
        ["debug_artifact"] = DebugArtifactJson,
        ["all"] = WorkflowGuideJson
    };

    [McpServerTool, Description(
        "Returns unifocl agentic workflow guidance. Call with no section for a quick-start summary (~200 tokens). " +
        "Request specific sections for deeper detail: exec_flags, modes, mutate, categories, session, discovery, script_workflow, debug_artifact. " +
        "Replaces the need to read docs or call /help.")]
    public static string GetAgentWorkflowGuide(
        [Description("Section to retrieve: quick_start (default, minimal getting-started), " +
                     "exec_flags (all --flag details), modes (project/inspector/hierarchy semantics), " +
                     "mutate (/mutate batch guidance), categories (custom tool loading), " +
                     "session (--session-seed patterns), discovery (list_commands/lookup_command usage), " +
                     "script_workflow (write-validate-open pattern for Host mode), " +
                     "debug_artifact (prep-playmode-collect workflow for debug reports), " +
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

// ── Native exec tool ──────────────────────────────────────────────────────────

[McpServerToolType]
public static class McpExecTools
{
    // Connection-scoped implicit session state
    private static string _implicitSessionSeed = $"mcp-{Guid.NewGuid():N}";
    private static string? _lastProject;

    [McpServerTool, Description(
        "Executes one or more unifocl commands and returns structured JSON results. " +
        "Commands run sequentially with shared session state — no need for --session-seed or shell escaping. " +
        "First call should include 'project' to open a Unity project. Subsequent calls reuse the session automatically.")]
    public static async Task<McpExecResult> Exec(
        [Description("Array of command strings to execute sequentially. State flows between commands. " +
                     "Examples: [\"/open /path/to/project\"], [\"inspect /Canvas\", \"set renderMode ScreenSpaceOverlay\"], " +
                     "[\"/mutate [{...}]\"], [\"/dump hierarchy --depth 2\"]")]
        string[] commands,
        [Description("Absolute path to Unity project. Required on first call, remembered for subsequent calls.")]
        string? project = null,
        [Description("Preview mutations without applying.")]
        bool dryRun = false,
        CancellationToken ct = default)
    {
        if (commands is null || commands.Length == 0)
        {
            return new McpExecResult(
                Ok: false,
                Status: "error",
                Data: null,
                Errors: ["No commands provided. Pass at least one command string."],
                Warnings: null,
                SessionSeed: _implicitSessionSeed,
                Mode: null);
        }

        // Resolve project: explicit param > remembered > null
        var resolvedProject = project ?? _lastProject;
        if (project is not null)
            _lastProject = project;

        // Join commands with newline for batch execution
        var joinedCommands = string.Join("\n", commands);

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return new McpExecResult(
                Ok: false,
                Status: "error",
                Data: null,
                Errors: ["Cannot resolve unifocl binary path (Environment.ProcessPath is null)."],
                Warnings: null,
                SessionSeed: _implicitSessionSeed,
                Mode: null);
        }

        var psi = new ProcessStartInfo(processPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Use ArgumentList to avoid shell escaping entirely
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(joinedCommands);
        psi.ArgumentList.Add("--agentic");
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add("json");
        if (resolvedProject is not null)
        {
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(resolvedProject);
        }
        psi.ArgumentList.Add("--session-seed");
        psi.ArgumentList.Add(_implicitSessionSeed);
        if (dryRun)
            psi.ArgumentList.Add("--dry-run");

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return new McpExecResult(
                    Ok: false,
                    Status: "error",
                    Data: null,
                    Errors: ["Failed to start unifocl process."],
                    Warnings: null,
                    SessionSeed: _implicitSessionSeed,
                    Mode: null);
            }

            // Read stdout and stderr concurrently to avoid deadlocks
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            // Timeout: 30 seconds
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return new McpExecResult(
                    Ok: false,
                    Status: "error",
                    Data: null,
                    Errors: ["Command execution timed out after 30 seconds."],
                    Warnings: null,
                    SessionSeed: _implicitSessionSeed,
                    Mode: null);
            }

            var stdout = StripAnsi(await stdoutTask);
            var stderr = await stderrTask;

            // Remember project from successful /open calls
            if (resolvedProject is not null && process.ExitCode == 0)
                _lastProject = resolvedProject;

            // Parse the JSON envelope from stdout
            return ParseEnvelope(stdout, stderr, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            throw; // propagate cancellation
        }
        catch (Exception ex)
        {
            return new McpExecResult(
                Ok: false,
                Status: "error",
                Data: null,
                Errors: [$"Process execution failed: {ex.Message}"],
                Warnings: null,
                SessionSeed: _implicitSessionSeed,
                Mode: null);
        }
    }

    private static McpExecResult ParseEnvelope(string stdout, string stderr, int exitCode)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            var errors = new List<string> { $"No output from unifocl (exit code {exitCode})." };
            if (!string.IsNullOrWhiteSpace(stderr))
                errors.Add($"stderr: {stderr.Trim()}");
            return new McpExecResult(
                Ok: false,
                Status: "error",
                Data: null,
                Errors: errors,
                Warnings: null,
                SessionSeed: _implicitSessionSeed,
                Mode: null);
        }

        // Find the first '{' to skip any non-JSON prefix
        var jsonStart = stdout.IndexOf('{');
        if (jsonStart < 0)
        {
            return new McpExecResult(
                Ok: false,
                Status: "error",
                Data: null,
                Errors: [$"No JSON found in output (exit code {exitCode}). Raw: {stdout[..Math.Min(stdout.Length, 500)]}"],
                Warnings: null,
                SessionSeed: _implicitSessionSeed,
                Mode: null);
        }

        var jsonText = stdout[jsonStart..];

        try
        {
            var envelope = JsonSerializer.Deserialize<AgenticResponseEnvelope>(
                jsonText, UnifoclMcpServerMode._jsonOpts);

            if (envelope is null)
            {
                return new McpExecResult(
                    Ok: false,
                    Status: "error",
                    Data: null,
                    Errors: ["Failed to deserialize response envelope."],
                    Warnings: null,
                    SessionSeed: _implicitSessionSeed,
                    Mode: null);
            }

            var ok = string.Equals(envelope.Status, "success", StringComparison.OrdinalIgnoreCase);
            var errors = envelope.Errors?.Count > 0
                ? envelope.Errors.Select(e => string.IsNullOrEmpty(e.Hint) ? e.Message : $"{e.Message} (hint: {e.Hint})").ToList()
                : null;
            var warnings = envelope.Warnings?.Count > 0
                ? envelope.Warnings.Select(w => w.Message).ToList()
                : null;

            return new McpExecResult(
                Ok: ok,
                Status: envelope.Status,
                Data: envelope.Data,
                Errors: errors,
                Warnings: warnings,
                SessionSeed: _implicitSessionSeed,
                Mode: envelope.Mode);
        }
        catch (JsonException ex)
        {
            return new McpExecResult(
                Ok: false,
                Status: "error",
                Data: null,
                Errors: [$"JSON parse error: {ex.Message}. Raw start: {jsonText[..Math.Min(jsonText.Length, 300)]}"],
                Warnings: null,
                SessionSeed: _implicitSessionSeed,
                Mode: null);
        }
    }

    private static string StripAnsi(string input)
        => Regex.Replace(input, @"\x1b\[[0-9;]*[mJKH]", "");
}

public sealed record McpExecResult(
    bool Ok,
    string Status,
    object? Data,
    List<string>? Errors,
    List<string>? Warnings,
    string? SessionSeed,
    string? Mode);
