using System.Text.Json;

internal sealed partial class ProjectViewService
{
    private async Task<bool> HandlePrefabCommandAsync(
        CliSessionState session,
        IReadOnlyList<string> tokens,
        List<string> outputs,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime)
    {
        _ = daemonControlService;
        _ = daemonRuntime;

        if (tokens.Count < 2)
        {
            outputs.Add("[x] usage: prefab <create|apply|revert|unpack|variant> [args...]");
            outputs.Add("[x]   prefab create <idx|name> <asset-path>");
            outputs.Add("[x]   prefab apply <idx>");
            outputs.Add("[x]   prefab revert <idx>");
            outputs.Add("[x]   prefab unpack <idx> [--completely]");
            outputs.Add("[x]   prefab variant <source-path> <new-path>");
            return true;
        }

        var subcommand = tokens[1];

        if (subcommand.Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            return await HandlePrefabCreateAsync(tokens, session, outputs);
        }

        if (subcommand.Equals("apply", StringComparison.OrdinalIgnoreCase))
        {
            return await HandlePrefabApplyAsync(tokens, session, outputs);
        }

        if (subcommand.Equals("revert", StringComparison.OrdinalIgnoreCase))
        {
            return await HandlePrefabRevertAsync(tokens, session, outputs);
        }

        if (subcommand.Equals("unpack", StringComparison.OrdinalIgnoreCase))
        {
            return await HandlePrefabUnpackAsync(tokens, session, outputs);
        }

        if (subcommand.Equals("variant", StringComparison.OrdinalIgnoreCase))
        {
            return await HandlePrefabVariantAsync(tokens, session, outputs);
        }

        outputs.Add($"[x] unknown prefab subcommand: {subcommand}");
        outputs.Add("[x] usage: prefab <create|apply|revert|unpack|variant> [args...]");
        return true;
    }

    private async Task<bool> HandlePrefabCreateAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        if (tokens.Count < 4)
        {
            outputs.Add("[x] usage: prefab create <idx|name> <asset-path>");
            return true;
        }

        var nodeSelector = tokens[2];
        var assetPath = tokens[3];

        if (!assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            assetPath += ".prefab";
        }

        var content = JsonSerializer.Serialize(
            new { nodeSelector },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var state = session.ProjectView;
        state.DbState = ProjectDbState.LockedImporting;
        try
        {
            var response = await RunTrackableProgressAsync(
                session,
                "creating prefab from scene object",
                TimeSpan.FromSeconds(12),
                () => ExecuteProjectCommandAsync(
                    session,
                    new ProjectCommandRequestDto("prefab-create", assetPath, null, content)));

            if (!response.Ok)
            {
                outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("prefab create", response.Message));
                return true;
            }

            if (response.Kind?.Equals("dry-run", StringComparison.OrdinalIgnoreCase) == true
                && TryAppendDryRunDiff(outputs, response.Content))
            {
                return true;
            }

            outputs.Add($"[+] created prefab: {assetPath}");
            await SyncAssetIndexAsync(session);
            if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                ProjectViewTreeUtils.RefreshTree(session.CurrentProjectPath, state);
            }

            return true;
        }
        finally
        {
            state.DbState = ProjectDbState.IdleSafe;
        }
    }

    private async Task<bool> HandlePrefabApplyAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        if (tokens.Count < 3)
        {
            outputs.Add("[x] usage: prefab apply <idx>");
            return true;
        }

        var nodeSelector = tokens[2];
        var content = JsonSerializer.Serialize(
            new { nodeSelector },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var response = await RunTrackableProgressAsync(
            session,
            "applying prefab overrides",
            TimeSpan.FromSeconds(12),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("prefab-apply", null, null, content)));

        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("prefab apply", response.Message));
            return true;
        }

        if (response.Kind?.Equals("dry-run", StringComparison.OrdinalIgnoreCase) == true
            && TryAppendDryRunDiff(outputs, response.Content))
        {
            return true;
        }

        outputs.Add("[+] applied prefab overrides to source asset");
        return true;
    }

    private async Task<bool> HandlePrefabRevertAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        if (tokens.Count < 3)
        {
            outputs.Add("[x] usage: prefab revert <idx>");
            return true;
        }

        var nodeSelector = tokens[2];
        var content = JsonSerializer.Serialize(
            new { nodeSelector },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var response = await RunTrackableProgressAsync(
            session,
            "reverting prefab overrides",
            TimeSpan.FromSeconds(12),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("prefab-revert", null, null, content)));

        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("prefab revert", response.Message));
            return true;
        }

        if (response.Kind?.Equals("dry-run", StringComparison.OrdinalIgnoreCase) == true
            && TryAppendDryRunDiff(outputs, response.Content))
        {
            return true;
        }

        outputs.Add("[+] reverted prefab instance to source asset state");
        return true;
    }

    private async Task<bool> HandlePrefabUnpackAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        if (tokens.Count < 3)
        {
            outputs.Add("[x] usage: prefab unpack <idx> [--completely]");
            return true;
        }

        var nodeSelector = tokens[2];
        var completely = tokens.Any(t => t.Equals("--completely", StringComparison.OrdinalIgnoreCase));

        var content = JsonSerializer.Serialize(
            new { nodeSelector, completely },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var response = await RunTrackableProgressAsync(
            session,
            "unpacking prefab instance",
            TimeSpan.FromSeconds(12),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("prefab-unpack", null, null, content)));

        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("prefab unpack", response.Message));
            return true;
        }

        if (response.Kind?.Equals("dry-run", StringComparison.OrdinalIgnoreCase) == true
            && TryAppendDryRunDiff(outputs, response.Content))
        {
            return true;
        }

        outputs.Add(completely
            ? "[+] completely unpacked prefab instance"
            : "[+] unpacked outermost prefab root");
        return true;
    }

    private async Task<bool> HandlePrefabVariantAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        if (tokens.Count < 4)
        {
            outputs.Add("[x] usage: prefab variant <source-path> <new-path>");
            return true;
        }

        var sourcePath = tokens[2];
        var newPath = tokens[3];

        if (!newPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            newPath += ".prefab";
        }

        var state = session.ProjectView;
        state.DbState = ProjectDbState.LockedImporting;
        try
        {
            var response = await RunTrackableProgressAsync(
                session,
                "creating prefab variant",
                TimeSpan.FromSeconds(12),
                () => ExecuteProjectCommandAsync(
                    session,
                    new ProjectCommandRequestDto("prefab-variant", sourcePath, newPath, null)));

            if (!response.Ok)
            {
                outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("prefab variant", response.Message));
                return true;
            }

            if (response.Kind?.Equals("dry-run", StringComparison.OrdinalIgnoreCase) == true
                && TryAppendDryRunDiff(outputs, response.Content))
            {
                return true;
            }

            outputs.Add($"[+] created prefab variant: {newPath}");
            await SyncAssetIndexAsync(session);
            if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                ProjectViewTreeUtils.RefreshTree(session.CurrentProjectPath, state);
            }

            return true;
        }
        finally
        {
            state.DbState = ProjectDbState.IdleSafe;
        }
    }
}
