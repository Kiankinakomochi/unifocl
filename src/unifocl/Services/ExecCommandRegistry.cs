using System.Text.Json;

internal sealed class ExecCommandRegistry
{
    private static readonly Dictionary<string, ExecRiskLevel> Operations = new(StringComparer.OrdinalIgnoreCase)
    {
        // asset operations
        ["asset.rename"]        = ExecRiskLevel.DestructiveWrite,
        ["asset.remove"]        = ExecRiskLevel.DestructiveWrite,
        ["asset.create_script"] = ExecRiskLevel.SafeWrite,
        ["asset.create"]        = ExecRiskLevel.SafeWrite,
        // build operations
        ["build.run"]           = ExecRiskLevel.PrivilegedExec,
        ["build.exec"]          = ExecRiskLevel.PrivilegedExec,
        ["build.scenes.set"]    = ExecRiskLevel.SafeWrite,
        // package management
        ["upm.remove"]          = ExecRiskLevel.DestructiveWrite,
        // eval operations
        ["eval.run"]            = ExecRiskLevel.PrivilegedExec,
        // compile operations
        ["compile.request"]     = ExecRiskLevel.SafeWrite,
        ["compile.status"]      = ExecRiskLevel.SafeRead,
        // prefab operations
        ["prefab.create"]       = ExecRiskLevel.SafeWrite,
        ["prefab.apply"]        = ExecRiskLevel.SafeWrite,
        ["prefab.revert"]       = ExecRiskLevel.SafeWrite,
        ["prefab.unpack"]       = ExecRiskLevel.DestructiveWrite,
        ["prefab.variant"]      = ExecRiskLevel.SafeWrite,
        // validate operations (read-only)
        ["validate.scene-list"]      = ExecRiskLevel.SafeRead,
        ["validate.missing-scripts"] = ExecRiskLevel.SafeRead,
        ["validate.packages"]        = ExecRiskLevel.SafeRead,
        ["validate.build-settings"]  = ExecRiskLevel.SafeRead,
        // test operations (subprocess, privileged exec)
        ["test.list"]                = ExecRiskLevel.SafeRead,
        ["test.run"]                 = ExecRiskLevel.PrivilegedExec,
        // read-only queries
        ["hierarchy.snapshot"]  = ExecRiskLevel.SafeRead,
        // meta
        ["session.open"]        = ExecRiskLevel.SafeRead,
        ["session.close"]       = ExecRiskLevel.SafeRead,
        ["session.status"]      = ExecRiskLevel.SafeRead,
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

            case "build.exec":
            {
                var method = GetString(req.Args, "method");
                if (string.IsNullOrWhiteSpace(method))
                {
                    validationError = "build.exec requires args.method";
                    return false;
                }

                var content = $"{{\"method\":\"{method}\"}}";
                dto = new ProjectCommandRequestDto("build-exec", null, null, content, req.RequestId);
                return true;
            }

            case "build.scenes.set":
            {
                var scenesRaw = GetRawString(req.Args, "scenes");
                if (string.IsNullOrWhiteSpace(scenesRaw))
                {
                    validationError = "build.scenes.set requires args.scenes (array of scene paths)";
                    return false;
                }

                var content = $"{{\"scenes\":{scenesRaw}}}";
                dto = new ProjectCommandRequestDto("build-scenes-set", null, null, content, req.RequestId);
                return true;
            }

            case "upm.remove":
            {
                var packageId = GetString(req.Args, "packageId");
                if (string.IsNullOrWhiteSpace(packageId))
                {
                    validationError = "upm.remove requires args.packageId";
                    return false;
                }

                var content = $"{{\"packageId\":\"{packageId}\"}}";
                var base_ = new ProjectCommandRequestDto("upm-remove", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "eval.run":
            {
                var code = GetString(req.Args, "code");
                if (string.IsNullOrWhiteSpace(code))
                {
                    validationError = "eval.run requires args.code";
                    return false;
                }

                var declarations = GetString(req.Args, "declarations") ?? string.Empty;
                var timeoutMs = req.Args.HasValue && req.Args.Value.TryGetProperty("timeoutMs", out var tm)
                    && tm.ValueKind == JsonValueKind.Number ? tm.GetInt32() : 10000;
                var content = JsonSerializer.Serialize(new { code, declarations, timeoutMs });
                var base_ = new ProjectCommandRequestDto("eval-code", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "compile.request":
            {
                dto = new ProjectCommandRequestDto("compile-request", null, null, null, req.RequestId);
                return true;
            }

            case "compile.status":
            {
                dto = new ProjectCommandRequestDto("compile-status", null, null, null, req.RequestId);
                return true;
            }

            case "prefab.create":
            {
                var assetPath = GetString(req.Args, "assetPath");
                var nodeSelector = GetString(req.Args, "nodeSelector");
                if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(nodeSelector))
                {
                    validationError = "prefab.create requires args.assetPath and args.nodeSelector";
                    return false;
                }

                var content = $"{{\"nodeSelector\":\"{nodeSelector}\"}}";
                var base_ = new ProjectCommandRequestDto("prefab-create", assetPath, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "prefab.apply":
            {
                var nodeSelector = GetString(req.Args, "nodeSelector");
                if (string.IsNullOrWhiteSpace(nodeSelector))
                {
                    validationError = "prefab.apply requires args.nodeSelector";
                    return false;
                }

                var content = $"{{\"nodeSelector\":\"{nodeSelector}\"}}";
                var base_ = new ProjectCommandRequestDto("prefab-apply", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "prefab.revert":
            {
                var nodeSelector = GetString(req.Args, "nodeSelector");
                if (string.IsNullOrWhiteSpace(nodeSelector))
                {
                    validationError = "prefab.revert requires args.nodeSelector";
                    return false;
                }

                var content = $"{{\"nodeSelector\":\"{nodeSelector}\"}}";
                var base_ = new ProjectCommandRequestDto("prefab-revert", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "prefab.unpack":
            {
                var nodeSelector = GetString(req.Args, "nodeSelector");
                if (string.IsNullOrWhiteSpace(nodeSelector))
                {
                    validationError = "prefab.unpack requires args.nodeSelector";
                    return false;
                }

                var completely = req.Args is not null
                    && req.Args.Value.TryGetProperty("completely", out var completelyProp)
                    && completelyProp.ValueKind == JsonValueKind.True;
                var content = $"{{\"nodeSelector\":\"{nodeSelector}\",\"completely\":{(completely ? "true" : "false")}}}";
                var base_ = new ProjectCommandRequestDto("prefab-unpack", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "prefab.variant":
            {
                var sourcePath = GetString(req.Args, "sourcePath");
                var newPath = GetString(req.Args, "newPath");
                if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(newPath))
                {
                    validationError = "prefab.variant requires args.sourcePath and args.newPath";
                    return false;
                }

                var base_ = new ProjectCommandRequestDto("prefab-variant", sourcePath, newPath, null, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };

                return true;
            }

            case "validate.scene-list":
            {
                dto = new ProjectCommandRequestDto("validate-scene-list", null, null, null, req.RequestId);
                return true;
            }

            case "validate.missing-scripts":
            {
                dto = new ProjectCommandRequestDto("validate-missing-scripts", null, null, null, req.RequestId);
                return true;
            }

            case "validate.packages":
            {
                dto = new ProjectCommandRequestDto("validate-packages", null, null, null, req.RequestId);
                return true;
            }

            case "validate.build-settings":
            {
                dto = new ProjectCommandRequestDto("validate-build-settings", null, null, null, req.RequestId);
                return true;
            }

            // session.* and hierarchy.snapshot are handled by ExecOperationRouter directly
            case "hierarchy.snapshot":
            case "session.open":
            case "session.close":
            case "session.status":
            // test.* operations are dispatched as subprocesses by ExecOperationRouter directly
            case "test.list":
            case "test.run":
            {
                // These operations do not dispatch through ProjectDaemonBridge
                validationError = $"operation '{req.Operation}' is handled by the router, not the project bridge";
                return false;
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

    /// <summary>Returns the raw JSON text of a property (for arrays/objects passed through as-is).</summary>
    private static string? GetRawString(JsonElement? element, string key)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return element.Value.TryGetProperty(key, out var prop)
            ? prop.GetRawText()
            : null;
    }
}
