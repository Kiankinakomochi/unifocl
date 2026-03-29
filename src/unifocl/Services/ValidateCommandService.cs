using System.Text.Json;
using Spectre.Console;

internal sealed class ValidateCommandService
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HierarchyDaemonClient _daemonClient = new();

    private static readonly string[] AllValidators = ["scene-list", "missing-scripts", "packages", "build-settings", "asmdef", "asset-refs", "addressables"];

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
            log("[x] usage: /validate <scene-list|missing-scripts|packages|build-settings|asmdef|asset-refs|addressables|all>");
            return;
        }

        var subcommand = tokens[1].ToLowerInvariant();
        var validators = subcommand == "all" ? AllValidators : new[] { subcommand };

        foreach (var validator in validators)
        {
            switch (validator)
            {
                case "scene-list":
                case "missing-scripts":
                case "build-settings":
                case "asset-refs":
                case "addressables":
                    await RunDaemonValidatorAsync(validator, session, log);
                    break;
                case "packages":
                    // packages is CLI-only — run locally without daemon
                    RunLocalPackagesValidator(session, log);
                    break;
                case "asmdef":
                    RunLocalAsmdefValidator(session, log);
                    break;
                default:
                    log($"[x] unknown validator: {Markup.Escape(validator)}");
                    log("supported: scene-list | missing-scripts | packages | build-settings | asmdef | asset-refs | addressables | all");
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

    private static void RunLocalAsmdefValidator(CliSessionState session, Action<string> log)
    {
        var projectPath = session.CurrentProjectPath!;
        var bridge = new ProjectDaemonBridge(projectPath);
        var request = new ProjectCommandRequestDto("validate-asmdef", null, null, null, Guid.NewGuid().ToString("N"));
        bridge.TryHandle($"PROJECT_CMD {JsonSerializer.Serialize(request, ReadJsonOptions)}", out var raw);

        ProjectCommandResponseDto? response;
        try
        {
            response = JsonSerializer.Deserialize<ProjectCommandResponseDto>(raw, ReadJsonOptions);
        }
        catch
        {
            log("[red]validate[/]: asmdef — failed to parse bridge response");
            return;
        }

        if (response is null || !response.Ok)
        {
            log($"[red]validate[/]: asmdef — {Markup.Escape(response?.Message ?? "unknown error")}");
            return;
        }

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            log("[green]validate[/]: asmdef passed (no diagnostics)");
            return;
        }

        ValidateResult? result;
        try
        {
            result = JsonSerializer.Deserialize<ValidateResult>(response.Content, ReadJsonOptions);
        }
        catch
        {
            log($"[green]validate[/]: asmdef — {Markup.Escape(response.Message)}");
            return;
        }

        if (result is null)
        {
            log($"[green]validate[/]: asmdef — {Markup.Escape(response.Message)}");
            return;
        }

        RenderResult(result, log);
    }

    public async Task<BuildPreflightResult> BuildPreflightAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        ValidateResult? sceneListResult = null;
        ValidateResult? buildSettingsResult = null;
        ValidateResult? packagesResult = null;

        // Run daemon validators
        foreach (var v in new[] { "scene-list", "build-settings" })
        {
            if (DaemonControlService.GetPort(session) is not int port)
            {
                log($"[yellow]preflight[/]: daemon not running — {v} skipped");
                continue;
            }

            var action = $"validate-{v}";
            var req = new ProjectCommandRequestDto(action, null, null, null, Guid.NewGuid().ToString("N"));
            var response = await _daemonClient.ExecuteProjectCommandAsync(port, req, onStatus: null);

            if (response.Ok && !string.IsNullOrWhiteSpace(response.Content))
            {
                try
                {
                    var r = JsonSerializer.Deserialize<ValidateResult>(response.Content, ReadJsonOptions);
                    if (v == "scene-list") sceneListResult = r;
                    else buildSettingsResult = r;
                }
                catch { /* leave null */ }
            }
        }

        // Run packages validator (CLI-only)
        var projectPath = session.CurrentProjectPath!;
        var bridge = new ProjectDaemonBridge(projectPath);
        var pkgRequest = new ProjectCommandRequestDto("validate-packages", null, null, null, Guid.NewGuid().ToString("N"));
        bridge.TryHandle($"PROJECT_CMD {JsonSerializer.Serialize(pkgRequest, ReadJsonOptions)}", out var pkgRaw);
        try
        {
            var pkgResponse = JsonSerializer.Deserialize<ProjectCommandResponseDto>(pkgRaw, ReadJsonOptions);
            if (pkgResponse?.Ok == true && !string.IsNullOrWhiteSpace(pkgResponse.Content))
            {
                packagesResult = JsonSerializer.Deserialize<ValidateResult>(pkgResponse.Content, ReadJsonOptions);
            }
        }
        catch { /* leave null */ }

        var totalErrors = (sceneListResult?.ErrorCount ?? 0) + (buildSettingsResult?.ErrorCount ?? 0) + (packagesResult?.ErrorCount ?? 0);
        var totalWarnings = (sceneListResult?.WarningCount ?? 0) + (buildSettingsResult?.WarningCount ?? 0) + (packagesResult?.WarningCount ?? 0);
        var passed = totalErrors == 0;

        return new BuildPreflightResult(passed, totalErrors, totalWarnings, sceneListResult, buildSettingsResult, packagesResult);
    }

    private static void RenderResult(ValidateResult result, Action<string> log)
    {
        var statusColor = result.Passed ? "green" : "red";
        var statusText = result.Passed ? "PASS" : "FAIL";
        log($"[{statusColor}]{statusText}[/] [white]{Markup.Escape(result.Validator)}[/] — {result.ErrorCount} error(s), {result.WarningCount} warning(s)");

        foreach (var diag in result.Diagnostics)
        {
            var sevColor = diag.Severity switch
            {
                ValidateSeverity.Error => "red",
                ValidateSeverity.Warning => "yellow",
                _ => "grey"
            };
            var location = diag.AssetPath ?? diag.ObjectPath ?? "";
            var fixHint = diag.Fixable ? " [grey](fixable)[/]" : "";
            log($"  [{sevColor}]{diag.Severity}[/] [{sevColor}]{Markup.Escape(diag.ErrorCode)}[/]: {Markup.Escape(diag.Message)}{(string.IsNullOrEmpty(location) ? "" : $" [grey]@ {Markup.Escape(location)}[/]")}{fixHint}");
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
