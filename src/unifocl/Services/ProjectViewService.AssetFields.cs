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
            outputs.Add("[x] usage: asset get <asset-path> [<field>]");
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
            outputs.Add("[x] usage: asset set <asset-path> <field> <value>");
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
