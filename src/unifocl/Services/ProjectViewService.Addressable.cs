using System.Text.Json;
using Spectre.Console;

internal sealed partial class ProjectViewService
{
    private static readonly JsonSerializerOptions AddressableJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private async Task<bool> HandleAddressableCommandAsync(
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
            AppendAddressableUsage(outputs);
            return true;
        }

        var head = tokens[1].ToLowerInvariant();
        return head switch
        {
            "init" => await ExecuteAddressableOperationAsync(
                session,
                outputs,
                "init",
                "initializing Addressables settings",
                "addressables initialized",
                new AddressableCommandPayload("init")),
            "profile" => await HandleAddressableProfileSubcommandAsync(session, tokens, outputs),
            "group" => await HandleAddressableGroupSubcommandAsync(session, tokens, outputs),
            "entry" => await HandleAddressableEntrySubcommandAsync(session, tokens, outputs),
            "bulk" => await HandleAddressableBulkSubcommandAsync(session, tokens, outputs),
            "analyze" => await HandleAddressableAnalyzeSubcommandAsync(session, tokens, outputs),
            _ => HandleAddressableUnknownSubcommand(head, outputs)
        };
    }

    private async Task<bool> HandleAddressableProfileSubcommandAsync(
        CliSessionState session,
        IReadOnlyList<string> tokens,
        List<string> outputs)
    {
        if (tokens.Count < 3)
        {
            outputs.Add("[x] usage: addressable profile <list|set>");
            return true;
        }

        var action = tokens[2].ToLowerInvariant();
        if (action == "list")
        {
            return await ExecuteAddressableOperationAsync(
                session,
                outputs,
                "profile list",
                "reading Addressables profiles",
                null,
                new AddressableCommandPayload("profile-list"));
        }

        if (action == "set")
        {
            if (tokens.Count < 4)
            {
                outputs.Add("[x] usage: addressable profile set <name>");
                return true;
            }

            var profileName = string.Join(' ', tokens.Skip(3)).Trim();
            if (string.IsNullOrWhiteSpace(profileName))
            {
                outputs.Add("[x] usage: addressable profile set <name>");
                return true;
            }

            return await ExecuteAddressableOperationAsync(
                session,
                outputs,
                "profile set",
                "switching active Addressables profile",
                $"active profile set: {profileName}",
                new AddressableCommandPayload("profile-set", Name: profileName));
        }

        outputs.Add($"[x] unknown addressable profile subcommand: {action}");
        outputs.Add("supported: addressable profile list | addressable profile set <name>");
        return true;
    }

    private async Task<bool> HandleAddressableGroupSubcommandAsync(
        CliSessionState session,
        IReadOnlyList<string> tokens,
        List<string> outputs)
    {
        if (tokens.Count < 3)
        {
            outputs.Add("[x] usage: addressable group <list|create|remove> ...");
            return true;
        }

        var action = tokens[2].ToLowerInvariant();
        if (action == "list")
        {
            return await ExecuteAddressableOperationAsync(
                session,
                outputs,
                "group list",
                "reading Addressables groups",
                null,
                new AddressableCommandPayload("group-list"));
        }

        if (action == "create")
        {
            if (tokens.Count < 4)
            {
                outputs.Add("[x] usage: addressable group create <name> [--default]");
                return true;
            }

            var setDefault = false;
            var parts = new List<string>();
            foreach (var token in tokens.Skip(3))
            {
                if (token.Equals("--default", StringComparison.OrdinalIgnoreCase))
                {
                    setDefault = true;
                    continue;
                }

                parts.Add(token);
            }

            var groupName = string.Join(' ', parts).Trim();
            if (string.IsNullOrWhiteSpace(groupName))
            {
                outputs.Add("[x] usage: addressable group create <name> [--default]");
                return true;
            }

            return await ExecuteAddressableOperationAsync(
                session,
                outputs,
                "group create",
                "creating Addressables group",
                $"created group: {groupName}",
                new AddressableCommandPayload("group-create", Name: groupName, SetDefault: setDefault));
        }

        if (action == "remove")
        {
            if (tokens.Count < 4)
            {
                outputs.Add("[x] usage: addressable group remove <name>");
                return true;
            }

            var groupName = string.Join(' ', tokens.Skip(3)).Trim();
            if (string.IsNullOrWhiteSpace(groupName))
            {
                outputs.Add("[x] usage: addressable group remove <name>");
                return true;
            }

            return await ExecuteAddressableOperationAsync(
                session,
                outputs,
                "group remove",
                "removing Addressables group",
                $"removed group: {groupName}",
                new AddressableCommandPayload("group-remove", Name: groupName));
        }

        outputs.Add($"[x] unknown addressable group subcommand: {action}");
        outputs.Add("supported: addressable group list | addressable group create <name> [--default] | addressable group remove <name>");
        return true;
    }

    private async Task<bool> HandleAddressableEntrySubcommandAsync(
        CliSessionState session,
        IReadOnlyList<string> tokens,
        List<string> outputs)
    {
        if (tokens.Count < 3)
        {
            outputs.Add("[x] usage: addressable entry <add|remove|rename|label> ...");
            return true;
        }

        var action = tokens[2].ToLowerInvariant();
        if (action == "add")
        {
            if (tokens.Count < 5)
            {
                outputs.Add("[x] usage: addressable entry add <asset-path> <group-name>");
                return true;
            }

            var assetPath = tokens[3];
            var groupName = string.Join(' ', tokens.Skip(4)).Trim();
            if (string.IsNullOrWhiteSpace(groupName))
            {
                outputs.Add("[x] usage: addressable entry add <asset-path> <group-name>");
                return true;
            }

            return await ExecuteAddressableOperationAsync(
                session,
                outputs,
                "entry add",
                "adding Addressables entry",
                $"addressable entry added: {assetPath}",
                new AddressableCommandPayload("entry-add", AssetPath: assetPath, GroupName: groupName),
                candidatePaths: [assetPath]);
        }

        if (action == "remove")
        {
            if (tokens.Count < 4)
            {
                outputs.Add("[x] usage: addressable entry remove <asset-path>");
                return true;
            }

            var assetPath = tokens[3];
            return await ExecuteAddressableOperationAsync(
                session,
                outputs,
                "entry remove",
                "removing Addressables entry",
                $"addressable entry removed: {assetPath}",
                new AddressableCommandPayload("entry-remove", AssetPath: assetPath),
                candidatePaths: [assetPath]);
        }

        if (action == "rename")
        {
            if (tokens.Count < 5)
            {
                outputs.Add("[x] usage: addressable entry rename <asset-path> <new-address>");
                return true;
            }

            var assetPath = tokens[3];
            var newAddress = string.Join(' ', tokens.Skip(4)).Trim();
            if (string.IsNullOrWhiteSpace(newAddress))
            {
                outputs.Add("[x] usage: addressable entry rename <asset-path> <new-address>");
                return true;
            }

            return await ExecuteAddressableOperationAsync(
                session,
                outputs,
                "entry rename",
                "renaming Addressables address key",
                $"addressable key updated: {assetPath} -> {newAddress}",
                new AddressableCommandPayload("entry-rename", AssetPath: assetPath, Address: newAddress),
                candidatePaths: [assetPath]);
        }

        if (action == "label")
        {
            if (tokens.Count < 5)
            {
                outputs.Add("[x] usage: addressable entry label <asset-path> <label> [--remove]");
                return true;
            }

            var removeLabel = false;
            var parts = new List<string>();
            foreach (var token in tokens.Skip(4))
            {
                if (token.Equals("--remove", StringComparison.OrdinalIgnoreCase))
                {
                    removeLabel = true;
                    continue;
                }

                parts.Add(token);
            }

            var assetPath = tokens[3];
            var label = string.Join(' ', parts).Trim();
            if (string.IsNullOrWhiteSpace(label))
            {
                outputs.Add("[x] usage: addressable entry label <asset-path> <label> [--remove]");
                return true;
            }

            return await ExecuteAddressableOperationAsync(
                session,
                outputs,
                "entry label",
                removeLabel ? "removing Addressables label" : "setting Addressables label",
                removeLabel
                    ? $"removed label '{label}' from {assetPath}"
                    : $"added label '{label}' to {assetPath}",
                new AddressableCommandPayload("entry-label", AssetPath: assetPath, Label: label, Remove: removeLabel),
                candidatePaths: [assetPath]);
        }

        outputs.Add($"[x] unknown addressable entry subcommand: {action}");
        outputs.Add("supported: addressable entry add|remove|rename|label");
        return true;
    }

    private async Task<bool> HandleAddressableAnalyzeSubcommandAsync(
        CliSessionState session,
        IReadOnlyList<string> tokens,
        List<string> outputs)
    {
        var duplicateOnly = tokens.Skip(2).Any(token => token.Equals("--duplicate", StringComparison.OrdinalIgnoreCase));
        return await ExecuteAddressableOperationAsync(
            session,
            outputs,
            "analyze",
            duplicateOnly
                ? "running Addressables duplicate dependency analysis"
                : "analyzing Addressables catalog",
            null,
            new AddressableCommandPayload("analyze", Duplicate: duplicateOnly));
    }

    private async Task<bool> HandleAddressableBulkSubcommandAsync(
        CliSessionState session,
        IReadOnlyList<string> tokens,
        List<string> outputs)
    {
        if (tokens.Count < 3)
        {
            outputs.Add("[x] usage: addressable bulk <add|label> ...");
            return true;
        }

        var action = tokens[2].ToLowerInvariant();
        if (action == "add")
        {
            var folder = string.Empty;
            var groupName = string.Empty;
            var typeName = string.Empty;
            for (var i = 3; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Equals("--folder", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
                {
                    folder = tokens[++i];
                    continue;
                }

                if (token.Equals("--group", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
                {
                    groupName = tokens[++i];
                    continue;
                }

                if (token.Equals("--type", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
                {
                    typeName = tokens[++i];
                    continue;
                }

                outputs.Add($"[x] unsupported option: {token}");
                outputs.Add("[x] usage: addressable bulk add --folder <path> --group <name> [--type <T>]");
                return true;
            }

            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(groupName))
            {
                outputs.Add("[x] usage: addressable bulk add --folder <path> --group <name> [--type <T>]");
                return true;
            }

            return await ExecuteAddressableOperationAsync(
                session,
                outputs,
                "bulk add",
                "bulk adding Addressables entries",
                null,
                new AddressableCommandPayload(
                    "bulk-add",
                    Folder: folder,
                    GroupName: groupName,
                    Type: typeName),
                candidatePaths: [folder]);
        }

        if (action == "label")
        {
            var folder = string.Empty;
            var label = string.Empty;
            var typeName = string.Empty;
            var remove = false;
            for (var i = 3; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Equals("--folder", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
                {
                    folder = tokens[++i];
                    continue;
                }

                if (token.Equals("--label", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
                {
                    label = tokens[++i];
                    continue;
                }

                if (token.Equals("--type", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
                {
                    typeName = tokens[++i];
                    continue;
                }

                if (token.Equals("--remove", StringComparison.OrdinalIgnoreCase))
                {
                    remove = true;
                    continue;
                }

                outputs.Add($"[x] unsupported option: {token}");
                outputs.Add("[x] usage: addressable bulk label --folder <path> --label <name> [--type <T>] [--remove]");
                return true;
            }

            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(label))
            {
                outputs.Add("[x] usage: addressable bulk label --folder <path> --label <name> [--type <T>] [--remove]");
                return true;
            }

            return await ExecuteAddressableOperationAsync(
                session,
                outputs,
                "bulk label",
                remove ? "bulk removing Addressables labels" : "bulk applying Addressables labels",
                null,
                new AddressableCommandPayload(
                    "bulk-label",
                    Folder: folder,
                    Label: label,
                    Type: typeName,
                    Remove: remove),
                candidatePaths: [folder]);
        }

        outputs.Add($"[x] unknown addressable bulk subcommand: {action}");
        outputs.Add("supported: addressable bulk add | addressable bulk label");
        return true;
    }

    private async Task<bool> ExecuteAddressableOperationAsync(
        CliSessionState session,
        List<string> outputs,
        string label,
        string progressLabel,
        string? successLine,
        AddressableCommandPayload payload,
        params string?[] candidatePaths)
    {
        var content = JsonSerializer.Serialize(payload, AddressableJsonOptions);
        var request = new ProjectCommandRequestDto("addressables-cli", null, null, content);
        if (IsMutationAddressableOperation(payload.Operation))
        {
            request = BuildVcsAwareProjectRequest(session, request, candidatePaths);
        }

        var response = await RunTrackableProgressAsync(
            session,
            progressLabel,
            TimeSpan.FromSeconds(15),
            () => ExecuteProjectCommandAsync(session, request));
        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure($"addressable {label}", response.Message));
            return true;
        }

        if (response.Kind?.Equals("dry-run", StringComparison.OrdinalIgnoreCase) == true
            && TryAppendDryRunDiff(outputs, response.Content))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(successLine))
        {
            outputs.Add($"[+] {Markup.Escape(successLine)}");
        }
        else if (!string.IsNullOrWhiteSpace(response.Message))
        {
            outputs.Add($"[i] {Markup.Escape(response.Message)}");
        }

        AppendAddressableContent(outputs, response.Content);
        return true;
    }

    private static bool HandleAddressableUnknownSubcommand(string head, List<string> outputs)
    {
        outputs.Add($"[x] unknown addressable subcommand: {head}");
        AppendAddressableUsage(outputs);
        return true;
    }

    private static void AppendAddressableUsage(List<string> outputs)
    {
        outputs.Add("[x] usage: addressable init");
        outputs.Add("[x] usage: addressable profile list");
        outputs.Add("[x] usage: addressable profile set <name>");
        outputs.Add("[x] usage: addressable group list");
        outputs.Add("[x] usage: addressable group create <name> [--default]");
        outputs.Add("[x] usage: addressable group remove <name>");
        outputs.Add("[x] usage: addressable entry add <asset-path> <group-name>");
        outputs.Add("[x] usage: addressable entry remove <asset-path>");
        outputs.Add("[x] usage: addressable entry rename <asset-path> <new-address>");
        outputs.Add("[x] usage: addressable entry label <asset-path> <label> [--remove]");
        outputs.Add("[x] usage: addressable bulk add --folder <path> --group <name> [--type <T>]");
        outputs.Add("[x] usage: addressable bulk label --folder <path> --label <name> [--type <T>] [--remove]");
        outputs.Add("[x] usage: addressable analyze [--duplicate]");
    }

    private static void AppendAddressableContent(List<string> outputs, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        foreach (var line in content
                     .Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split('\n'))
        {
            outputs.Add(Markup.Escape(line));
        }
    }

    internal static bool IsAddressableMutationCommand(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 2 || !tokens[0].Equals("addressable", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var first = tokens[1];
        if (first.Equals("init", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (first.Equals("analyze", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (first.Equals("profile", StringComparison.OrdinalIgnoreCase) && tokens.Count >= 3)
        {
            return tokens[2].Equals("set", StringComparison.OrdinalIgnoreCase);
        }

        if (first.Equals("group", StringComparison.OrdinalIgnoreCase) && tokens.Count >= 3)
        {
            return tokens[2].Equals("create", StringComparison.OrdinalIgnoreCase)
                   || tokens[2].Equals("remove", StringComparison.OrdinalIgnoreCase);
        }

        if (first.Equals("entry", StringComparison.OrdinalIgnoreCase) && tokens.Count >= 3)
        {
            return true;
        }

        if (first.Equals("bulk", StringComparison.OrdinalIgnoreCase) && tokens.Count >= 3)
        {
            return tokens[2].Equals("add", StringComparison.OrdinalIgnoreCase)
                   || tokens[2].Equals("label", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsMutationAddressableOperation(string operation)
    {
        return operation is "init"
            or "profile-set"
            or "group-create"
            or "group-remove"
            or "entry-add"
            or "entry-remove"
            or "entry-rename"
            or "entry-label"
            or "bulk-add"
            or "bulk-label";
    }

    private sealed record AddressableCommandPayload(
        string Operation,
        string? Name = null,
        string? AssetPath = null,
        string? GroupName = null,
        string? Address = null,
        string? Label = null,
        string? Folder = null,
        string? Type = null,
        bool SetDefault = false,
        bool Remove = false,
        bool Duplicate = false);
}
