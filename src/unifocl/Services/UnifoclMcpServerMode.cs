using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

internal static class UnifoclMcpServerMode
{
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
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync(cancellationToken);
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
