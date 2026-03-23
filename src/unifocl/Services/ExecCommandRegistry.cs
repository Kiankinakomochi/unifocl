using System.Text.Json;

internal sealed class ExecCommandRegistry
{
    private static readonly Dictionary<string, ExecRiskLevel> Operations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["asset.rename"]        = ExecRiskLevel.DestructiveWrite,
        ["asset.remove"]        = ExecRiskLevel.DestructiveWrite,
        ["asset.create_script"] = ExecRiskLevel.SafeWrite,
        ["asset.create"]        = ExecRiskLevel.SafeWrite,
        ["build.run"]           = ExecRiskLevel.PrivilegedExec,
        ["approval.confirm"]    = ExecRiskLevel.SafeRead,
    };

    public bool TryGetRisk(string operation, out ExecRiskLevel risk)
        => Operations.TryGetValue(operation, out risk);

    public bool IsKnown(string operation)
        => Operations.ContainsKey(operation);

    /// <summary>
    /// Maps an ExecV2Request to a ProjectCommandRequestDto for dispatch to ProjectDaemonBridge.
    /// Returns false and sets validationError if args are invalid or operation is unsupported.
    /// </summary>
    public bool TryBuildProjectRequest(
        ExecV2Request req,
        bool dryRun,
        out ProjectCommandRequestDto? dto,
        out string? validationError)
    {
        dto = null;
        validationError = null;

        switch (req.Operation.ToLowerInvariant())
        {
            case "asset.rename":
            {
                var assetPath = GetString(req.Args, "assetPath");
                var newAssetPath = GetString(req.Args, "newAssetPath");
                if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(newAssetPath))
                {
                    validationError = "asset.rename requires args.assetPath and args.newAssetPath";
                    return false;
                }

                var base_ = new ProjectCommandRequestDto("rename-asset", assetPath, newAssetPath, null, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "asset.remove":
            {
                var assetPath = GetString(req.Args, "assetPath");
                if (string.IsNullOrWhiteSpace(assetPath))
                {
                    validationError = "asset.remove requires args.assetPath";
                    return false;
                }

                var base_ = new ProjectCommandRequestDto("remove-asset", assetPath, null, null, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "asset.create_script":
            {
                var assetPath = GetString(req.Args, "assetPath");
                var content = GetString(req.Args, "content");
                if (string.IsNullOrWhiteSpace(assetPath))
                {
                    validationError = "asset.create_script requires args.assetPath";
                    return false;
                }

                var base_ = new ProjectCommandRequestDto("mk-script", assetPath, null, content ?? string.Empty, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "asset.create":
            {
                var assetPath = GetString(req.Args, "assetPath");
                var content = GetString(req.Args, "content");
                if (string.IsNullOrWhiteSpace(assetPath))
                {
                    validationError = "asset.create requires args.assetPath";
                    return false;
                }

                var base_ = new ProjectCommandRequestDto("mk-asset", assetPath, null, content ?? string.Empty, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "build.run":
            {
                dto = new ProjectCommandRequestDto("build-run", null, null, null, req.RequestId);
                return true;
            }

            default:
                validationError = $"no handler registered for operation: {req.Operation}";
                return false;
        }
    }

    private static string? GetString(JsonElement? element, string key)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return element.Value.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}
