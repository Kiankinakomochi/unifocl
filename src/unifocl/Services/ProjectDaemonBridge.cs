using System.Text.Json;

internal sealed class ProjectDaemonBridge
{
    public const string StubbedBridgePrefix = "stubbed bridge:";

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _projectPath;

    public ProjectDaemonBridge(string? projectPath)
    {
        _projectPath = string.IsNullOrWhiteSpace(projectPath)
            ? Directory.GetCurrentDirectory()
            : projectPath;
    }

    public bool TryHandle(string? commandLine, out string response)
    {
        response = string.Empty;
        if (string.IsNullOrWhiteSpace(commandLine) || !commandLine.StartsWith("PROJECT_CMD ", StringComparison.Ordinal))
        {
            return false;
        }

        var payload = commandLine["PROJECT_CMD ".Length..];
        ProjectCommandRequestDto? request;
        try
        {
            request = JsonSerializer.Deserialize<ProjectCommandRequestDto>(payload, _jsonOptions);
        }
        catch (JsonException)
        {
            response = SerializeError($"{StubbedBridgePrefix} invalid project command payload");
            return true;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Action))
        {
            response = SerializeError($"{StubbedBridgePrefix} missing project command payload");
            return true;
        }

        if (DaemonMutationActionCatalog.IsProjectMutation(request.Action))
        {
            if (!TryValidateMutationIntent(request.Intent, out var validationError))
            {
                response = SerializeError($"{StubbedBridgePrefix} {validationError}");
                return true;
            }

            if (request.Intent!.Flags.DryRun)
            {
                response = JsonSerializer.Serialize(
                    new ProjectCommandResponseDto(true, "dry-run accepted (stubbed bridge fallback)", "dry-run", null),
                    _jsonOptions);
                return true;
            }
        }

