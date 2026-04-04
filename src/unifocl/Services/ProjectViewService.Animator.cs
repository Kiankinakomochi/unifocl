using System.Text.Json;

internal sealed partial class ProjectViewService
{
    private async Task<bool> HandleAnimatorCommandAsync(
        CliSessionState session,
        IReadOnlyList<string> tokens,
        List<string> outputs)
    {
        // tokens[0]="animator"
        if (tokens.Count < 3)
        {
            outputs.Add("[x] usage: animator <param|state|transition> <subcommand> [args...]");
            outputs.Add("[x]   animator param add <asset-path> <name> <type>");
            outputs.Add("[x]   animator param remove <asset-path> <name>");
            outputs.Add("[x]   animator state add <asset-path> <name> [--layer <n>]");
            outputs.Add("[x]   animator transition add <asset-path> <from-state> <to-state> [--layer <n>]");
            return true;
        }

        var domain = tokens[1];
        var subcommand = tokens[2];

        if (domain.Equals("param", StringComparison.OrdinalIgnoreCase))
        {
            if (subcommand.Equals("add", StringComparison.OrdinalIgnoreCase))
                return await HandleAnimatorParamAddAsync(tokens, session, outputs);
            if (subcommand.Equals("remove", StringComparison.OrdinalIgnoreCase))
                return await HandleAnimatorParamRemoveAsync(tokens, session, outputs);
        }
        else if (domain.Equals("state", StringComparison.OrdinalIgnoreCase))
        {
            if (subcommand.Equals("add", StringComparison.OrdinalIgnoreCase))
                return await HandleAnimatorStateAddAsync(tokens, session, outputs);
        }
        else if (domain.Equals("transition", StringComparison.OrdinalIgnoreCase))
        {
            if (subcommand.Equals("add", StringComparison.OrdinalIgnoreCase))
                return await HandleAnimatorTransitionAddAsync(tokens, session, outputs);
        }

        outputs.Add($"[x] unknown animator command: animator {domain} {subcommand}");
        return true;
    }

    private async Task<bool> HandleAnimatorParamAddAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        // tokens: animator param add <asset-path> <name> <type>
        if (tokens.Count < 6)
        {
            outputs.Add("[x] usage: animator param add <asset-path> <name> <type>");
            outputs.Add("[x]   type must be: float | int | bool | trigger");
            return true;
        }

        var assetPath = tokens[3];
        var name = tokens[4];
        var type = tokens[5];
        var content = JsonSerializer.Serialize(new { name, type });

        var response = await RunTrackableProgressAsync(
            session,
            $"adding animator parameter '{name}'",
            TimeSpan.FromSeconds(8),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("animator-param-add", assetPath, null, content)));

        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("animator param add", response.Message));
            return true;
        }

        outputs.Add($"[+] {response.Message}");
        return true;
    }

    private async Task<bool> HandleAnimatorParamRemoveAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        // tokens: animator param remove <asset-path> <name>
        if (tokens.Count < 5)
        {
            outputs.Add("[x] usage: animator param remove <asset-path> <name>");
            return true;
        }

        var assetPath = tokens[3];
        var name = tokens[4];
        var content = JsonSerializer.Serialize(new { name });

        var response = await RunTrackableProgressAsync(
            session,
            $"removing animator parameter '{name}'",
            TimeSpan.FromSeconds(8),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("animator-param-remove", assetPath, null, content)));

        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("animator param remove", response.Message));
            return true;
        }

        outputs.Add($"[+] {response.Message}");
        return true;
    }

    private async Task<bool> HandleAnimatorStateAddAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        // tokens: animator state add <asset-path> <name> [--layer <n>]
        if (tokens.Count < 5)
        {
            outputs.Add("[x] usage: animator state add <asset-path> <name> [--layer <n>]");
            return true;
        }

        var assetPath = tokens[3];
        var name = tokens[4];
        var layer = TryGetTokenFlagInt(tokens, "--layer") ?? 0;
        var content = JsonSerializer.Serialize(new { name, layer });

        var response = await RunTrackableProgressAsync(
            session,
            $"adding animator state '{name}'",
            TimeSpan.FromSeconds(8),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("animator-state-add", assetPath, null, content)));

        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("animator state add", response.Message));
            return true;
        }

        outputs.Add($"[+] {response.Message}");
        return true;
    }

    private async Task<bool> HandleAnimatorTransitionAddAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        // tokens: animator transition add <asset-path> <from-state> <to-state> [--layer <n>]
        if (tokens.Count < 6)
        {
            outputs.Add("[x] usage: animator transition add <asset-path> <from-state> <to-state> [--layer <n>]");
            outputs.Add("[x]   use AnyState as <from-state> to create an Any State transition");
            return true;
        }

        var assetPath = tokens[3];
        var fromState = tokens[4];
        var toState = tokens[5];
        var layer = TryGetTokenFlagInt(tokens, "--layer") ?? 0;
        var content = JsonSerializer.Serialize(new { fromState, toState, layer });

        var response = await RunTrackableProgressAsync(
            session,
            $"adding transition '{fromState}' to '{toState}'",
            TimeSpan.FromSeconds(8),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("animator-transition-add", assetPath, null, content)));

        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("animator transition add", response.Message));
            return true;
        }

        outputs.Add($"[+] {response.Message}");
        return true;
    }

    private static int? TryGetTokenFlagInt(IReadOnlyList<string> tokens, string flag)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i].Equals(flag, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(tokens[i + 1], out var value))
            {
                return value;
            }
        }

        return null;
    }
}
