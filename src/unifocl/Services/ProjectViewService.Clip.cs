using System.Text.Json;

internal sealed partial class ProjectViewService
{
    private async Task<bool> HandleClipCommandAsync(
        CliSessionState session,
        IReadOnlyList<string> tokens,
        List<string> outputs)
    {
        // tokens[0]="clip"
        if (tokens.Count < 2)
        {
            outputs.Add("[x] usage: clip <config|event|curve> <subcommand> [args...]");
            outputs.Add("[x]   clip config <asset-path> [--loop-time <bool>] [--loop-pose <bool>]");
            outputs.Add("[x]   clip event add <asset-path> <time> <function-name> [--string <v>|--float <v>|--int <v>]");
            outputs.Add("[x]   clip event clear <asset-path>");
            outputs.Add("[x]   clip curve clear <asset-path>");
            return true;
        }

        var domain = tokens[1];

        if (domain.Equals("config", StringComparison.OrdinalIgnoreCase))
            return await HandleClipConfigAsync(tokens, session, outputs);

        if (domain.Equals("event", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Count < 3)
            {
                outputs.Add("[x] usage: clip event <add|clear> [args...]");
                return true;
            }

            var sub = tokens[2];
            if (sub.Equals("add", StringComparison.OrdinalIgnoreCase))
                return await HandleClipEventAddAsync(tokens, session, outputs);
            if (sub.Equals("clear", StringComparison.OrdinalIgnoreCase))
                return await HandleClipEventClearAsync(tokens, session, outputs);

            outputs.Add($"[x] unknown clip event subcommand: {sub}");
            return true;
        }

        if (domain.Equals("curve", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Count < 3)
            {
                outputs.Add("[x] usage: clip curve clear <asset-path>");
                return true;
            }

            var sub = tokens[2];
            if (sub.Equals("clear", StringComparison.OrdinalIgnoreCase))
                return await HandleClipCurveClearAsync(tokens, session, outputs);

            outputs.Add($"[x] unknown clip curve subcommand: {sub}");
            return true;
        }

        outputs.Add($"[x] unknown clip subcommand: {domain}");
        return true;
    }

    private async Task<bool> HandleClipConfigAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        // tokens: clip config <asset-path> [--loop-time <bool>] [--loop-pose <bool>]
        if (tokens.Count < 3)
        {
            outputs.Add("[x] usage: clip config <asset-path> [--loop-time <bool>] [--loop-pose <bool>]");
            return true;
        }

        var assetPath = tokens[2];
        var loopTime = TryGetTokenFlagBool(tokens, "--loop-time");
        var loopPose = TryGetTokenFlagBool(tokens, "--loop-pose");

        if (loopTime == null && loopPose == null)
        {
            outputs.Add("[x] clip config requires at least one of --loop-time or --loop-pose");
            return true;
        }

        var content = JsonSerializer.Serialize(new
        {
            loopTime = loopTime ?? false,
            loopPose = loopPose ?? false,
            setLoopTime = loopTime != null,
            setLoopPose = loopPose != null
        });

        var response = await RunTrackableProgressAsync(
            session,
            "updating clip loop settings",
            TimeSpan.FromSeconds(8),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("clip-config", assetPath, null, content)));

        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("clip config", response.Message));
            return true;
        }

        outputs.Add($"[+] {response.Message}");
        return true;
    }

    private async Task<bool> HandleClipEventAddAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        // tokens: clip event add <asset-path> <time> <function-name> [--string <v>|--float <v>|--int <v>]
        if (tokens.Count < 6)
        {
            outputs.Add("[x] usage: clip event add <asset-path> <time> <function-name> [--string <v>|--float <v>|--int <v>]");
            return true;
        }

        var assetPath = tokens[3];
        if (!float.TryParse(tokens[4], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var time))
        {
            outputs.Add($"[x] clip event add: <time> must be a number, got '{tokens[4]}'");
            return true;
        }

        var functionName = tokens[5];
        var stringParam = TryGetTokenFlagString(tokens, "--string");
        var floatParamStr = TryGetTokenFlagString(tokens, "--float");
        var intParamStr = TryGetTokenFlagString(tokens, "--int");
        float? floatParam = floatParamStr != null
            && float.TryParse(floatParamStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var fp)
            ? fp : null;
        int? intParam = intParamStr != null && int.TryParse(intParamStr, out var ip) ? ip : null;

        var content = JsonSerializer.Serialize(new
        {
            time,
            functionName,
            stringParam = stringParam ?? string.Empty,
            floatParam = floatParam ?? 0f,
            intParam = intParam ?? 0,
            hasStringParam = stringParam != null,
            hasFloatParam = floatParam != null,
            hasIntParam = intParam != null
        });

        var response = await RunTrackableProgressAsync(
            session,
            $"adding clip event '{functionName}' at t={time}",
            TimeSpan.FromSeconds(8),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("clip-event-add", assetPath, null, content)));

        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("clip event add", response.Message));
            return true;
        }

        outputs.Add($"[+] {response.Message}");
        return true;
    }

    private async Task<bool> HandleClipEventClearAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        // tokens: clip event clear <asset-path>
        if (tokens.Count < 4)
        {
            outputs.Add("[x] usage: clip event clear <asset-path>");
            return true;
        }

        var assetPath = tokens[3];

        var response = await RunTrackableProgressAsync(
            session,
            "clearing clip events",
            TimeSpan.FromSeconds(8),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("clip-event-clear", assetPath, null, null)));

        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("clip event clear", response.Message));
            return true;
        }

        outputs.Add($"[+] {response.Message}");
        return true;
    }

    private async Task<bool> HandleClipCurveClearAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        // tokens: clip curve clear <asset-path>
        if (tokens.Count < 4)
        {
            outputs.Add("[x] usage: clip curve clear <asset-path>");
            return true;
        }

        var assetPath = tokens[3];

        var response = await RunTrackableProgressAsync(
            session,
            "clearing clip curves",
            TimeSpan.FromSeconds(8),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("clip-curve-clear", assetPath, null, null)));

        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("clip curve clear", response.Message));
            return true;
        }

        outputs.Add($"[+] {response.Message}");
        return true;
    }

    private static bool? TryGetTokenFlagBool(IReadOnlyList<string> tokens, string flag)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                var val = tokens[i + 1];
                if (val.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (val.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            }
        }

        return null;
    }

    private static string? TryGetTokenFlagString(IReadOnlyList<string> tokens, string flag)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return tokens[i + 1];
        }

        return null;
    }
}
