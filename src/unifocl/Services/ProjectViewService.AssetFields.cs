using System.Text.Json;

internal sealed partial class ProjectViewService
{
    private static readonly JsonSerializerOptions AssetFieldsJsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    // ── asset get ──────────────────────────────────────────────────────────

    private async Task<bool> HandleAssetGetAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        // tokens: asset get <path> [<field>]
        if (tokens.Count < 3)
        {
            outputs.Add("[x] usage: asset get <asset-path> [<field>]  (quote paths with spaces: asset get \"Assets/My Folder/Foo.asset\")");
            return true;
        }

        var assetPath = tokens[2];
        var field = tokens.Count >= 4 ? tokens[3] : null;

        var content = field is not null
            ? JsonSerializer.Serialize(new { field })
            : null;

        var response = await RunTrackableProgressAsync(
            session,
            $"reading fields from '{assetPath}'",
            TimeSpan.FromSeconds(10),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("asset-get", assetPath, null, content)));

        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("asset get", response.Message));
            return true;
        }

        outputs.Add($"[+] {response.Message}");

        if (!string.IsNullOrWhiteSpace(response.Content))
        {
            AssetGetResult? result;
            try
            {
                result = JsonSerializer.Deserialize<AssetGetResult>(response.Content, AssetFieldsJsonOpts);
            }
            catch
            {
                result = null;
            }

            if (result?.Fields is { Length: > 0 } fields)
            {
                foreach (var f in fields)
                {
                    outputs.Add($"  [grey]{f.Name}[/]  [{CliTheme.TextMuted}]{f.Type}[/]  {EscapeMarkup(f.Value)}");
                }
            }
        }

        return true;
    }

    // ── asset set ──────────────────────────────────────────────────────────

    private async Task<bool> HandleAssetSetAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        // tokens: asset set <path> <field> <value>
        if (tokens.Count < 5)
        {
            outputs.Add("[x] usage: asset set <asset-path> <field> <value>  (quote paths with spaces: asset set \"Assets/My Folder/Foo.asset\" fieldName value)");
            return true;
        }

        var assetPath = tokens[2];
        var field = tokens[3];
        var value = string.Join(' ', tokens.Skip(4));
        var content = JsonSerializer.Serialize(new { field, value });

        var response = await RunTrackableProgressAsync(
            session,
            $"setting '{field}' on '{assetPath}'",
            TimeSpan.FromSeconds(10),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("asset-set", assetPath, null, content)));

        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("asset set", response.Message));
            return true;
        }

        outputs.Add($"[+] {response.Message}");
        return true;
    }

    // ── asset rename ──────────────────────────────────────────────────────

    private async Task<bool> HandleAssetRenameByPathAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        // tokens: asset rename <source-path> <new-name>
        if (tokens.Count < 4)
        {
            outputs.Add("[x] usage: asset rename <asset-path> <new-name>  (quote paths with spaces: asset rename \"Assets/My Folder/Foo.asset\" Bar)");
            return true;
        }

        var sourceRelativePath = tokens[2];
        var newName = tokens[3];

        var isDirectory = !Path.HasExtension(sourceRelativePath)
                          && !string.IsNullOrWhiteSpace(session.CurrentProjectPath)
                          && Directory.Exists(Path.Combine(session.CurrentProjectPath, sourceRelativePath));
        var destinationRelative = ProjectViewServiceUtils.ComputeRenameDestinationPath(sourceRelativePath, isDirectory, newName);

        var state = session.ProjectView;
        state.DbState = ProjectDbState.LockedImporting;
        try
        {
            var response = await RunTrackableProgressAsync(
                session,
                $"renaming '{Path.GetFileName(sourceRelativePath)}' → '{newName}'",
                TimeSpan.FromSeconds(10),
                () => ExecuteProjectCommandAsync(
                    session,
                    BuildVcsAwareProjectRequest(
                        session,
                        new ProjectCommandRequestDto("rename-asset", sourceRelativePath, destinationRelative, null),
                        sourceRelativePath,
                        sourceRelativePath + ".meta",
                        destinationRelative,
                        destinationRelative + ".meta")));

            if (!response.Ok)
            {
                outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("asset rename", response.Message));
                return true;
            }

            if (response.Kind?.Equals("dry-run", StringComparison.OrdinalIgnoreCase) == true
                && TryAppendDryRunDiff(outputs, response.Content))
            {
                return true;
            }

            outputs.Add("[=] rename complete. .meta file updated successfully.");
            if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                ProjectViewTreeUtils.RefreshTree(session.CurrentProjectPath, state);
            }

            await SyncAssetIndexAsync(session);
            return true;
        }
        finally
        {
            state.DbState = ProjectDbState.IdleSafe;
        }
    }

    // ── asset remove ──────────────────────────────────────────────────────

    private async Task<bool> HandleAssetRemoveByPathAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        // tokens: asset remove <path>
        if (tokens.Count < 3)
        {
            outputs.Add("[x] usage: asset remove <asset-path>  (quote paths with spaces: asset remove \"Assets/My Folder/Foo.asset\")");
            return true;
        }

        var sourcePath = tokens[2];
        var state = session.ProjectView;
        state.DbState = ProjectDbState.LockedImporting;
        try
        {
            var response = await RunTrackableProgressAsync(
                session,
                $"removing '{Path.GetFileName(sourcePath)}'",
                TimeSpan.FromSeconds(6),
                () => ExecuteProjectCommandAsync(
                    session,
                    BuildVcsAwareProjectRequest(
                        session,
                        new ProjectCommandRequestDto("remove-asset", sourcePath, null, null),
                        sourcePath,
                        sourcePath + ".meta")));

            if (!response.Ok)
            {
                outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("asset remove", response.Message));
                return true;
            }

            if (response.Kind?.Equals("dry-run", StringComparison.OrdinalIgnoreCase) == true
                && TryAppendDryRunDiff(outputs, response.Content))
            {
                return true;
            }

            outputs.Add($"[=] removed: {sourcePath}");
            if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                ProjectViewTreeUtils.RefreshTree(session.CurrentProjectPath, state);
            }

            await SyncAssetIndexAsync(session);
            return true;
        }
        finally
        {
            state.DbState = ProjectDbState.IdleSafe;
        }
    }

    // ── asset refresh ───────────────────────────────────────────────────────

    private async Task<bool> HandleAssetRefreshAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        // tokens: asset refresh [<path>]
        var assetPath = tokens.Count >= 3 ? tokens[2] : null;

        var response = await RunTrackableProgressAsync(
            session,
            assetPath is not null ? $"reimporting '{assetPath}'" : "refreshing asset database",
            TimeSpan.FromSeconds(30),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("asset-refresh", assetPath, null, null)));

        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("asset refresh", response.Message));
            return true;
        }

        outputs.Add($"[+] {response.Message}");
        return true;
    }

    // ── DTOs ───────────────────────────────────────────────────────────────

    private sealed class AssetGetResult
    {
        public string AssetPath { get; set; } = string.Empty;
        public bool IsImporter { get; set; }
        public AssetFieldRow[] Fields { get; set; } = [];
    }

    private sealed class AssetFieldRow
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private static string EscapeMarkup(string s)
    {
        return Spectre.Console.Markup.Escape(s ?? string.Empty);
    }
}
