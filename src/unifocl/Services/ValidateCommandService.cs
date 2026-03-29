using System.Text.Json;
using Spectre.Console;

internal sealed class ValidateCommandService
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HierarchyDaemonClient _daemonClient = new();

    private static readonly string[] AllValidators = ["scene-list", "missing-scripts", "packages", "build-settings"];

    public async Task HandleValidateCommandAsync(
        string input,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            log("[yellow]validate[/]: open a project first with /open");
            return;
        }

        var tokens = Tokenize(input);
        if (tokens.Count < 2)
        {
            log("[x] usage: /validate <scene-list|missing-scripts|packages|build-settings|all>");
            return;
        }

        var subcommand = tokens[1].ToLowerInvariant();
        var validators = subcommand == "all" ? AllValidators : new[] { subcommand };

        var isAll = subcommand == "all";
        var first = true;
        foreach (var validator in validators)
        {
            if (isAll && !first)
            {
                var sep = "─── " + new string('─', 40);
                log($"[{CliTheme.TextMuted}]{sep}[/]");
            }

            first = false;

            switch (validator)
            {
                case "scene-list":
                case "missing-scripts":
                case "build-settings":
                    await RunDaemonValidatorAsync(validator, session, log);
                    break;
                case "packages":
                    // packages is CLI-only — run locally without daemon
                    RunLocalPackagesValidator(session, log);
                    break;
                default:
                    log($"[x] unknown validator: {Markup.Escape(validator)}");
                    log("supported: scene-list | missing-scripts | packages | build-settings | all");
                    return;
            }
        }
    }

    private async Task RunDaemonValidatorAsync(string validator, CliSessionState session, Action<string> log)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            log($"[yellow]validate[/]: daemon not running — start with /open");
            return;
        }

        var action = $"validate-{validator}";
        var request = new ProjectCommandRequestDto(action, null, null, null, Guid.NewGuid().ToString("N"));

        log($"[grey]validate[/]: running {Markup.Escape(validator)}...");
        var response = await _daemonClient.ExecuteProjectCommandAsync(port, request,
            onStatus: status => log($"[grey]validate[/]: {Markup.Escape(status)}"));

        if (!response.Ok)
        {
            log($"[red]validate[/]: {Markup.Escape(validator)} failed — {Markup.Escape(response.Message)}");
            return;
        }

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            log($"[green]validate[/]: {Markup.Escape(validator)} passed (no diagnostics)");
            return;
        }

        ValidateResult? result;
        try
        {
            result = JsonSerializer.Deserialize<ValidateResult>(response.Content, ReadJsonOptions);
        }
        catch
        {
            log($"[green]validate[/]: {Markup.Escape(validator)} — {Markup.Escape(response.Message)}");
            return;
        }

        if (result is null)
        {
            log($"[green]validate[/]: {Markup.Escape(validator)} — {Markup.Escape(response.Message)}");
            return;
        }

        RenderResult(result, log);
    }

    private static void RunLocalPackagesValidator(CliSessionState session, Action<string> log)
    {
        var projectPath = session.CurrentProjectPath!;
        var bridge = new ProjectDaemonBridge(projectPath);
        var request = new ProjectCommandRequestDto("validate-packages", null, null, null, Guid.NewGuid().ToString("N"));
        bridge.TryHandle($"PROJECT_CMD {JsonSerializer.Serialize(request, ReadJsonOptions)}", out var raw);

        ProjectCommandResponseDto? response;
        try
        {
            response = JsonSerializer.Deserialize<ProjectCommandResponseDto>(raw, ReadJsonOptions);
        }
        catch
        {
            log("[red]validate[/]: packages — failed to parse bridge response");
            return;
        }

        if (response is null || !response.Ok)
        {
            log($"[red]validate[/]: packages — {Markup.Escape(response?.Message ?? "unknown error")}");
            return;
        }

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            log("[green]validate[/]: packages passed (no diagnostics)");
            return;
        }

        ValidateResult? result;
        try
        {
            result = JsonSerializer.Deserialize<ValidateResult>(response.Content, ReadJsonOptions);
        }
        catch
        {
            log($"[green]validate[/]: packages — {Markup.Escape(response.Message)}");
            return;
        }

        if (result is null)
        {
            log($"[green]validate[/]: packages — {Markup.Escape(response.Message)}");
            return;
        }

        RenderResult(result, log);
    }

    private static void RenderResult(ValidateResult result, Action<string> log)
    {
        var statusColor = result.Passed ? CliTheme.Success : CliTheme.Error;
        var statusIcon = result.Passed ? "✓" : "✗";
        log($"[bold {statusColor}]{statusIcon}[/] [{CliTheme.TextPrimary}]{Markup.Escape(result.Validator)}[/]  " +
            $"[{CliTheme.Error}]{result.ErrorCount} error(s)[/]  " +
            $"[{CliTheme.Warning}]{result.WarningCount} warning(s)[/]");

        if (result.Diagnostics.Count == 0)
            return;

        // Errors first, then warnings, then info
        var grouped = result.Diagnostics
            .OrderBy(d => d.Severity switch
            {
                ValidateSeverity.Error => 0,
                ValidateSeverity.Warning => 1,
                _ => 2
            })
            .ToList();

        foreach (var diag in grouped)
        {
            var (sevColor, sevIcon) = diag.Severity switch
            {
                ValidateSeverity.Error => (CliTheme.Error, "✗"),
                ValidateSeverity.Warning => (CliTheme.Warning, "⚠"),
                _ => (CliTheme.Info, "i")
            };
            var location = diag.AssetPath ?? diag.ObjectPath ?? string.Empty;
            var locationPart = string.IsNullOrEmpty(location)
                ? string.Empty
                : $" [{CliTheme.TextMuted}]@ {Markup.Escape(location)}[/]";
            var fixPart = diag.Fixable ? $" [{CliTheme.TextMuted}](fixable)[/]" : string.Empty;
            log($"  [{sevColor}]{sevIcon}[/] [{CliTheme.TextMuted}]{Markup.Escape(diag.ErrorCode)}[/] {Markup.Escape(diag.Message)}{locationPart}{fixPart}");
        }
    }

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var span = input.AsSpan().Trim();
        while (!span.IsEmpty)
        {
            if (span[0] == '"' || span[0] == '\'')
            {
                var quote = span[0];
                span = span[1..];
                var end = span.IndexOf(quote);
                if (end < 0) end = span.Length;
                tokens.Add(span[..end].ToString());
                span = end < span.Length ? span[(end + 1)..].TrimStart() : ReadOnlySpan<char>.Empty;
            }
            else
            {
                var end = span.IndexOf(' ');
                if (end < 0) end = span.Length;
                tokens.Add(span[..end].ToString());
                span = end < span.Length ? span[(end + 1)..].TrimStart() : ReadOnlySpan<char>.Empty;
            }
        }

        return tokens;
    }
}