        var result = request.Action switch
        {
            "mk-script" => HandleCreateScript(request),
            "mk-asset" => HandleCreateAsset(request),
            "rename-asset" => HandleRenameAsset(request),
            "duplicate-asset" => HandleDuplicateAsset(request),
            "remove-asset" => HandleRemoveAsset(request),
            "load-asset" => HandleLoadAsset(request),
            "upm-list" => HandleUpmList(request),
            "upm-install" => RequireBridgeMode("upm-install"),
            "upm-remove" => RequireBridgeMode("upm-remove"),
            "build-run" => RequireBridgeMode("build-run"),
            "build-exec" => RequireBridgeMode("build-exec"),
            "build-scenes-get" => RequireBridgeMode("build-scenes-get"),
            "build-scenes-set" => RequireBridgeMode("build-scenes-set"),
            "build-addressables" => RequireBridgeMode("build-addressables"),
            "addressables-cli" => RequireBridgeMode("addressables-cli"),
            "build-targets" => HandleBuildTargetsStub(),
            "build-cancel" => HandleBuildCancelStub(),
            "hierarchy-find" => RequireBridgeMode("hierarchy-find"),
            "settings-inspect" => RequireBridgeMode("settings-inspect"),
            "console-clear" => RequireBridgeMode("console-clear"),
            "time-scale" => RequireBridgeMode("time-scale"),
            "scene-load" => RequireBridgeMode("scene-load"),
            "scene-add" => RequireBridgeMode("scene-add"),
            "scene-unload" => RequireBridgeMode("scene-unload"),
            "scene-remove" => RequireBridgeMode("scene-remove"),
            "hierarchy-duplicate" => RequireBridgeMode("hierarchy-duplicate"),
            "prefab-create" => RequireBridgeMode("prefab-create"),
            "prefab-apply" => RequireBridgeMode("prefab-apply"),
            "prefab-revert" => RequireBridgeMode("prefab-revert"),
            "prefab-unpack" => RequireBridgeMode("prefab-unpack"),
            "prefab-variant" => RequireBridgeMode("prefab-variant"),
            "validate-scene-list" => RequireBridgeMode("validate-scene-list"),
            "validate-missing-scripts" => RequireBridgeMode("validate-missing-scripts"),
            "validate-build-settings" => RequireBridgeMode("validate-build-settings"),
            "validate-packages" => HandleValidatePackages(),
            "validate-asmdef" => HandleValidateAsmdef(),
            "validate-asset-refs" => RequireBridgeMode("validate-asset-refs"),
            "validate-addressables" => RequireBridgeMode("validate-addressables"),
            "build-snapshot-packages" => HandleBuildSnapshotPackages(),
            "build-preflight" => RequireBridgeMode("build-preflight"),
            "build-artifact-metadata" => RequireBridgeMode("build-artifact-metadata"),
            "build-failure-classify" => RequireBridgeMode("build-failure-classify"),
            _ => new ProjectCommandResponseDto(false, $"{StubbedBridgePrefix} unsupported action: {request.Action}", null, null)
        };
        response = JsonSerializer.Serialize(result, _jsonOptions);
        return true;
    }

    private ProjectCommandResponseDto HandleCreateScript(ProjectCommandRequestDto request)
    {
        if (!IsValidAssetPath(request.AssetPath) || string.IsNullOrWhiteSpace(request.Content))
        {
            return new ProjectCommandResponseDto(false, $"{StubbedBridgePrefix} mk-script requires assetPath and content", null, null);
        }

        var absolutePath = ResolveAssetPath(request.AssetPath!);
        if (File.Exists(absolutePath))
        {
            return new ProjectCommandResponseDto(false, $"asset already exists: {request.AssetPath}", null, null);
        }

        try
        {
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(absolutePath, request.Content);
            return new ProjectCommandResponseDto(true, "script created (stubbed bridge fallback)", "script", null);
        }
        catch (Exception ex)
        {
            return new ProjectCommandResponseDto(false, $"failed to create script: {ex.Message}", null, null);
        }
    }

    private ProjectCommandResponseDto HandleCreateAsset(ProjectCommandRequestDto request)
    {
        if (!IsValidAssetPath(request.AssetPath) || string.IsNullOrWhiteSpace(request.Content))
        {
            return new ProjectCommandResponseDto(false, $"{StubbedBridgePrefix} mk-asset requires assetPath and content", null, null);
        }

        MkAssetRequestPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<MkAssetRequestPayload>(request.Content, _jsonOptions);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Type))
        {
            return new ProjectCommandResponseDto(false, $"{StubbedBridgePrefix} mk-asset requires type", null, null);
        }

        var count = payload.Count <= 0 ? 1 : Math.Min(payload.Count, 100);
        var created = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var relativePath = BuildStubAssetPath(
                request.AssetPath!,
                payload.Type,
                payload.Name,
                i,
                count);
            relativePath = EnsureUniqueStubAssetPath(relativePath);
            var absolutePath = ResolveAssetPath(relativePath);
            try
            {
                if (relativePath.EndsWith("/", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(absolutePath);
                    created.Add(relativePath.TrimEnd('/'));
                    continue;
                }

                var directory = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(absolutePath))
                {
                    return new ProjectCommandResponseDto(false, $"asset already exists: {relativePath}", null, null);
                }

                File.WriteAllText(absolutePath, $"// generated by unifocl stub bridge: {payload.Type}");
                created.Add(relativePath);
            }
            catch (Exception ex)
            {
                return new ProjectCommandResponseDto(false, $"failed to create asset: {ex.Message}", null, null);
            }
        }

        var responseContent = JsonSerializer.Serialize(new MkAssetResponsePayload(created), _jsonOptions);
        return new ProjectCommandResponseDto(true, "asset(s) created (stubbed bridge fallback)", "asset", responseContent);
    }

    private ProjectCommandResponseDto HandleRenameAsset(ProjectCommandRequestDto request)
    {
        if (!IsValidAssetPath(request.AssetPath) || !IsValidAssetPath(request.NewAssetPath))
        {
            return new ProjectCommandResponseDto(false, $"{StubbedBridgePrefix} rename-asset requires assetPath and newAssetPath", null, null);
        }

        var sourcePath = ResolveAssetPath(request.AssetPath!);
        var targetPath = ResolveAssetPath(request.NewAssetPath!);
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            return new ProjectCommandResponseDto(false, $"asset not found: {request.AssetPath}", null, null);
        }

        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            return new ProjectCommandResponseDto(false, $"target already exists: {request.NewAssetPath}", null, null);
        }

        try
        {
            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            if (Directory.Exists(sourcePath))
            {
                Directory.Move(sourcePath, targetPath);
            }
            else
            {
                File.Move(sourcePath, targetPath);
            }

            MoveMetaIfPresent(sourcePath, targetPath);
            return new ProjectCommandResponseDto(true, "asset renamed (stubbed bridge fallback)", null, null);
        }
        catch (Exception ex)
        {
            return new ProjectCommandResponseDto(false, $"failed to rename asset: {ex.Message}", null, null);
        }
    }

    private ProjectCommandResponseDto HandleRemoveAsset(ProjectCommandRequestDto request)
    {
        if (!IsValidAssetPath(request.AssetPath))
        {
            return new ProjectCommandResponseDto(false, $"{StubbedBridgePrefix} remove-asset requires assetPath", null, null);
        }

        var absolutePath = ResolveAssetPath(request.AssetPath!);
        if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
        {
            return new ProjectCommandResponseDto(false, $"asset not found: {request.AssetPath}", null, null);
        }

        try
        {
            if (Directory.Exists(absolutePath))
            {
                Directory.Delete(absolutePath, recursive: true);
            }
            else
            {
                File.Delete(absolutePath);
            }

            DeleteMetaIfPresent(absolutePath);
            return new ProjectCommandResponseDto(true, "asset removed (stubbed bridge fallback)", null, null);
        }
        catch (Exception ex)
        {
            return new ProjectCommandResponseDto(false, $"failed to remove asset: {ex.Message}", null, null);
        }
    }

    private ProjectCommandResponseDto HandleDuplicateAsset(ProjectCommandRequestDto request)
    {
        if (!IsValidAssetPath(request.AssetPath) || !IsValidAssetPath(request.NewAssetPath))
        {
            return new ProjectCommandResponseDto(false, $"{StubbedBridgePrefix} duplicate-asset requires assetPath and newAssetPath", null, null);
        }

        var sourcePath = ResolveAssetPath(request.AssetPath!);
        var targetPath = ResolveAssetPath(request.NewAssetPath!);
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            return new ProjectCommandResponseDto(false, $"asset not found: {request.AssetPath}", null, null);
        }

        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            return new ProjectCommandResponseDto(false, $"target already exists: {request.NewAssetPath}", null, null);
        }

        try
        {
            if (Directory.Exists(sourcePath))
            {
                CopyDirectoryRecursive(sourcePath, targetPath);
            }
            else
            {
                var targetDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                File.Copy(sourcePath, targetPath);
            }

            CopyMetaIfPresent(sourcePath, targetPath);
            return new ProjectCommandResponseDto(true, "asset duplicated (stubbed bridge fallback)", null, null);
        }
        catch (Exception ex)
        {
            return new ProjectCommandResponseDto(false, $"failed to duplicate asset: {ex.Message}", null, null);
        }
    }

    private static void CopyDirectoryRecursive(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        foreach (var file in Directory.GetFiles(sourcePath))
        {
            var fileName = Path.GetFileName(file);
            var targetFile = Path.Combine(destinationPath, fileName);
            File.Copy(file, targetFile);
        }

        foreach (var directory in Directory.GetDirectories(sourcePath))
        {
            var name = Path.GetFileName(directory);
            CopyDirectoryRecursive(directory, Path.Combine(destinationPath, name));
        }
    }

    private ProjectCommandResponseDto HandleLoadAsset(ProjectCommandRequestDto request)
    {
        if (!IsValidAssetPath(request.AssetPath))
        {
            return new ProjectCommandResponseDto(false, $"{StubbedBridgePrefix} load-asset requires assetPath", null, null);
        }

        var assetPath = request.AssetPath!;
        var absolutePath = ResolveAssetPath(assetPath);
        if (!File.Exists(absolutePath))
        {
            return new ProjectCommandResponseDto(false, $"asset not found: {request.AssetPath}", null, null);
        }

        var extension = Path.GetExtension(assetPath);
        if (extension.Equals(".unity", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            var kind = extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase) ? "prefab" : "scene";
            return new ProjectCommandResponseDto(
                false,
                $"{StubbedBridgePrefix} {kind} load is unavailable without Bridge mode: {assetPath}",
                null,
                null);
        }

        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return new ProjectCommandResponseDto(
                true,
                "script path resolved (stubbed bridge fallback; Unity script open unavailable)",
                "script",
                null);
        }

        return new ProjectCommandResponseDto(
            false,
            $"unsupported asset type: {extension} (supported: .unity, .prefab, .cs)",
            null,
            null);
    }

    private ProjectCommandResponseDto HandleUpmList(ProjectCommandRequestDto request)
    {
        UpmListRequestPayload? payload = null;
        if (!string.IsNullOrWhiteSpace(request.Content))
        {
            try
            {
                payload = JsonSerializer.Deserialize<UpmListRequestPayload>(request.Content, _jsonOptions);
            }
            catch (JsonException)
            {
                payload = null;
            }
        }

        var includeOutdated = payload?.IncludeOutdated ?? false;
        var includeBuiltin = payload?.IncludeBuiltin ?? false;
        var includeGit = payload?.IncludeGit ?? false;
        var manifestPath = Path.Combine(_projectPath, "Packages", "manifest.json");
        if (!File.Exists(manifestPath))
        {
            var empty = JsonSerializer.Serialize(new UpmListResponsePayload([]), _jsonOptions);
            return new ProjectCommandResponseDto(true, "upm package list loaded from manifest", "upm-list", empty);
        }

        List<UpmListPackagePayload> packages;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object)
            {
                packages = [];
            }
            else
            {
                packages = dependencies
                    .EnumerateObject()
                    .Where(property => property.Value.ValueKind == JsonValueKind.String)
                    .Select(property =>
                    {
                        var version = property.Value.GetString() ?? string.Empty;
                        var source = ResolveUpmSource(version);
                        return new UpmListPackagePayload(
                            property.Name,
                            property.Name,
                            version,
                            source,
                            null,
                            false,
                            false,
                            false);
                    })
                    .Where(package =>
                        (!includeGit || string.Equals(package.Source, "Git", StringComparison.OrdinalIgnoreCase))
                        && (includeBuiltin || !string.Equals(package.Source, "BuiltIn", StringComparison.OrdinalIgnoreCase))
                        && (!includeOutdated || package.IsOutdated))
                    .OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            return new ProjectCommandResponseDto(false, $"failed to read package manifest: {ex.Message}", null, null);
        }

        var responsePayload = JsonSerializer.Serialize(new UpmListResponsePayload(packages), _jsonOptions);
        return new ProjectCommandResponseDto(true, "upm package list loaded from manifest", "upm-list", responsePayload);
    }

    private ProjectCommandResponseDto HandleBuildTargetsStub()
    {
        var payload = JsonSerializer.Serialize(
            new BuildTargetsResponsePayload(
            [
                new BuildTargetPayload("Win64", true, "StandaloneWindows64"),
                new BuildTargetPayload("Android", true, "Android"),
                new BuildTargetPayload("iOS", true, "iOS"),
                new BuildTargetPayload("WebGL", true, "WebGL"),
                new BuildTargetPayload("macOS", true, "StandaloneOSX"),
                new BuildTargetPayload("Linux", true, "StandaloneLinux64")
            ]),
            _jsonOptions);
        return new ProjectCommandResponseDto(
            false,
            $"{StubbedBridgePrefix} build-targets metadata is synthetic in Host mode; attach Bridge mode for authoritative build support state",
            "build-targets",
            payload);
    }

    private ProjectCommandResponseDto HandleBuildCancelStub()
    {
        return new ProjectCommandResponseDto(
            true,
            "build cancel acknowledged (stubbed host mode had no active build worker)",
            "build",
            null);
    }

    private ProjectCommandResponseDto HandleValidatePackages()
    {
        var diagnostics = new List<ValidateDiagnostic>();
        var manifestPath = Path.Combine(_projectPath, "Packages", "manifest.json");
        var lockPath = Path.Combine(_projectPath, "Packages", "packages-lock.json");

        if (!File.Exists(manifestPath))
        {
            diagnostics.Add(new ValidateDiagnostic(
                ValidateSeverity.Error, "VPK001", "Packages/manifest.json not found",
                AssetPath: "Packages/manifest.json"));
        }
        else
        {
            try
            {
                using var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var deps = manifestDoc.RootElement.TryGetProperty("dependencies", out var depsEl)
                    ? depsEl : (JsonElement?)null;

                if (deps is null || deps.Value.ValueKind != JsonValueKind.Object)
                {
                    diagnostics.Add(new ValidateDiagnostic(
                        ValidateSeverity.Warning, "VPK002", "manifest.json has no dependencies block",
                        AssetPath: "Packages/manifest.json"));
                }
                else if (File.Exists(lockPath))
                {
                    using var lockDoc = JsonDocument.Parse(File.ReadAllText(lockPath));
                    var locked = lockDoc.RootElement.TryGetProperty("dependencies", out var lockDeps)
                        ? lockDeps : (JsonElement?)null;

                    if (locked is not null && locked.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var dep in deps.Value.EnumerateObject())
                        {
                            if (!locked.Value.TryGetProperty(dep.Name, out var lockEntry))
                            {
                                diagnostics.Add(new ValidateDiagnostic(
                                    ValidateSeverity.Warning, "VPK003",
                                    $"package '{dep.Name}' in manifest.json but missing from packages-lock.json",
                                    AssetPath: "Packages/manifest.json", Fixable: true));
                                continue;
                            }

                            var requestedVersion = dep.Value.GetString() ?? "";
                            if (lockEntry.TryGetProperty("version", out var lockedVersion))
                            {
                                var lockedStr = lockedVersion.GetString() ?? "";
                                // Warn when manifest requests a specific version that differs from resolved
                                if (!string.IsNullOrEmpty(requestedVersion)
                                    && !requestedVersion.StartsWith("file:", StringComparison.Ordinal)
                                    && !requestedVersion.StartsWith("git", StringComparison.OrdinalIgnoreCase)
                                    && !requestedVersion.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                    && requestedVersion != lockedStr
                                    && !lockedStr.StartsWith(requestedVersion, StringComparison.Ordinal))
                                {
                                    diagnostics.Add(new ValidateDiagnostic(
                                        ValidateSeverity.Info, "VPK004",
                                        $"package '{dep.Name}': manifest requests '{requestedVersion}', lock resolved '{lockedStr}'",
                                        AssetPath: "Packages/manifest.json"));
                                }
                            }
                        }
                    }
                }
                else
                {
                    diagnostics.Add(new ValidateDiagnostic(
                        ValidateSeverity.Warning, "VPK005", "packages-lock.json not found — run Unity to regenerate",
                        AssetPath: "Packages/packages-lock.json", Fixable: true));
                }
            }
            catch (JsonException ex)
            {
                diagnostics.Add(new ValidateDiagnostic(
                    ValidateSeverity.Error, "VPK006", $"failed to parse package files: {ex.Message}",
                    AssetPath: "Packages/manifest.json"));
            }
        }

        var errorCount = diagnostics.Count(d => d.Severity == ValidateSeverity.Error);
        var warningCount = diagnostics.Count(d => d.Severity == ValidateSeverity.Warning);
        var result = new ValidateResult("packages", errorCount == 0, errorCount, warningCount, diagnostics);
        var json = JsonSerializer.Serialize(result, _jsonOptions);
        return new ProjectCommandResponseDto(true, errorCount == 0 ? "packages valid" : $"packages: {errorCount} error(s), {warningCount} warning(s)", "validate", json);
    }

    private ProjectCommandResponseDto HandleValidateAsmdef()
    {
        var diagnostics = new List<ValidateDiagnostic>();
        var assetsPath = Path.Combine(_projectPath, "Assets");

        if (!Directory.Exists(assetsPath))
        {
            diagnostics.Add(new ValidateDiagnostic(ValidateSeverity.Error, "VASD001", "Assets/ directory not found"));
            var r0 = new ValidateResult("asmdef", false, 1, 0, diagnostics);
            return new ProjectCommandResponseDto(false, "asmdef: 1 error(s)", "validate", JsonSerializer.Serialize(r0, _jsonOptions));
        }

        var asmdefFiles = Directory.GetFiles(assetsPath, "*.asmdef", SearchOption.AllDirectories);
        var nameToPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var allNames = new HashSet<string>(StringComparer.Ordinal);

        // First pass: build name → path map and detect duplicates
        foreach (var file in asmdefFiles)
        {
            string? name = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("name", out var nameProp))
                    name = nameProp.GetString();
            }
            catch { /* skip unreadable */ }

            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!allNames.Add(name))
            {
                diagnostics.Add(new ValidateDiagnostic(ValidateSeverity.Error, "VASD002",
                    $"duplicate assembly name '{name}'", AssetPath: GetRelativePath(file)));
                continue;
            }

            nameToPath[name] = file;
        }

        // Second pass: check references and build adjacency for cycle detection
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in asmdefFiles)
        {
            string? name = null;
            string[]? refs = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("name", out var nameProp))
                    name = nameProp.GetString();
                if (doc.RootElement.TryGetProperty("references", out var refsProp) && refsProp.ValueKind == JsonValueKind.Array)
                {
                    refs = refsProp.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString() ?? "")
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToArray();
                }
            }
            catch { continue; }

            if (string.IsNullOrWhiteSpace(name) || !nameToPath.ContainsKey(name))
                continue;

            var neighbors = new List<string>();
            foreach (var refName in refs ?? Array.Empty<string>())
            {
                if (!nameToPath.ContainsKey(refName))
                {
                    diagnostics.Add(new ValidateDiagnostic(ValidateSeverity.Warning, "VASD003",
                        $"assembly '{name}' references undefined assembly '{refName}'",
                        AssetPath: GetRelativePath(file)));
                }
                else
                {
                    neighbors.Add(refName);
                }
            }

            adjacency[name] = neighbors;
        }

        // Cycle detection via DFS
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var inStack = new HashSet<string>(StringComparer.Ordinal);
        var cycleReported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var startName in adjacency.Keys)
        {
            if (!visited.Contains(startName))
                DfsCycleDetect(startName, adjacency, visited, inStack, cycleReported, diagnostics);
        }

        var errorCount = diagnostics.Count(d => d.Severity == ValidateSeverity.Error);
        var warningCount = diagnostics.Count(d => d.Severity == ValidateSeverity.Warning);
        var result = new ValidateResult("asmdef", errorCount == 0, errorCount, warningCount, diagnostics);
        var json = JsonSerializer.Serialize(result, _jsonOptions);
        return new ProjectCommandResponseDto(true, errorCount == 0 ? "asmdef valid" : $"asmdef: {errorCount} error(s), {warningCount} warning(s)", "validate", json);
    }

    private static void DfsCycleDetect(
        string node,
        Dictionary<string, List<string>> adjacency,
        HashSet<string> visited,
        HashSet<string> inStack,
        HashSet<string> cycleReported,
        List<ValidateDiagnostic> diagnostics)
    {
        visited.Add(node);
        inStack.Add(node);

        if (adjacency.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                if (!visited.Contains(neighbor))
                {
                    DfsCycleDetect(neighbor, adjacency, visited, inStack, cycleReported, diagnostics);
                }
                else if (inStack.Contains(neighbor) && cycleReported.Add(neighbor))
                {
                    diagnostics.Add(new ValidateDiagnostic(ValidateSeverity.Error, "VASD004",
                        $"circular dependency detected: '{node}' -> '{neighbor}'"));
                }
            }
        }

        inStack.Remove(node);
    }

    private string GetRelativePath(string absolutePath)
    {
        if (absolutePath.StartsWith(_projectPath, StringComparison.Ordinal))
        {
            var rel = absolutePath[_projectPath.Length..].TrimStart(Path.DirectorySeparatorChar, '/');
            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }
        return absolutePath;
    }

    private ProjectCommandResponseDto HandleBuildSnapshotPackages()
    {
        var manifestPath = Path.Combine(_projectPath, "Packages", "manifest.json");
        var lockPath = Path.Combine(_projectPath, "Packages", "packages-lock.json");

        if (!File.Exists(manifestPath))
        {
            return new ProjectCommandResponseDto(false, "Packages/manifest.json not found", null, null);
        }

        var packages = new List<SnapshotPackageEntry>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in deps.EnumerateObject())
                {
                    packages.Add(new SnapshotPackageEntry(prop.Name, prop.Value.GetString() ?? ""));
                }
            }
        }
        catch (Exception ex)
        {
            return new ProjectCommandResponseDto(false, $"failed to read manifest: {ex.Message}", null, null);
        }

        var lockfilePresent = File.Exists(lockPath);
        var timestamp = DateTime.UtcNow;
        var timestampStr = timestamp.ToString("yyyyMMdd-HHmmss");
        var snapshotsDir = Path.Combine(_projectPath, ".unifocl-runtime", "snapshots");
        Directory.CreateDirectory(snapshotsDir);
        var snapshotPath = Path.Combine(snapshotsDir, $"packages-{timestampStr}.json");

        var snapshotPayload = new
        {
            timestamp = timestamp.ToString("O"),
            packageCount = packages.Count,
            packages = packages.Select(p => new { name = p.Name, version = p.Version }).ToArray(),
            lockfilePresent,
            snapshotPath
        };

        try
        {
            File.WriteAllText(snapshotPath, JsonSerializer.Serialize(snapshotPayload, _jsonOptions));
        }
        catch (Exception ex)
        {
            return new ProjectCommandResponseDto(false, $"failed to write snapshot: {ex.Message}", null, null);
        }

        var result = new BuildSnapshotResult(snapshotPath, timestamp.ToString("O"), packages.Count, lockfilePresent);
        var resultJson = JsonSerializer.Serialize(result, _jsonOptions);
        return new ProjectCommandResponseDto(true, $"snapshot written: {snapshotPath}", "build-snapshot", resultJson);
    }

    private sealed record SnapshotPackageEntry(string Name, string Version);

    private ProjectCommandResponseDto RequireBridgeMode(string action)
    {
        return new ProjectCommandResponseDto(
            false,
            $"{StubbedBridgePrefix} {action} requires Bridge mode",
            null,
            null);
    }

    private static bool IsValidAssetPath(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath) || assetPath.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return assetPath.Equals("Assets", StringComparison.Ordinal)
               || assetPath.Equals("Assets/", StringComparison.Ordinal)
               || assetPath.StartsWith("Assets/", StringComparison.Ordinal);
    }

    private string ResolveAssetPath(string assetPath)
    {
        return Path.GetFullPath(Path.Combine(_projectPath, assetPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static void CopyMetaIfPresent(string sourcePath, string targetPath)
    {
        var sourceMeta = sourcePath + ".meta";
        var targetMeta = targetPath + ".meta";
        if (!File.Exists(sourceMeta))
        {
            return;
        }

        var targetDirectory = Path.GetDirectoryName(targetMeta);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        File.Copy(sourceMeta, targetMeta, overwrite: true);
    }

    private static void MoveMetaIfPresent(string sourcePath, string targetPath)
    {
        var sourceMeta = sourcePath + ".meta";
        var targetMeta = targetPath + ".meta";
        if (!File.Exists(sourceMeta))
        {
            return;
        }

        if (File.Exists(targetMeta))
        {
            File.Delete(targetMeta);
        }

        File.Move(sourceMeta, targetMeta);
    }

    private static void DeleteMetaIfPresent(string absolutePath)
    {
        var metaPath = absolutePath + ".meta";
        if (File.Exists(metaPath))
        {
            File.Delete(metaPath);
        }
    }

    private string SerializeError(string message)
    {
        return JsonSerializer.Serialize(new ProjectCommandResponseDto(false, message, null, null), _jsonOptions);
    }

    private static bool TryValidateMutationIntent(MutationIntentDto? intent, out string error)
    {
        if (intent is null)
        {
            error = "mutation intent envelope is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(intent.TransactionId))
        {
            error = "mutation intent transactionId is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(intent.Target))
        {
            error = "mutation intent target is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(intent.Property))
        {
            error = "mutation intent property is required";
            return false;
        }

        if (!intent.Flags.RequireRollback)
        {
            error = "mutation intent must require rollback semantics";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string BuildStubAssetPath(string cwd, string type, string? baseName, int index, int count)
    {
        var ext = ResolveStubExtension(type);
        var resolvedBase = string.IsNullOrWhiteSpace(baseName)
            ? $"New{type}"
            : baseName.Trim();
        var finalName = count > 1 ? $"{resolvedBase}_{index + 1}" : resolvedBase;
        if (type.Equals("Folder", StringComparison.OrdinalIgnoreCase))
        {
            return $"{cwd.TrimEnd('/', '\\')}/{finalName}/".Replace('\\', '/');
        }

        return $"{cwd.TrimEnd('/', '\\')}/{finalName}{ext}".Replace('\\', '/');
    }

    private static string ResolveStubExtension(string type)
    {
        return ProjectMkCatalog.ResolveDefaultExtension(type);
    }

    private string EnsureUniqueStubAssetPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var isFolder = normalized.EndsWith("/", StringComparison.Ordinal);
        var trimmed = isFolder ? normalized.TrimEnd('/') : normalized;
        var directory = Path.GetDirectoryName(trimmed)?.Replace('\\', '/') ?? "Assets";
        var fileName = Path.GetFileName(trimmed);
        var stem = isFolder ? fileName : Path.GetFileNameWithoutExtension(fileName);
        var extension = isFolder ? string.Empty : Path.GetExtension(fileName);

        var candidateStem = stem;
        var suffix = 1;
        while (true)
        {
            var candidate = isFolder
                ? $"{directory}/{candidateStem}/"
                : $"{directory}/{candidateStem}{extension}";
            var absolutePath = ResolveAssetPath(candidate.TrimEnd('/'));
            if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
            {
                return candidate.Replace('\\', '/');
            }

            candidateStem = $"{stem}_{suffix++}";
        }
    }

    private sealed record MkAssetRequestPayload(string Type, int Count, string? Name);
    private sealed record MkAssetResponsePayload(List<string> CreatedPaths);
    private sealed record UpmListRequestPayload(bool IncludeOutdated, bool IncludeBuiltin, bool IncludeGit);
    private sealed record UpmListResponsePayload(List<UpmListPackagePayload>? Packages);
    private sealed record UpmListPackagePayload(
        string? PackageId,
        string? DisplayName,
        string? Version,
        string? Source,
        string? LatestCompatibleVersion,
        bool IsOutdated,
        bool IsDeprecated,
        bool IsPreview);
    private sealed record BuildTargetsResponsePayload(List<BuildTargetPayload> Targets);
    private sealed record BuildTargetPayload(string Name, bool Installed, string? Note);

    private static string ResolveUpmSource(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "Unknown";
        }

        if (version.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return "Local";
        }

        if (version.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
            || version.Contains(".git", StringComparison.OrdinalIgnoreCase)
            || version.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || version.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "Git";
        }

        return "Registry";
    }
}
