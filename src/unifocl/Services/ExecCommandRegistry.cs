using System.Text.Json;

internal sealed class ExecCommandRegistry
{
    private static readonly Dictionary<string, ExecRiskLevel> Operations = new(StringComparer.OrdinalIgnoreCase)
    {
        // animator operations
        ["animator.param.add"]        = ExecRiskLevel.SafeWrite,
        ["animator.param.remove"]     = ExecRiskLevel.DestructiveWrite,
        ["animator.state.add"]        = ExecRiskLevel.SafeWrite,
        ["animator.transition.add"]   = ExecRiskLevel.SafeWrite,
        // clip operations
        ["clip.config"]               = ExecRiskLevel.SafeWrite,
        ["clip.event.add"]            = ExecRiskLevel.SafeWrite,
        ["clip.event.clear"]          = ExecRiskLevel.DestructiveWrite,
        ["clip.curve.clear"]          = ExecRiskLevel.DestructiveWrite,
        // asset operations
        ["asset.rename"]        = ExecRiskLevel.DestructiveWrite,
        ["asset.remove"]        = ExecRiskLevel.DestructiveWrite,
        ["asset.create_script"] = ExecRiskLevel.SafeWrite,
        ["asset.create"]        = ExecRiskLevel.SafeWrite,
        ["asset.describe"]      = ExecRiskLevel.SafeRead,
        // build operations
        ["build.run"]           = ExecRiskLevel.PrivilegedExec,
        ["build.exec"]          = ExecRiskLevel.PrivilegedExec,
        ["build.scenes.set"]    = ExecRiskLevel.SafeWrite,
        // package management
        ["upm.list"]            = ExecRiskLevel.SafeRead,
        ["upm.install"]         = ExecRiskLevel.SafeWrite,
        ["upm.update"]          = ExecRiskLevel.SafeWrite,
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
        ["validate.asmdef"]          = ExecRiskLevel.SafeRead,
        ["validate.asset-refs"]      = ExecRiskLevel.SafeRead,
        ["validate.addressables"]    = ExecRiskLevel.SafeRead,
        ["validate.scripts"]         = ExecRiskLevel.SafeRead,
        // build subcommands (sprint C)
        ["build.addressables"]       = ExecRiskLevel.SafeWrite,
        ["build.cancel"]             = ExecRiskLevel.SafeWrite,
        ["build.targets"]            = ExecRiskLevel.SafeRead,
        // build workflow operations
        ["build.snapshot-packages"]  = ExecRiskLevel.SafeWrite,
        ["build.preflight"]          = ExecRiskLevel.SafeRead,
        ["build.artifact-metadata"]  = ExecRiskLevel.SafeRead,
        ["build.failure-classify"]   = ExecRiskLevel.SafeRead,
        ["build.report"]             = ExecRiskLevel.SafeRead,
        // diag operations (structural introspection — sprint 4)
        ["diag.script-defines"]      = ExecRiskLevel.SafeRead,
        ["diag.compile-errors"]      = ExecRiskLevel.SafeRead,
        ["diag.assembly-graph"]      = ExecRiskLevel.SafeRead,
        ["diag.scene-deps"]          = ExecRiskLevel.SafeRead,
        ["diag.prefab-deps"]         = ExecRiskLevel.SafeRead,
        // diag operations (advanced diagnostics — sprint 5)
        ["diag.asset-size"]          = ExecRiskLevel.SafeRead,
        ["diag.import-hotspots"]     = ExecRiskLevel.SafeRead,
        // test operations (subprocess, privileged exec)
        ["test.list"]                = ExecRiskLevel.SafeRead,
        ["test.run"]                 = ExecRiskLevel.PrivilegedExec,
        ["test.flaky-report"]        = ExecRiskLevel.SafeRead,
        // addressable operations
        ["addressable.init"]           = ExecRiskLevel.SafeWrite,
        ["addressable.profile.list"]   = ExecRiskLevel.SafeRead,
        ["addressable.profile.set"]    = ExecRiskLevel.SafeWrite,
        ["addressable.group.list"]     = ExecRiskLevel.SafeRead,
        ["addressable.group.create"]   = ExecRiskLevel.SafeWrite,
        ["addressable.group.remove"]   = ExecRiskLevel.DestructiveWrite,
        ["addressable.entry.add"]      = ExecRiskLevel.SafeWrite,
        ["addressable.entry.remove"]   = ExecRiskLevel.DestructiveWrite,
        ["addressable.entry.rename"]   = ExecRiskLevel.SafeWrite,
        ["addressable.entry.label"]    = ExecRiskLevel.SafeWrite,
        ["addressable.bulk.add"]       = ExecRiskLevel.SafeWrite,
        ["addressable.bulk.label"]     = ExecRiskLevel.SafeWrite,
        ["addressable.analyze"]        = ExecRiskLevel.SafeRead,
        // tag operations
        ["tag.list"]   = ExecRiskLevel.SafeRead,
        ["tag.add"]    = ExecRiskLevel.SafeWrite,
        ["tag.remove"] = ExecRiskLevel.DestructiveWrite,
        // layer operations
        ["layer.list"]   = ExecRiskLevel.SafeRead,
        ["layer.add"]    = ExecRiskLevel.SafeWrite,
        ["layer.rename"] = ExecRiskLevel.SafeWrite,
        ["layer.remove"] = ExecRiskLevel.DestructiveWrite,
        // time operations
        ["time.scale"]  = ExecRiskLevel.SafeWrite,
        // read-only queries
        ["hierarchy.snapshot"]  = ExecRiskLevel.SafeRead,
        ["go find"]             = ExecRiskLevel.SafeRead,
        ["settings inspect"]    = ExecRiskLevel.SafeRead,
        // console operations
        ["console dump"]        = ExecRiskLevel.SafeRead,
        ["console tail"]        = ExecRiskLevel.SafeRead,
        ["console clear"]       = ExecRiskLevel.SafeWrite,
        // playmode operations
        ["playmode.start"]      = ExecRiskLevel.PrivilegedExec,
        ["playmode.stop"]       = ExecRiskLevel.PrivilegedExec,
        ["playmode.pause"]      = ExecRiskLevel.SafeWrite,
        ["playmode.resume"]     = ExecRiskLevel.SafeWrite,
        ["playmode.step"]       = ExecRiskLevel.SafeWrite,
        // scene utilities
        ["scene load"]          = ExecRiskLevel.SafeWrite,
        ["scene add"]           = ExecRiskLevel.SafeWrite,
        ["scene unload"]        = ExecRiskLevel.SafeWrite,
        ["scene remove"]        = ExecRiskLevel.SafeWrite,
        // hierarchy utilities
        ["go duplicate"]        = ExecRiskLevel.SafeWrite,
        // meta
        ["session.open"]        = ExecRiskLevel.SafeRead,
        ["session.close"]       = ExecRiskLevel.SafeRead,
        ["session.status"]      = ExecRiskLevel.SafeRead,
        ["approval.confirm"]    = ExecRiskLevel.SafeRead,
        // recorder operations (lazy-loaded category)
        ["recorder.start"]  = ExecRiskLevel.PrivilegedExec,
        ["recorder.stop"]   = ExecRiskLevel.PrivilegedExec,
        ["recorder.status"] = ExecRiskLevel.SafeRead,
        ["recorder.config"] = ExecRiskLevel.SafeWrite,
        ["recorder.switch"] = ExecRiskLevel.SafeWrite,
        // profiling operations (lazy-loaded category)
        ["profiling.capabilities"]    = ExecRiskLevel.SafeRead,
        ["profiling.inspect"]         = ExecRiskLevel.SafeRead,
        ["profiling.start_recording"] = ExecRiskLevel.PrivilegedExec,
        ["profiling.stop_recording"]  = ExecRiskLevel.PrivilegedExec,
        ["profiling.load_profile"]    = ExecRiskLevel.SafeWrite,
        ["profiling.save_profile"]    = ExecRiskLevel.SafeWrite,
        ["profiling.take_snapshot"]   = ExecRiskLevel.SafeWrite,
        ["profiling.frames"]          = ExecRiskLevel.SafeRead,
        ["profiling.counters"]        = ExecRiskLevel.SafeRead,
        ["profiling.threads"]         = ExecRiskLevel.SafeRead,
        ["profiling.markers"]         = ExecRiskLevel.SafeRead,
        ["profiling.sample"]          = ExecRiskLevel.SafeRead,
        ["profiling.gc_alloc"]        = ExecRiskLevel.SafeRead,
        ["profiling.compare"]         = ExecRiskLevel.SafeRead,
        ["profiling.budget_check"]    = ExecRiskLevel.SafeRead,
        ["profiling.export_summary"]  = ExecRiskLevel.SafeRead,
        ["profiling.live_start"]      = ExecRiskLevel.PrivilegedExec,
        ["profiling.live_stop"]       = ExecRiskLevel.PrivilegedExec,
        ["profiling.recorders_list"]  = ExecRiskLevel.SafeRead,
        ["profiling.frame_timing"]    = ExecRiskLevel.SafeRead,
        ["profiling.binary_log_start"]= ExecRiskLevel.PrivilegedExec,
        ["profiling.binary_log_stop"] = ExecRiskLevel.PrivilegedExec,
        ["profiling.annotate_session"]= ExecRiskLevel.SafeWrite,
        ["profiling.annotate_frame"]  = ExecRiskLevel.SafeWrite,
        ["profiling.gpu_capture_begin"] = ExecRiskLevel.PrivilegedExec,
        ["profiling.gpu_capture_end"]   = ExecRiskLevel.PrivilegedExec,
        // debug artifact (composite, handled by router)
        ["debug-artifact.collect"]  = ExecRiskLevel.SafeRead,
        ["debug-artifact.prep"]     = ExecRiskLevel.PrivilegedExec,
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

            case "upm.list":
            {
                var outdated = req.Args is not null
                    && req.Args.Value.TryGetProperty("outdated", out var outdatedProp)
                    && outdatedProp.ValueKind == JsonValueKind.True;
                var builtin = req.Args is not null
                    && req.Args.Value.TryGetProperty("builtin", out var builtinProp)
                    && builtinProp.ValueKind == JsonValueKind.True;
                var git = req.Args is not null
                    && req.Args.Value.TryGetProperty("git", out var gitProp)
                    && gitProp.ValueKind == JsonValueKind.True;

                var content = $"{{\"outdated\":{(outdated ? "true" : "false")},\"builtin\":{(builtin ? "true" : "false")},\"git\":{(git ? "true" : "false")}}}";
                dto = new ProjectCommandRequestDto("upm-list", null, null, content, req.RequestId);
                return true;
            }

            case "upm.install":
            {
                var target = GetString(req.Args, "target");
                if (string.IsNullOrWhiteSpace(target))
                {
                    validationError = "upm.install requires args.target (registry ID, Git URL, or file: path)";
                    return false;
                }

                var content = $"{{\"target\":\"{target}\"}}";
                var base_ = new ProjectCommandRequestDto("upm-install", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "upm.update":
            {
                var id = GetString(req.Args, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    validationError = "upm.update requires args.id";
                    return false;
                }

                var version = GetString(req.Args, "version");
                var content = string.IsNullOrWhiteSpace(version)
                    ? $"{{\"id\":\"{id}\"}}"
                    : $"{{\"id\":\"{id}\",\"version\":\"{version}\"}}";
                var base_ = new ProjectCommandRequestDto("upm-install", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "build.addressables":
            {
                var clean = req.Args is not null
                    && req.Args.Value.TryGetProperty("clean", out var cleanProp)
                    && cleanProp.ValueKind == JsonValueKind.True;
                var update = req.Args is not null
                    && req.Args.Value.TryGetProperty("update", out var updateProp)
                    && updateProp.ValueKind == JsonValueKind.True;
                var content = $"{{\"clean\":{(clean ? "true" : "false")},\"update\":{(update ? "true" : "false")}}}";
                var base_ = new ProjectCommandRequestDto("build-addressables", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "build.cancel":
            {
                var base_ = new ProjectCommandRequestDto("build-cancel", null, null, null, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "build.targets":
            {
                dto = new ProjectCommandRequestDto("build-targets", null, null, null, req.RequestId);
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

            case "time.scale":
            {
                var scale = GetFloat(req.Args, "scale");
                if (scale is null)
                {
                    validationError = "time.scale requires args.scale (float)";
                    return false;
                }

                var content = JsonSerializer.Serialize(new { scale = scale.Value });
                var base_ = new ProjectCommandRequestDto("time-scale", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
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

            case "validate.asmdef":
            {
                dto = new ProjectCommandRequestDto("validate-asmdef", null, null, null, req.RequestId);
                return true;
            }

            case "validate.asset-refs":
            {
                dto = new ProjectCommandRequestDto("validate-asset-refs", null, null, null, req.RequestId);
                return true;
            }

            case "validate.addressables":
            {
                dto = new ProjectCommandRequestDto("validate-addressables", null, null, null, req.RequestId);
                return true;
            }

            case "build.snapshot-packages":
            {
                dto = new ProjectCommandRequestDto("build-snapshot-packages", null, null, null, req.RequestId);
                return true;
            }

            case "build.preflight":
            {
                dto = new ProjectCommandRequestDto("build-preflight", null, null, null, req.RequestId);
                return true;
            }

            case "build.artifact-metadata":
            {
                dto = new ProjectCommandRequestDto("build-artifact-metadata", null, null, null, req.RequestId);
                return true;
            }

            case "build.failure-classify":
            {
                dto = new ProjectCommandRequestDto("build-failure-classify", null, null, null, req.RequestId);
                return true;
            }

            case "build.report":
            {
                dto = new ProjectCommandRequestDto("build-report", null, null, null, req.RequestId);
                return true;
            }

            case "diag.script-defines":
            {
                dto = new ProjectCommandRequestDto("diag-script-defines", null, null, null, req.RequestId);
                return true;
            }

            case "diag.compile-errors":
            {
                dto = new ProjectCommandRequestDto("diag-compile-errors", null, null, null, req.RequestId);
                return true;
            }

            case "diag.assembly-graph":
            {
                dto = new ProjectCommandRequestDto("diag-assembly-graph", null, null, null, req.RequestId);
                return true;
            }

            case "diag.scene-deps":
            {
                dto = new ProjectCommandRequestDto("diag-scene-deps", null, null, null, req.RequestId);
                return true;
            }

            case "diag.prefab-deps":
            {
                dto = new ProjectCommandRequestDto("diag-prefab-deps", null, null, null, req.RequestId);
                return true;
            }

            case "diag.asset-size":
            {
                dto = new ProjectCommandRequestDto("diag-asset-size", null, null, null, req.RequestId);
                return true;
            }

            case "diag.import-hotspots":
            {
                dto = new ProjectCommandRequestDto("diag-import-hotspots", null, null, null, req.RequestId);
                return true;
            }

            case "go find":
            {
                var query = GetString(req.Args, "query") ?? string.Empty;
                var limit = GetInt(req.Args, "limit") ?? 20;
                var parentId = GetInt(req.Args, "parentId") ?? 0;
                var tag = GetString(req.Args, "tag");
                var layer = GetString(req.Args, "layer");
                var component = GetString(req.Args, "component");

                if (string.IsNullOrWhiteSpace(query)
                    && string.IsNullOrWhiteSpace(tag)
                    && string.IsNullOrWhiteSpace(layer)
                    && string.IsNullOrWhiteSpace(component))
                {
                    validationError = "go find requires args.query or at least one of args.tag/args.layer/args.component";
                    return false;
                }

                var content = JsonSerializer.Serialize(new
                {
                    query,
                    limit,
                    parentId,
                    tag,
                    layer,
                    component
                });
                dto = new ProjectCommandRequestDto("hierarchy-find", null, null, content, req.RequestId);
                return true;
            }

            case "settings inspect":
            {
                dto = new ProjectCommandRequestDto("settings-inspect", null, null, null, req.RequestId);
                return true;
            }

            case "console dump":
            {
                var type = GetString(req.Args, "type");
                var limit = GetInt(req.Args, "limit") ?? 100;
                var content = JsonSerializer.Serialize(new { type, limit });
                dto = new ProjectCommandRequestDto("console-dump", null, null, content, req.RequestId);
                return true;
            }

            case "console tail":
            {
                var follow = req.Args is not null
                    && req.Args.Value.TryGetProperty("follow", out var followProp)
                    && followProp.ValueKind == JsonValueKind.True;
                var content = JsonSerializer.Serialize(new { follow });
                dto = new ProjectCommandRequestDto("console-tail", null, null, content, req.RequestId);
                return true;
            }

            case "console clear":
            {
                var base_ = new ProjectCommandRequestDto("console-clear", null, null, null, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "playmode start":
            case "playmode.start":
            {
                var base_ = new ProjectCommandRequestDto("playmode-start", null, null, null, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "playmode stop":
            case "playmode.stop":
            {
                var base_ = new ProjectCommandRequestDto("playmode-stop", null, null, null, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "playmode pause":
            case "playmode.pause":
            {
                var base_ = new ProjectCommandRequestDto("playmode-pause", null, null, null, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "playmode resume":
            case "playmode.resume":
            {
                var base_ = new ProjectCommandRequestDto("playmode-resume", null, null, null, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "playmode step":
            case "playmode.step":
            {
                var base_ = new ProjectCommandRequestDto("playmode-step", null, null, null, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "scene load":
            case "scene add":
            case "scene unload":
            case "scene remove":
            {
                var scenePath = GetString(req.Args, "scenePath");
                if (string.IsNullOrWhiteSpace(scenePath))
                {
                    validationError = $"{req.Operation} requires args.scenePath";
                    return false;
                }

                var normalizedOperation = req.Operation.Replace('.', ' ');
                var projectAction = normalizedOperation.ToLowerInvariant() switch
                {
                    "scene load" => "scene-load",
                    "scene add" => "scene-add",
                    "scene unload" => "scene-unload",
                    _ => "scene-remove"
                };
                var content = JsonSerializer.Serialize(new { scenePath });
                var base_ = new ProjectCommandRequestDto(projectAction, null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "go duplicate":
            {
                var targetId = GetInt(req.Args, "targetId") ?? 0;
                if (targetId == 0)
                {
                    validationError = "go duplicate requires args.targetId";
                    return false;
                }

                var parentId = GetInt(req.Args, "parentId") ?? 0;
                var name = GetString(req.Args, "name");
                var content = JsonSerializer.Serialize(new { targetId, parentId, name });
                var base_ = new ProjectCommandRequestDto("hierarchy-duplicate", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            // ── addressable operations ──────────────────────────────────
            case "addressable.init":
            {
                var content = JsonSerializer.Serialize(new { operation = "init" });
                var base_ = new ProjectCommandRequestDto("addressables-cli", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "addressable.profile.list":
            {
                var content = JsonSerializer.Serialize(new { operation = "profile-list" });
                dto = new ProjectCommandRequestDto("addressables-cli", null, null, content, req.RequestId);
                return true;
            }

            case "addressable.profile.set":
            {
                var name = GetString(req.Args, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    validationError = "addressable.profile.set requires args.name";
                    return false;
                }

                var content = JsonSerializer.Serialize(new { operation = "profile-set", name });
                var base_ = new ProjectCommandRequestDto("addressables-cli", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "addressable.group.list":
            {
                var content = JsonSerializer.Serialize(new { operation = "group-list" });
                dto = new ProjectCommandRequestDto("addressables-cli", null, null, content, req.RequestId);
                return true;
            }

            case "addressable.group.create":
            {
                var name = GetString(req.Args, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    validationError = "addressable.group.create requires args.name";
                    return false;
                }

                var setDefault = req.Args is not null
                    && req.Args.Value.TryGetProperty("default", out var defaultProp)
                    && defaultProp.ValueKind == JsonValueKind.True;
                var content = JsonSerializer.Serialize(new { operation = "group-create", name, setDefault });
                var base_ = new ProjectCommandRequestDto("addressables-cli", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "addressable.group.remove":
            {
                var name = GetString(req.Args, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    validationError = "addressable.group.remove requires args.name";
                    return false;
                }

                var content = JsonSerializer.Serialize(new { operation = "group-remove", name });
                var base_ = new ProjectCommandRequestDto("addressables-cli", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "addressable.entry.add":
            {
                var assetPath = GetString(req.Args, "assetPath");
                var groupName = GetString(req.Args, "groupName");
                if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(groupName))
                {
                    validationError = "addressable.entry.add requires args.assetPath and args.groupName";
                    return false;
                }

                var content = JsonSerializer.Serialize(new { operation = "entry-add", assetPath, groupName });
                var base_ = new ProjectCommandRequestDto("addressables-cli", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "addressable.entry.remove":
            {
                var assetPath = GetString(req.Args, "assetPath");
                if (string.IsNullOrWhiteSpace(assetPath))
                {
                    validationError = "addressable.entry.remove requires args.assetPath";
                    return false;
                }

                var content = JsonSerializer.Serialize(new { operation = "entry-remove", assetPath });
                var base_ = new ProjectCommandRequestDto("addressables-cli", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "addressable.entry.rename":
            {
                var assetPath = GetString(req.Args, "assetPath");
                var newAddress = GetString(req.Args, "newAddress");
                if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(newAddress))
                {
                    validationError = "addressable.entry.rename requires args.assetPath and args.newAddress";
                    return false;
                }

                var content = JsonSerializer.Serialize(new { operation = "entry-rename", assetPath, address = newAddress });
                var base_ = new ProjectCommandRequestDto("addressables-cli", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "addressable.entry.label":
            {
                var assetPath = GetString(req.Args, "assetPath");
                var label = GetString(req.Args, "label");
                if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(label))
                {
                    validationError = "addressable.entry.label requires args.assetPath and args.label";
                    return false;
                }

                var remove = req.Args is not null
                    && req.Args.Value.TryGetProperty("remove", out var removeProp)
                    && removeProp.ValueKind == JsonValueKind.True;
                var content = JsonSerializer.Serialize(new { operation = "entry-label", assetPath, label, remove });
                var base_ = new ProjectCommandRequestDto("addressables-cli", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "addressable.bulk.add":
            {
                var folder = GetString(req.Args, "folder");
                var group = GetString(req.Args, "group");
                if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(group))
                {
                    validationError = "addressable.bulk.add requires args.folder and args.group";
                    return false;
                }

                var type = GetString(req.Args, "type");
                var content = JsonSerializer.Serialize(new { operation = "bulk-add", folder, groupName = group, type });
                var base_ = new ProjectCommandRequestDto("addressables-cli", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "addressable.bulk.label":
            {
                var folder = GetString(req.Args, "folder");
                var label = GetString(req.Args, "label");
                if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(label))
                {
                    validationError = "addressable.bulk.label requires args.folder and args.label";
                    return false;
                }

                var type = GetString(req.Args, "type");
                var remove = req.Args is not null
                    && req.Args.Value.TryGetProperty("remove", out var removeProp)
                    && removeProp.ValueKind == JsonValueKind.True;
                var content = JsonSerializer.Serialize(new { operation = "bulk-label", folder, label, type, remove });
                var base_ = new ProjectCommandRequestDto("addressables-cli", null, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "addressable.analyze":
            {
                var duplicate = req.Args is not null
                    && req.Args.Value.TryGetProperty("duplicate", out var dupProp)
                    && dupProp.ValueKind == JsonValueKind.True;
                var content = JsonSerializer.Serialize(new { operation = "analyze", duplicate });
                dto = new ProjectCommandRequestDto("addressables-cli", null, null, content, req.RequestId);
                return true;
            }

            // ── animator operations ────────────────────────────────────────
            case "animator.param.add":
            {
                var assetPath = GetString(req.Args, "assetPath");
                var name = GetString(req.Args, "name");
                var type = GetString(req.Args, "type");
                if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
                {
                    validationError = "animator.param.add requires args.assetPath, args.name, and args.type (float|int|bool|trigger)";
                    return false;
                }

                var content = JsonSerializer.Serialize(new { name, type });
                var base_ = new ProjectCommandRequestDto("animator-param-add", assetPath, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "animator.param.remove":
            {
                var assetPath = GetString(req.Args, "assetPath");
                var name = GetString(req.Args, "name");
                if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(name))
                {
                    validationError = "animator.param.remove requires args.assetPath and args.name";
                    return false;
                }

                var content = JsonSerializer.Serialize(new { name });
                var base_ = new ProjectCommandRequestDto("animator-param-remove", assetPath, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "animator.state.add":
            {
                var assetPath = GetString(req.Args, "assetPath");
                var name = GetString(req.Args, "name");
                if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(name))
                {
                    validationError = "animator.state.add requires args.assetPath and args.name";
                    return false;
                }

                var layer = GetInt(req.Args, "layer") ?? 0;
                var content = JsonSerializer.Serialize(new { name, layer });
                var base_ = new ProjectCommandRequestDto("animator-state-add", assetPath, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "animator.transition.add":
            {
                var assetPath = GetString(req.Args, "assetPath");
                var fromState = GetString(req.Args, "fromState");
                var toState = GetString(req.Args, "toState");
                if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(fromState) || string.IsNullOrWhiteSpace(toState))
                {
                    validationError = "animator.transition.add requires args.assetPath, args.fromState, and args.toState";
                    return false;
                }

                var layer = GetInt(req.Args, "layer") ?? 0;
                var content = JsonSerializer.Serialize(new { fromState, toState, layer });
                var base_ = new ProjectCommandRequestDto("animator-transition-add", assetPath, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            // ── clip operations ────────────────────────────────────────────
            case "clip.config":
            {
                var assetPath = GetString(req.Args, "assetPath");
                if (string.IsNullOrWhiteSpace(assetPath))
                {
                    validationError = "clip.config requires args.assetPath";
                    return false;
                }

                var loopTime = GetBool(req.Args, "loopTime");
                var loopPose = GetBool(req.Args, "loopPose");
                if (loopTime == null && loopPose == null)
                {
                    validationError = "clip.config requires at least one of args.loopTime or args.loopPose";
                    return false;
                }

                var content = JsonSerializer.Serialize(new
                {
                    loopTime = loopTime ?? false,
                    loopPose = loopPose ?? false,
                    setLoopTime = loopTime != null,
                    setLoopPose = loopPose != null
                });
                var base_ = new ProjectCommandRequestDto("clip-config", assetPath, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "clip.event.add":
            {
                var assetPath = GetString(req.Args, "assetPath");
                var functionName = GetString(req.Args, "functionName");
                if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(functionName))
                {
                    validationError = "clip.event.add requires args.assetPath and args.functionName";
                    return false;
                }

                var time = GetFloat(req.Args, "time") ?? 0f;
                var stringParam = GetString(req.Args, "string");
                var floatParam = GetFloat(req.Args, "float");
                var intParam = GetInt(req.Args, "int");
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
                var base_ = new ProjectCommandRequestDto("clip-event-add", assetPath, null, content, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "clip.event.clear":
            {
                var assetPath = GetString(req.Args, "assetPath");
                if (string.IsNullOrWhiteSpace(assetPath))
                {
                    validationError = "clip.event.clear requires args.assetPath";
                    return false;
                }

                var base_ = new ProjectCommandRequestDto("clip-event-clear", assetPath, null, null, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            case "clip.curve.clear":
            {
                var assetPath = GetString(req.Args, "assetPath");
                if (string.IsNullOrWhiteSpace(assetPath))
                {
                    validationError = "clip.curve.clear requires args.assetPath";
                    return false;
                }

                var base_ = new ProjectCommandRequestDto("clip-curve-clear", assetPath, null, null, req.RequestId);
                var withIntent = MutationIntentFactory.EnsureProjectIntent(base_);
                dto = withIntent with { Intent = withIntent.Intent! with { Flags = withIntent.Intent.Flags with { DryRun = dryRun } } };
                return true;
            }

            // ── tag operations ──────────────────────────────────────────────

            case "tag.list":
            {
                dto = new ProjectCommandRequestDto("tag-list", null, null, null, req.RequestId);
                return true;
            }

            case "tag.add":
            {
                var name = GetString(req.Args, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    validationError = "tag.add requires args.name";
                    return false;
                }

                var content = JsonSerializer.Serialize(new { name });
                dto = new ProjectCommandRequestDto("tag-add", null, null, content, req.RequestId);
                return true;
            }

            case "tag.remove":
            {
                var name = GetString(req.Args, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    validationError = "tag.remove requires args.name";
                    return false;
                }

                var content = JsonSerializer.Serialize(new { name });
                dto = new ProjectCommandRequestDto("tag-remove", null, null, content, req.RequestId);
                return true;
            }

            // ── layer operations ─────────────────────────────────────────────

            case "layer.list":
            {
                dto = new ProjectCommandRequestDto("layer-list", null, null, null, req.RequestId);
                return true;
            }

            case "layer.add":
            {
                var name = GetString(req.Args, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    validationError = "layer.add requires args.name";
                    return false;
                }

                var index = GetInt(req.Args, "index");
                var content = index.HasValue
                    ? JsonSerializer.Serialize(new { name, index = index.Value })
                    : JsonSerializer.Serialize(new { name });
                dto = new ProjectCommandRequestDto("layer-add", null, null, content, req.RequestId);
                return true;
            }

            case "layer.rename":
            {
                var nameOrIndex = GetString(req.Args, "nameOrIndex");
                var newName = GetString(req.Args, "newName");
                if (string.IsNullOrWhiteSpace(nameOrIndex) || string.IsNullOrWhiteSpace(newName))
                {
                    validationError = "layer.rename requires args.nameOrIndex and args.newName";
                    return false;
                }

                var content = JsonSerializer.Serialize(new { nameOrIndex, newName });
                dto = new ProjectCommandRequestDto("layer-rename", null, null, content, req.RequestId);
                return true;
            }

            case "layer.remove":
            {
                var nameOrIndex = GetString(req.Args, "nameOrIndex");
                if (string.IsNullOrWhiteSpace(nameOrIndex))
                {
                    validationError = "layer.remove requires args.nameOrIndex";
                    return false;
                }

                var content = JsonSerializer.Serialize(new { nameOrIndex });
                dto = new ProjectCommandRequestDto("layer-remove", null, null, content, req.RequestId);
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
            case "test.flaky-report":
            // validate.scripts runs dotnet build locally — not dispatched through daemon
            case "validate.scripts":
            // debug-artifact operations are composite, orchestrated by the router
            case "debug-artifact.collect":
            case "debug-artifact.prep":
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

        if (TryGetPropertyWithAliases(element.Value, key, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    private static int? GetInt(JsonElement? element, string key)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGetPropertyWithAliases(element.Value, key, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetInt32();
        }

        return null;
    }

    private static float? GetFloat(JsonElement? element, string key)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGetPropertyWithAliases(element.Value, key, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetSingle();
        }

        return null;
    }

    private static bool? GetBool(JsonElement? element, string key)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGetPropertyWithAliases(element.Value, key, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }

        return null;
    }

    /// <summary>Returns the raw JSON text of a property (for arrays/objects passed through as-is).</summary>
    private static string? GetRawString(JsonElement? element, string key)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryGetPropertyWithAliases(element.Value, key, out var prop)
            ? prop.GetRawText()
            : null;
    }

    private static bool TryGetPropertyWithAliases(JsonElement element, string key, out JsonElement value)
    {
        if (element.TryGetProperty(key, out value))
        {
            return true;
        }

        var kebab = ToKebabCase(key);
        if (!kebab.Equals(key, StringComparison.Ordinal)
            && element.TryGetProperty(kebab, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string ToKebabCase(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var chars = new List<char>(key.Length + 4);
        for (var i = 0; i < key.Length; i++)
        {
            var ch = key[i];
            if (char.IsUpper(ch))
            {
                if (i > 0)
                {
                    chars.Add('-');
                }

                chars.Add(char.ToLowerInvariant(ch));
                continue;
            }

            chars.Add(ch);
        }

        return new string(chars.ToArray());
    }
}
