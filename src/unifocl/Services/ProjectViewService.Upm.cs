using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;

internal sealed partial class ProjectViewService
{
    private static readonly HttpClient UpmRegistryHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private async Task<bool> HandleUpmCommandAsync(
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
            outputs.Add("[x] usage: upm <list|ls> [--outdated] [--builtin] [--git]");
            outputs.Add("[x] usage: upm <install|add|i> <target>");
            outputs.Add("[x] usage: upm <remove|rm|uninstall> <id>");
            outputs.Add("[x] usage: upm <update|u> <packageId> [version]");
            return true;
        }

        if (!TryResolveProjectPath(session, out var projectPath, out var pathError))
        {
            outputs.Add($"[x] upm failed: {pathError}");
            return true;
        }

        var subcommand = tokens[1];
        session.ProjectView.ExpandTranscriptForUpmList =
            subcommand.Equals("list", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("ls", StringComparison.OrdinalIgnoreCase);

        if (subcommand.Equals("install", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("add", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("i", StringComparison.OrdinalIgnoreCase))
        {
            var rawTarget = tokens.Count >= 3
                ? string.Join(' ', tokens.Skip(2))
                : string.Empty;
            var installResult = await RunTrackableProgressAsync(
                session,
                "editing Packages/manifest.json",
                TimeSpan.FromSeconds(6),
                () => InstallDependencyFromManifestTargetAsync(projectPath, rawTarget));

            if (!installResult.Ok)
            {
                outputs.Add($"[x] upm install failed: {installResult.Message}");
                outputs.Add("manifest mode notes:");
                outputs.Add("- registry install requires explicit version (com.unity.addressables@1.21.19)");
                outputs.Add("- git install requires package id hint (<package-id>=https://...repo.git#tag)");
                outputs.Add("- local file install may infer package id from file: path package.json");
                return true;
            }

            outputs.Add(installResult.UpdatedExisting
                ? $"[+] updated package: {Markup.Escape(installResult.PackageId)}"
                : $"[+] installed package: {Markup.Escape(installResult.PackageId)}");
            if (!string.IsNullOrWhiteSpace(installResult.Version))
            {
                outputs.Add($"[i] version: {Markup.Escape(installResult.Version)}");
            }

            if (!string.IsNullOrWhiteSpace(installResult.Hash))
            {
                outputs.Add($"[i] resolved hash: {Markup.Escape(installResult.Hash)}");
            }

            outputs.Add($"[i] source: {Markup.Escape(installResult.Source)}");
            outputs.Add($"[i] target type: {Markup.Escape(installResult.TargetType)}");
            outputs.Add($"[i] lock graph: direct={installResult.DirectCount}, transitive={installResult.TransitiveCount}");
            return true;
        }

        if (subcommand.Equals("remove", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("rm", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("uninstall", StringComparison.OrdinalIgnoreCase))
        {
            var rawId = tokens.Count >= 3
                ? string.Join(' ', tokens.Skip(2))
                : string.Empty;
            var packageId = ProjectViewServiceUtils.NormalizeLoadSelector(rawId);
            if (!ProjectViewServiceUtils.IsRegistryPackageId(packageId))
            {
                outputs.Add("[x] upm remove failed: package id is required (e.g., com.unity.addressables)");
                return true;
            }

            var impact = await RunTrackableProgressAsync(
                session,
                "analyzing package dependency impact",
                TimeSpan.FromSeconds(10),
                () => AnalyzeRemoveDependencyImpactAsync(projectPath, packageId));
            if (!impact.Ok)
            {
                outputs.Add($"[x] upm remove failed: {impact.Message}");
                return true;
            }

            if (impact.Dependents.Count > 0)
            {
                outputs.Add($"[yellow]warning[/]: {Markup.Escape(packageId)} is required by:");
                foreach (var dependent in impact.Dependents)
                {
                    outputs.Add($"- {Markup.Escape(dependent)}");
                }

                if (Console.IsInputRedirected)
                {
                    outputs.Add("[x] removal canceled in non-interactive mode; remove dependent packages first or run interactively to confirm");
                    return true;
                }

                RenderFrame(session.ProjectView);
                var continueRemove = AnsiConsole.Confirm(
                    $"Package [white]{Markup.Escape(packageId)}[/] is used by other packages. Remove anyway?",
                    defaultValue: false);
                if (!continueRemove)
                {
                    outputs.Add("[i] remove canceled");
                    return true;
                }
            }

            var removeResult = await RunTrackableProgressAsync(
                session,
                "editing Packages/manifest.json",
                TimeSpan.FromSeconds(6),
                () => RemoveDependencyFromManifestAsync(projectPath, packageId));
            if (!removeResult.Ok)
            {
                outputs.Add($"[x] upm remove failed: {removeResult.Message}");
                return true;
            }

            outputs.Add($"[+] removed package: {Markup.Escape(packageId)}");
            outputs.Add($"[i] lock graph: direct={removeResult.DirectCount}, transitive={removeResult.TransitiveCount}");
            return true;
        }

        if (subcommand.Equals("update", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("u", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Count < 3)
            {
                outputs.Add("[x] usage: upm update <packageId> [version]");
                outputs.Add("example latest: upm update com.unity.addressables");
                outputs.Add("example pinned: upm update com.unity.addressables 1.21.19");
                return true;
            }

            var packageId = ProjectViewServiceUtils.NormalizeLoadSelector(tokens[2]);
            if (!ProjectViewServiceUtils.IsRegistryPackageId(packageId))
            {
                outputs.Add("[x] upm update failed: package id is required (e.g., com.unity.addressables)");
                return true;
            }

            var explicitVersion = tokens.Count >= 4
                ? ProjectViewServiceUtils.NormalizeLoadSelector(tokens[3])
                : string.Empty;
            string rawTarget;
            if (!string.IsNullOrWhiteSpace(explicitVersion))
            {
                var exactVersionResult = await RunTrackableProgressAsync(
                    session,
                    "validating requested version",
                    TimeSpan.FromSeconds(8),
                    () => ResolveRegistryInstallTargetForExactVersionAsync(projectPath, packageId, explicitVersion));
                if (!exactVersionResult.Ok)
                {
                    outputs.Add($"[x] upm update failed: {exactVersionResult.Message}");
                    return true;
                }

                rawTarget = exactVersionResult.Target;
                outputs.Add($"[i] using requested version: {Markup.Escape(explicitVersion)}");
            }
            else
            {
                var latestResult = await RunTrackableProgressAsync(
                    session,
                    "checking latest registry version",
                    TimeSpan.FromSeconds(8),
                    () => ResolveRegistryLatestInstallTargetAsync(projectPath, packageId));
                if (!latestResult.Ok)
                {
                    outputs.Add($"[x] upm update failed: {latestResult.Message}");
                    return true;
                }

                rawTarget = latestResult.Target;
                outputs.Add($"[i] resolved latest: {Markup.Escape(rawTarget)}");
            }

            var updateResult = await RunTrackableProgressAsync(
                session,
                "editing Packages/manifest.json",
                TimeSpan.FromSeconds(6),
                () => InstallDependencyFromManifestTargetAsync(projectPath, rawTarget));
            if (!updateResult.Ok)
            {
                outputs.Add($"[x] upm update failed: {updateResult.Message}");
                return true;
            }

            var label = updateResult.UpdatedExisting ? "updated" : "installed";
            outputs.Add($"[+] {label} package: {Markup.Escape(updateResult.PackageId)}");
            if (!string.IsNullOrWhiteSpace(updateResult.Version))
            {
                outputs.Add($"[i] version: {Markup.Escape(updateResult.Version)}");
            }

            if (!string.IsNullOrWhiteSpace(updateResult.Hash))
            {
                outputs.Add($"[i] resolved hash: {Markup.Escape(updateResult.Hash)}");
            }

            outputs.Add($"[i] lock graph: direct={updateResult.DirectCount}, transitive={updateResult.TransitiveCount}");
            return true;
        }

        if (!subcommand.Equals("list", StringComparison.OrdinalIgnoreCase)
            && !subcommand.Equals("ls", StringComparison.OrdinalIgnoreCase))
        {
            outputs.Add($"[x] unsupported upm subcommand: {subcommand}");
            outputs.Add("supported: upm list (alias: upm ls), upm install (aliases: upm add, upm i), upm remove (aliases: upm rm, upm uninstall), upm update (alias: upm u)");
            return true;
        }

        var includeOutdatedOnly = false;
        var includeBuiltin = false;
        var includeGitOnly = false;
        for (var i = 2; i < tokens.Count; i++)
        {
            var option = tokens[i];
            if (option.Equals("--outdated", StringComparison.OrdinalIgnoreCase))
            {
                includeOutdatedOnly = true;
                continue;
            }

            if (option.Equals("--builtin", StringComparison.OrdinalIgnoreCase))
            {
                includeBuiltin = true;
                continue;
            }

            if (option.Equals("--git", StringComparison.OrdinalIgnoreCase))
            {
                includeGitOnly = true;
                continue;
            }

            outputs.Add($"[x] unsupported flag: {option}");
            outputs.Add("supported flags: --outdated --builtin --git");
            return true;
        }

        var listResult = await RunTrackableProgressAsync(
            session,
            "reading package lock",
            TimeSpan.FromSeconds(includeOutdatedOnly ? 18 : 5),
            () => ListPackagesFromLockOrManifestAsync(projectPath, includeOutdatedOnly, includeBuiltin, includeGitOnly));
        if (!listResult.Ok)
        {
            outputs.Add($"[x] upm list failed: {listResult.Message}");
            return true;
        }

        session.ProjectView.LastUpmPackages.Clear();
        session.ProjectView.LastUpmPackages.AddRange(listResult.Packages);

        if (!string.IsNullOrWhiteSpace(listResult.Message))
        {
            outputs.Add($"[i] {Markup.Escape(listResult.Message)}");
        }

        outputs.Add($"{listResult.Packages.Count} package(s)");
        if (listResult.Packages.Count == 0)
        {
            outputs.Add("no packages matched the selected filters");
            if (includeOutdatedOnly)
            {
                outputs.Add("[i] manifest strategy cannot infer latest compatible versions; --outdated is empty by design");
            }

            return true;
        }

        foreach (var package in listResult.Packages)
        {
            var statusColor = ProjectViewServiceUtils.ResolveUpmStatusColor(package);
            var statusLabel = ProjectViewServiceUtils.ResolveUpmStatusLabel(package);
            outputs.Add(
                $"[{CliTheme.TextSecondary}]{package.Index}.[/] {Markup.Escape(package.DisplayName)} ({Markup.Escape(package.PackageId)}) v{Markup.Escape(package.Version)} [{CliTheme.TextSecondary}]{Markup.Escape(package.Source)}[/] [{statusColor}]{Markup.Escape(statusLabel)}[/]");
        }

        return true;
    }

    private static bool TryResolveProjectPath(CliSessionState session, out string projectPath, out string error)
    {
        projectPath = session.CurrentProjectPath ?? string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            error = "project path is missing; open a Unity project first with /open";
            return false;
        }

        if (!Directory.Exists(projectPath))
        {
            error = $"project path does not exist: {projectPath}";
            return false;
        }

        return true;
    }

    private static async Task<UpmMutationResult> InstallDependencyFromManifestTargetAsync(string projectPath, string rawTarget)
    {
        var beforeLock = CapturePackagesLockState(projectPath);
        if (!TryLoadManifest(projectPath, out var manifestPath, out var root, out var dependencies, out var loadError))
        {
            return UpmMutationResult.Fail(loadError);
        }

        if (!TryResolveInstallSpec(projectPath, rawTarget, out var spec, out var targetError))
        {
            return UpmMutationResult.Fail(targetError);
        }

        var updatedExisting = dependencies.ContainsKey(spec.PackageId);
        dependencies[spec.PackageId] = JsonValue.Create(spec.ManifestValue);
        var mutationAtUtc = DateTime.UtcNow;

        if (!TrySaveManifest(manifestPath, root, out var saveError))
        {
            return UpmMutationResult.Fail(saveError);
        }

        var lockValidation = await ValidatePackagesLockAfterMutationAsync(
            projectPath,
            beforeLock,
            mutationAtUtc,
            spec.PackageId,
            spec,
            expectRemoval: false);
        if (!lockValidation.Ok)
        {
            return UpmMutationResult.Fail(lockValidation.Message);
        }

        var resolvedVersion = string.IsNullOrWhiteSpace(lockValidation.ResolvedVersion)
            ? spec.DisplayVersion
            : lockValidation.ResolvedVersion;
        var resolvedSource = string.IsNullOrWhiteSpace(lockValidation.ResolvedSource)
            ? spec.Source
            : lockValidation.ResolvedSource;

        return UpmMutationResult.Success(
            spec.PackageId,
            resolvedVersion,
            resolvedSource,
            spec.TargetType,
            updatedExisting,
            lockValidation.ResolvedHash,
            lockValidation.DirectCount,
            lockValidation.TransitiveCount);
    }

    private static async Task<UpmMutationResult> RemoveDependencyFromManifestAsync(string projectPath, string packageId)
    {
        var beforeLock = CapturePackagesLockState(projectPath);
        if (!TryLoadManifest(projectPath, out var manifestPath, out var root, out var dependencies, out var loadError))
        {
            return UpmMutationResult.Fail(loadError);
        }

        if (!dependencies.Remove(packageId))
        {
            return UpmMutationResult.Fail($"package not found in manifest dependencies: {packageId}");
        }

        var mutationAtUtc = DateTime.UtcNow;
        if (!TrySaveManifest(manifestPath, root, out var saveError))
        {
            return UpmMutationResult.Fail(saveError);
        }

        var lockValidation = await ValidatePackagesLockAfterMutationAsync(
            projectPath,
            beforeLock,
            mutationAtUtc,
            packageId,
            expectedSpec: null,
            expectRemoval: true);
        if (!lockValidation.Ok)
        {
            return UpmMutationResult.Fail(lockValidation.Message);
        }

        return UpmMutationResult.Success(
            packageId,
            null,
            "manifest",
            "remove",
            updatedExisting: true,
            hash: null,
            lockValidation.DirectCount,
            lockValidation.TransitiveCount);
    }

    private static async Task<UpmListResult> ListPackagesFromLockOrManifestAsync(
        string projectPath,
        bool includeOutdatedOnly,
        bool includeBuiltin,
        bool includeGitOnly)
    {
        if (TryReadPackagesLockGraph(projectPath, out var graph, out var lockError))
        {
            var directEntries = graph.AllPackages
                .Where(entry => entry.Depth == 0)
                .Where(entry => includeBuiltin || !entry.Source.Equals("BuiltIn", StringComparison.OrdinalIgnoreCase))
                .Where(entry => !includeGitOnly || entry.Source.Equals("Git", StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var latestById = includeOutdatedOnly
                ? await FetchExternalLatestVersionsAsync(projectPath, directEntries)
                : new Dictionary<string, ExternalLatestInfo>(StringComparer.OrdinalIgnoreCase);

            var lockPackages = directEntries
                .Select((entry, index) =>
                {
                    latestById.TryGetValue(entry.PackageId, out var latest);
                    var isOutdated = latest is not null
                                     && latest.IsOutdated
                                     && !string.IsNullOrWhiteSpace(latest.LatestVersionOrTag);
                    return new UpmPackageEntry(
                        index,
                        entry.PackageId,
                        ProjectViewServiceUtils.ResolvePackageDisplayName(entry.PackageId, entry.PackageId),
                        string.IsNullOrWhiteSpace(entry.Version) ? "-" : entry.Version,
                        entry.Source,
                        latest?.LatestVersionOrTag,
                        isOutdated,
                        false,
                        entry.Version.Contains("preview", StringComparison.OrdinalIgnoreCase));
                })
                .Where(package => !includeOutdatedOnly || package.IsOutdated)
                .ToList();

            var summary = $"source=packages-lock.json, direct={graph.DirectCount}, transitive={graph.TransitiveCount}";
            return UpmListResult.Success(lockPackages, summary);
        }

        if (!TryLoadManifest(projectPath, out _, out _, out var dependencies, out var manifestError))
        {
            return UpmListResult.Fail(lockError ?? manifestError);
        }

        var mapped = new List<UpmListPackagePayload>();
        foreach (var dependency in dependencies)
        {
            if (dependency.Value is not JsonValue valueNode)
            {
                continue;
            }

            if (!valueNode.TryGetValue<string>(out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            var source = ResolveManifestSource(rawValue);
            if (!includeBuiltin && source.Equals("BuiltIn", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (includeGitOnly && !source.Equals("Git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (includeOutdatedOnly)
            {
                continue;
            }

            mapped.Add(new UpmListPackagePayload(
                dependency.Key,
                dependency.Key,
                rawValue,
                source,
                null,
                false,
                false,
                rawValue.Contains("preview", StringComparison.OrdinalIgnoreCase)));
        }

        var indexed = mapped
            .OrderBy(package => ProjectViewServiceUtils.ResolvePackageDisplayName(package.DisplayName, package.PackageId!), StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select((package, index) => new UpmPackageEntry(
                index,
                package.PackageId!,
                ProjectViewServiceUtils.ResolvePackageDisplayName(package.DisplayName, package.PackageId!),
                string.IsNullOrWhiteSpace(package.Version) ? "-" : package.Version!,
                string.IsNullOrWhiteSpace(package.Source) ? "unknown" : package.Source!,
                string.IsNullOrWhiteSpace(package.LatestCompatibleVersion) ? null : package.LatestCompatibleVersion,
                package.IsOutdated,
                package.IsDeprecated,
                package.IsPreview))
            .ToList();

        var fallbackMessage = includeOutdatedOnly
            ? "packages-lock.json not available; cannot perform external outdated checks"
            : "packages-lock.json not available; using manifest dependencies only";
        return UpmListResult.Success(indexed, fallbackMessage);
    }

    private static async Task<RegistryTargetResolutionResult> ResolveRegistryLatestInstallTargetAsync(
        string projectPath,
        string packageId)
    {
        if (!ProjectViewServiceUtils.IsRegistryPackageId(packageId))
        {
            return RegistryTargetResolutionResult.Fail("package id is invalid");
        }

        var scopedRegistries = LoadScopedRegistries(projectPath);
        var registryUrl = ResolveRegistryUrlForPackage(packageId, scopedRegistries);
        var latest = await TryFetchRegistryLatestVersionAsync(packageId, registryUrl);
        if (!latest.Ok || string.IsNullOrWhiteSpace(latest.LatestVersion))
        {
            return RegistryTargetResolutionResult.Fail(
                string.IsNullOrWhiteSpace(latest.Error)
                    ? $"failed to resolve latest version for {packageId}"
                    : latest.Error!);
        }

        return RegistryTargetResolutionResult.Success($"{packageId}@{latest.LatestVersion}");
    }

    private static async Task<RegistryTargetResolutionResult> ResolveRegistryInstallTargetForExactVersionAsync(
        string projectPath,
        string packageId,
        string requestedVersion)
    {
        if (!ProjectViewServiceUtils.IsRegistryPackageId(packageId))
        {
            return RegistryTargetResolutionResult.Fail("package id is invalid");
        }

        if (string.IsNullOrWhiteSpace(requestedVersion))
        {
            return RegistryTargetResolutionResult.Fail("requested version is empty");
        }

        var scopedRegistries = LoadScopedRegistries(projectPath);
        var registryUrl = ResolveRegistryUrlForPackage(packageId, scopedRegistries);
        var hasVersion = await TryFetchRegistryVersionExistsAsync(packageId, requestedVersion, registryUrl);
        if (!hasVersion.Ok)
        {
            return RegistryTargetResolutionResult.Fail(
                string.IsNullOrWhiteSpace(hasVersion.Error)
                    ? $"failed to validate version {requestedVersion} for {packageId}"
                    : hasVersion.Error!);
        }

        if (!hasVersion.Exists)
        {
            return RegistryTargetResolutionResult.Fail(
                $"requested version not found: {packageId}@{requestedVersion}");
        }

        return RegistryTargetResolutionResult.Success($"{packageId}@{requestedVersion}");
    }

    private static async Task<RemoveDependencyImpactResult> AnalyzeRemoveDependencyImpactAsync(
        string projectPath,
        string packageIdToRemove)
    {
        if (!TryReadPackagesLockGraph(projectPath, out var graph, out var lockError))
        {
            return RemoveDependencyImpactResult.Fail(
                string.IsNullOrWhiteSpace(lockError)
                    ? "packages-lock.json is required to analyze dependency impact"
                    : lockError!);
        }

        var directEntries = graph.AllPackages
            .Where(entry => entry.Depth == 0)
            .Where(entry => !entry.PackageId.Equals(packageIdToRemove, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (directEntries.Count == 0)
        {
            return RemoveDependencyImpactResult.Success([]);
        }

        var manifestDependencies = ReadManifestDependencyMap(projectPath);
        var scopedRegistries = LoadScopedRegistries(projectPath);
        var tasks = directEntries.Select(async entry =>
        {
            if (entry.Source.Equals("Registry", StringComparison.OrdinalIgnoreCase))
            {
                var registryUrl = ResolveRegistryUrlForPackage(entry.PackageId, scopedRegistries);
                var deps = await TryFetchRegistryDependenciesForVersionAsync(entry.PackageId, entry.Version, registryUrl);
                if (!deps.Ok || deps.Dependencies.Count == 0)
                {
                    return null;
                }

                if (deps.Dependencies.TryGetValue(packageIdToRemove, out var versionRange))
                {
                    var hint = string.IsNullOrWhiteSpace(versionRange) ? "*" : versionRange;
                    return $"{entry.PackageId} (requires {packageIdToRemove}@{hint})";
                }

                return null;
            }

            if (entry.Source.Equals("Local", StringComparison.OrdinalIgnoreCase)
                && manifestDependencies.TryGetValue(entry.PackageId, out var manifestTarget)
                && TryReadLocalPackageDependencies(projectPath, manifestTarget, out var localDeps, out _)
                && localDeps.TryGetValue(packageIdToRemove, out var localVersion))
            {
                var hint = string.IsNullOrWhiteSpace(localVersion) ? "*" : localVersion;
                return $"{entry.PackageId} (requires {packageIdToRemove}@{hint})";
            }

            return null;
        }).ToArray();

        var scanned = await Task.WhenAll(tasks);
        var dependents = scanned
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return RemoveDependencyImpactResult.Success(dependents);
    }

    private static async Task<Dictionary<string, ExternalLatestInfo>> FetchExternalLatestVersionsAsync(
        string projectPath,
        IReadOnlyList<PackagesLockEntry> directEntries)
    {
        var results = new Dictionary<string, ExternalLatestInfo>(StringComparer.OrdinalIgnoreCase);
        if (directEntries.Count == 0)
        {
            return results;
        }

        var scopedRegistries = LoadScopedRegistries(projectPath);
        var manifestDependencies = ReadManifestDependencyMap(projectPath);
        var tasks = directEntries.Select(async entry =>
        {
            if (entry.Source.Equals("Registry", StringComparison.OrdinalIgnoreCase))
            {
                var registryUrl = ResolveRegistryUrlForPackage(entry.PackageId, scopedRegistries);
                var fetched = await TryFetchRegistryLatestVersionAsync(entry.PackageId, registryUrl);
                if (!fetched.Ok || string.IsNullOrWhiteSpace(fetched.LatestVersion))
                {
                    return new ExternalLatestInfo(entry.PackageId, null, false, fetched.Error);
                }

                var cmp = CompareSemVer(entry.Version, fetched.LatestVersion!);
                return new ExternalLatestInfo(entry.PackageId, fetched.LatestVersion, cmp < 0, fetched.Error);
            }

            if (entry.Source.Equals("Git", StringComparison.OrdinalIgnoreCase))
            {
                if (!manifestDependencies.TryGetValue(entry.PackageId, out var manifestTarget)
                    || string.IsNullOrWhiteSpace(manifestTarget))
                {
                    return new ExternalLatestInfo(entry.PackageId, null, false, "manifest git target not found");
                }

                var gitUrl = NormalizeGitTargetForRemote(manifestTarget);
                if (string.IsNullOrWhiteSpace(gitUrl))
                {
                    return new ExternalLatestInfo(entry.PackageId, null, false, "invalid git target");
                }

                var gitLatest = await TryFetchGitLatestTagAsync(gitUrl!);
                if (!gitLatest.Ok || string.IsNullOrWhiteSpace(gitLatest.Tag))
                {
                    return new ExternalLatestInfo(entry.PackageId, null, false, gitLatest.Error);
                }

                var isOutdated = !string.IsNullOrWhiteSpace(entry.Hash)
                                 && !string.IsNullOrWhiteSpace(gitLatest.Hash)
                                 && !entry.Hash.Equals(gitLatest.Hash, StringComparison.OrdinalIgnoreCase);
                return new ExternalLatestInfo(entry.PackageId, gitLatest.Tag, isOutdated, gitLatest.Error);
            }

            return new ExternalLatestInfo(entry.PackageId, null, false, null);
        }).ToArray();

        var resolved = await Task.WhenAll(tasks);
        foreach (var item in resolved)
        {
            results[item.PackageId] = item;
        }

        return results;
    }

    private static Dictionary<string, string> ReadManifestDependencyMap(string projectPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryLoadManifest(projectPath, out _, out _, out var dependencies, out _))
        {
            return map;
        }

        foreach (var dependency in dependencies)
        {
            if (dependency.Value is not JsonValue valueNode)
            {
                continue;
            }

            if (!valueNode.TryGetValue<string>(out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            map[dependency.Key] = rawValue;
        }

        return map;
    }

    private static List<ScopedRegistryConfig> LoadScopedRegistries(string projectPath)
    {
        var manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("scopedRegistries", out var scopedRegistries)
                || scopedRegistries.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var resolved = new List<ScopedRegistryConfig>();
            foreach (var item in scopedRegistries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!item.TryGetProperty("url", out var urlElement)
                    || urlElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var url = urlElement.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var scopes = new List<string>();
                if (item.TryGetProperty("scopes", out var scopesElement) && scopesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var scope in scopesElement.EnumerateArray())
                    {
                        if (scope.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var value = scope.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            scopes.Add(value!);
                        }
                    }
                }

                if (scopes.Count > 0)
                {
                    resolved.Add(new ScopedRegistryConfig(url!, scopes));
                }
            }

            return resolved;
        }
        catch
        {
            return [];
        }
    }

    private static string ResolveRegistryUrlForPackage(string packageId, IReadOnlyList<ScopedRegistryConfig> scopedRegistries)
    {
        var bestScopeLength = -1;
        var resolvedUrl = "https://packages.unity.com";
        foreach (var scopedRegistry in scopedRegistries)
        {
            foreach (var scope in scopedRegistry.Scopes)
            {
                var matches = packageId.Equals(scope, StringComparison.OrdinalIgnoreCase)
                              || packageId.StartsWith(scope + ".", StringComparison.OrdinalIgnoreCase);
                if (!matches || scope.Length <= bestScopeLength)
                {
                    continue;
                }

                bestScopeLength = scope.Length;
                resolvedUrl = scopedRegistry.Url;
            }
        }

        return resolvedUrl;
    }

    private static async Task<RegistryLatestResult> TryFetchRegistryLatestVersionAsync(string packageId, string registryUrl)
    {
        try
        {
            var endpoint = $"{registryUrl.TrimEnd('/')}/{Uri.EscapeDataString(packageId)}";
            using var response = await UpmRegistryHttpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return RegistryLatestResult.Fail($"registry lookup failed ({response.StatusCode}) for {packageId} at {registryUrl}");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("dist-tags", out var distTags)
                || distTags.ValueKind != JsonValueKind.Object
                || !distTags.TryGetProperty("latest", out var latest)
                || latest.ValueKind != JsonValueKind.String)
            {
                return RegistryLatestResult.Fail($"registry metadata is missing dist-tags.latest for {packageId}");
            }

            var latestVersion = latest.GetString();
            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                return RegistryLatestResult.Fail($"registry latest version is empty for {packageId}");
            }

            return RegistryLatestResult.Success(latestVersion!);
        }
        catch (Exception ex)
        {
            return RegistryLatestResult.Fail($"registry lookup exception for {packageId}: {ex.Message}");
        }
    }

    private static async Task<RegistryDependencyResult> TryFetchRegistryDependenciesForVersionAsync(
        string packageId,
        string version,
        string registryUrl)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
        {
            return RegistryDependencyResult.Fail("package id/version is missing");
        }

        try
        {
            var endpoint = $"{registryUrl.TrimEnd('/')}/{Uri.EscapeDataString(packageId)}";
            using var response = await UpmRegistryHttpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return RegistryDependencyResult.Fail(
                    $"registry dependency lookup failed ({response.StatusCode}) for {packageId}@{version}");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("versions", out var versions)
                || versions.ValueKind != JsonValueKind.Object
                || !versions.TryGetProperty(version, out var selectedVersion)
                || selectedVersion.ValueKind != JsonValueKind.Object)
            {
                return RegistryDependencyResult.Success([]);
            }

            if (!selectedVersion.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object)
            {
                return RegistryDependencyResult.Success([]);
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in dependencies.EnumerateObject())
            {
                if (dependency.Value.ValueKind == JsonValueKind.String)
                {
                    map[dependency.Name] = dependency.Value.GetString() ?? string.Empty;
                }
            }

            return RegistryDependencyResult.Success(map);
        }
        catch (Exception ex)
        {
            return RegistryDependencyResult.Fail(
                $"registry dependency lookup exception for {packageId}@{version}: {ex.Message}");
        }
    }

    private static async Task<RegistryVersionExistsResult> TryFetchRegistryVersionExistsAsync(
        string packageId,
        string version,
        string registryUrl)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
        {
            return RegistryVersionExistsResult.Fail("package id/version is missing");
        }

        try
        {
            var endpoint = $"{registryUrl.TrimEnd('/')}/{Uri.EscapeDataString(packageId)}";
            using var response = await UpmRegistryHttpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return RegistryVersionExistsResult.Fail(
                    $"registry lookup failed ({response.StatusCode}) for {packageId} at {registryUrl}");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("versions", out var versions)
                || versions.ValueKind != JsonValueKind.Object)
            {
            return RegistryVersionExistsResult.Success(exists: false);
            }

            return RegistryVersionExistsResult.Success(versions.TryGetProperty(version, out _));
        }
        catch (Exception ex)
        {
            return RegistryVersionExistsResult.Fail(
                $"registry version lookup exception for {packageId}@{version}: {ex.Message}");
        }
    }

    private static bool TryReadLocalPackageDependencies(
        string projectPath,
        string manifestTarget,
        out Dictionary<string, string> dependencies,
        out string error)
    {
        dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(manifestTarget)
            || !manifestTarget.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            error = "manifest target is not a file package";
            return false;
        }

        var relativePath = manifestTarget["file:".Length..].Trim();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "file package path is empty";
            return false;
        }

        var absolutePath = Path.GetFullPath(
            Path.Combine(projectPath, "Packages", relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var packageJsonPath = Path.Combine(absolutePath, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            error = $"package.json not found at {absolutePath}";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (!document.RootElement.TryGetProperty("dependencies", out var depsElement)
                || depsElement.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            foreach (var dependency in depsElement.EnumerateObject())
            {
                if (dependency.Value.ValueKind == JsonValueKind.String)
                {
                    dependencies[dependency.Name] = dependency.Value.GetString() ?? string.Empty;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to parse local package.json: {ex.Message}";
            return false;
        }
    }

    private static string? NormalizeGitTargetForRemote(string manifestTarget)
    {
        if (string.IsNullOrWhiteSpace(manifestTarget))
        {
            return null;
        }

        var normalized = manifestTarget.Trim();
        if (normalized.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["git+".Length..];
        }

        var fragmentIndex = normalized.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            normalized = normalized[..fragmentIndex];
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static async Task<GitLatestResult> TryFetchGitLatestTagAsync(string gitUrl)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("ls-remote");
            process.StartInfo.ArgumentList.Add("--tags");
            process.StartInfo.ArgumentList.Add("--refs");
            process.StartInfo.ArgumentList.Add(gitUrl);

            if (!process.Start())
            {
                return GitLatestResult.Fail($"failed to start git for {gitUrl}");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await process.WaitForExitAsync(timeout.Token);
            var stdOut = await process.StandardOutput.ReadToEndAsync();
            var stdErr = await process.StandardError.ReadToEndAsync();
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stdErr) ? $"exit={process.ExitCode}" : stdErr.Trim();
                return GitLatestResult.Fail($"git ls-remote failed for {gitUrl}: {detail}");
            }

            var tags = stdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line =>
                {
                    var columns = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (columns.Length != 2 || !columns[1].StartsWith("refs/tags/", StringComparison.Ordinal))
                    {
                        return null;
                    }

                    var hash = columns[0];
                    var tag = columns[1]["refs/tags/".Length..];
                    return string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(hash)
                        ? null
                        : new GitTagRef(tag, hash);
                })
                .Where(tag => tag is not null)
                .Select(tag => tag!)
                .ToList();
            if (tags.Count == 0)
            {
                return GitLatestResult.Fail($"no tags found in {gitUrl}");
            }

            var latest = tags
                .OrderByDescending(tag => tag.Tag, SemVerStringComparer.Instance)
                .First();
            return GitLatestResult.Success(latest.Tag, latest.Hash);
        }
        catch (OperationCanceledException)
        {
            return GitLatestResult.Fail($"git ls-remote timed out for {gitUrl}");
        }
        catch (Exception ex)
        {
            return GitLatestResult.Fail($"git ls-remote exception for {gitUrl}: {ex.Message}");
        }
    }

    private static int CompareSemVer(string? left, string? right)
        => SemVerStringComparer.Instance.Compare(left ?? string.Empty, right ?? string.Empty);

    private static bool TryResolveInstallSpec(
        string projectPath,
        string rawTarget,
        out UpmInstallSpec spec,
        out string error)
    {
        spec = new UpmInstallSpec(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        error = string.Empty;

        var normalizedInput = ProjectViewServiceUtils.NormalizeLoadSelector(rawTarget);
        if (string.IsNullOrWhiteSpace(normalizedInput))
        {
            error = "missing target";
            return false;
        }

        string? packageHint = null;
        var target = normalizedInput;
        var assignIndex = normalizedInput.IndexOf('=');
        if (assignIndex > 0)
        {
            packageHint = normalizedInput[..assignIndex].Trim();
            target = assignIndex + 1 < normalizedInput.Length
                ? normalizedInput[(assignIndex + 1)..].Trim()
                : string.Empty;
            if (!ProjectViewServiceUtils.IsRegistryPackageId(packageHint))
            {
                error = "package hint must be a valid package id when using <package-id>=<target>";
                return false;
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                error = "target is required after '='";
                return false;
            }
        }

        if (!ProjectViewServiceUtils.TryNormalizeUpmInstallTarget(target, out var normalizedTarget, out var targetType, out var validationError))
        {
            error = validationError;
            return false;
        }

        if (targetType.Equals("registry", StringComparison.OrdinalIgnoreCase))
        {
            var (packageId, version) = ProjectViewServiceUtils.SplitRegistryTarget(normalizedTarget);
            if (string.IsNullOrWhiteSpace(version))
            {
                error = "manifest strategy requires explicit registry version (e.g., com.unity.addressables@1.21.19)";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(packageHint)
                && !packageHint.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            {
                error = "package hint must match registry package id";
                return false;
            }

            spec = new UpmInstallSpec(packageId, version, "registry", "Registry", version);
            return true;
        }

        if (targetType.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(packageHint))
            {
                error = "git target requires package id hint: <package-id>=https://...repo.git#tag";
                return false;
            }

            spec = new UpmInstallSpec(packageHint, normalizedTarget, "git", "Git", normalizedTarget);
            return true;
        }

        if (targetType.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            var packageId = packageHint;
            if (string.IsNullOrWhiteSpace(packageId)
                && !TryResolvePackageIdFromFileTarget(projectPath, normalizedTarget, out packageId, out _))
            {
                error = "unable to infer package id from file target; use <package-id>=file:...";
                return false;
            }

            spec = new UpmInstallSpec(packageId!, normalizedTarget, "file", "Local", normalizedTarget);
            return true;
        }

        error = "unsupported target type";
        return false;
    }

    private static bool TryResolvePackageIdFromFileTarget(
        string projectPath,
        string target,
        out string packageId,
        out string error)
    {
        packageId = string.Empty;
        error = string.Empty;

        if (!target.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            error = "target is not a file: path";
            return false;
        }

        var pathPart = target["file:".Length..].Trim();
        if (string.IsNullOrWhiteSpace(pathPart))
        {
            error = "file target path is empty";
            return false;
        }

        var baseDirectory = Path.Combine(projectPath, "Packages");
        var absolutePath = Path.GetFullPath(Path.Combine(baseDirectory, pathPart.Replace('/', Path.DirectorySeparatorChar)));
        var packageJsonPath = Path.Combine(absolutePath, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            error = $"package.json not found at {absolutePath}";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (!document.RootElement.TryGetProperty("name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String)
            {
                error = "package.json does not contain string 'name'";
                return false;
            }

            var name = nameElement.GetString();
            if (!ProjectViewServiceUtils.IsRegistryPackageId(name ?? string.Empty))
            {
                error = "package.json name is not a valid UPM package id";
                return false;
            }

            packageId = name!;
            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to parse package.json: {ex.Message}";
            return false;
        }
    }

    private static bool TryLoadManifest(
        string projectPath,
        out string manifestPath,
        out JsonObject root,
        out JsonObject dependencies,
        out string error)
    {
        manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
        root = new JsonObject();
        dependencies = new JsonObject();
        error = string.Empty;

        try
        {
            if (File.Exists(manifestPath))
            {
                var parsed = JsonNode.Parse(File.ReadAllText(manifestPath));
                if (parsed is not JsonObject parsedObject)
                {
                    error = "manifest.json root must be a JSON object";
                    return false;
                }

                root = parsedObject;
            }

            if (root["dependencies"] is JsonObject existingDependencies)
            {
                dependencies = existingDependencies;
            }
            else
            {
                dependencies = new JsonObject();
                root["dependencies"] = dependencies;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to read Packages/manifest.json ({ex.Message})";
            return false;
        }
    }

    private static bool TrySaveManifest(string manifestPath, JsonObject root, out string error)
    {
        error = string.Empty;
        try
        {
            var parent = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var content = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(manifestPath, content + Environment.NewLine);
            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to write Packages/manifest.json ({ex.Message})";
            return false;
        }
    }

    private static async Task<UpmLockValidationResult> ValidatePackagesLockAfterMutationAsync(
        string projectPath,
        PackagesLockState beforeLock,
        DateTime mutationAtUtc,
        string requestedPackageId,
        UpmInstallSpec? expectedSpec,
        bool expectRemoval)
    {
        var timeout = ResolveEnvMilliseconds("UNIFOCL_UPM_LOCK_TIMEOUT_MS", 90_000, min: 5_000, max: 300_000);
        var grace = ResolveEnvMilliseconds("UNIFOCL_UPM_LOCK_GRACE_MS", 250, min: 50, max: 2_000);
        var poll = ResolveEnvMilliseconds("UNIFOCL_UPM_LOCK_POLL_MS", 250, min: 50, max: 1_000);

        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (DateTime.UtcNow <= deadline)
        {
            var current = CapturePackagesLockState(projectPath);
            if (DidPackagesLockChange(beforeLock, current))
            {
                await Task.Delay(grace);
                if (!TryReadPackagesLockGraph(projectPath, out var graph, out var readError))
                {
                    await Task.Delay(150);
                    if (!TryReadPackagesLockGraph(projectPath, out graph, out readError))
                    {
                        return UpmLockValidationResult.Fail($"packages-lock.json updated but could not be parsed: {readError}");
                    }
                }

                return ValidateResolvedGraph(graph, requestedPackageId, expectedSpec, expectRemoval);
            }

            await Task.Delay(poll);
        }

        var logHints = ReadUpmLogErrorHints(mutationAtUtc, maxLines: 4);
        var hintSuffix = logHints.Count == 0
            ? string.Empty
            : $" upm.log hints: {string.Join(" | ", logHints)}";
        return UpmLockValidationResult.Fail($"packages-lock.json did not update within {timeout}ms after manifest change.{hintSuffix}");
    }

    private static UpmLockValidationResult ValidateResolvedGraph(
        PackagesLockGraph graph,
        string requestedPackageId,
        UpmInstallSpec? expectedSpec,
        bool expectRemoval)
    {
        if (expectRemoval)
        {
            if (!graph.PackagesById.TryGetValue(requestedPackageId, out var removalEntry) || removalEntry.Depth > 0)
            {
                return UpmLockValidationResult.Success(null, null, null, graph.DirectCount, graph.TransitiveCount);
            }

            return UpmLockValidationResult.Fail($"removed package is still present as a direct dependency in packages-lock.json: {requestedPackageId}");
        }

        if (!graph.PackagesById.TryGetValue(requestedPackageId, out var entry))
        {
            return UpmLockValidationResult.Fail($"requested package was not resolved in packages-lock.json: {requestedPackageId}");
        }

        if (entry.Depth != 0)
        {
            return UpmLockValidationResult.Fail($"resolved package depth is {entry.Depth}, expected 0 for direct dependency: {requestedPackageId}");
        }

        if (expectedSpec is not null)
        {
            if (expectedSpec.TargetType.Equals("registry", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(entry.Version)
                    || !entry.Version.Equals(expectedSpec.DisplayVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return UpmLockValidationResult.Fail($"registry package version mismatch: expected {expectedSpec.DisplayVersion}, resolved {entry.Version}");
                }
            }

            if (expectedSpec.TargetType.Equals("git", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(entry.Hash))
            {
                return UpmLockValidationResult.Fail("git package resolved without commit hash in packages-lock.json");
            }
        }

        return UpmLockValidationResult.Success(
            entry.Version,
            entry.Source,
            entry.Hash,
            graph.DirectCount,
            graph.TransitiveCount);
    }

    private static PackagesLockState CapturePackagesLockState(string projectPath)
    {
        var lockPath = Path.Combine(projectPath, "Packages", "packages-lock.json");
        if (!File.Exists(lockPath))
        {
            return new PackagesLockState(false, DateTime.MinValue, 0);
        }

        var info = new FileInfo(lockPath);
        return new PackagesLockState(true, info.LastWriteTimeUtc, info.Length);
    }

    private static bool DidPackagesLockChange(PackagesLockState before, PackagesLockState after)
    {
        if (!before.Exists && after.Exists)
        {
            return true;
        }

        if (!before.Exists || !after.Exists)
        {
            return false;
        }

        return after.LastWriteTimeUtc > before.LastWriteTimeUtc || after.Length != before.Length;
    }

    private static bool TryReadPackagesLockGraph(string projectPath, out PackagesLockGraph graph, out string? error)
    {
        graph = new PackagesLockGraph([], new Dictionary<string, PackagesLockEntry>(StringComparer.OrdinalIgnoreCase), 0, 0);
        error = null;

        var lockPath = Path.Combine(projectPath, "Packages", "packages-lock.json");
        if (!File.Exists(lockPath))
        {
            error = "Packages/packages-lock.json not found";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
            if (!document.RootElement.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object)
            {
                error = "packages-lock.json does not contain dependencies object";
                return false;
            }

            var all = new List<PackagesLockEntry>();
            var map = new Dictionary<string, PackagesLockEntry>(StringComparer.OrdinalIgnoreCase);
            var directCount = 0;
            var transitiveCount = 0;

            foreach (var property in dependencies.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var version = TryGetString(property.Value, "version") ?? string.Empty;
                var sourceRaw = TryGetString(property.Value, "source") ?? "unknown";
                var source = NormalizeLockSource(sourceRaw);
                var hash = TryGetString(property.Value, "hash")
                           ?? TryGetString(property.Value, "revision")
                           ?? string.Empty;
                var depth = TryGetInt(property.Value, "depth") ?? 0;

                if (depth <= 0)
                {
                    directCount++;
                }
                else
                {
                    transitiveCount++;
                }

                var entry = new PackagesLockEntry(property.Name, version, source, hash, depth);
                all.Add(entry);
                map[property.Name] = entry;
            }

            graph = new PackagesLockGraph(all, map, directCount, transitiveCount);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static List<string> ReadUpmLogErrorHints(DateTime sinceUtc, int maxLines)
    {
        var path = ResolveUpmLogPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return [];
        }

        try
        {
            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc < sinceUtc.AddMinutes(-2))
            {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= 0)
            {
                return [];
            }

            var readLength = (int)Math.Min(stream.Length, 256 * 1024);
            stream.Seek(-readLength, SeekOrigin.End);
            var buffer = new byte[readLength];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead <= 0)
            {
                return [];
            }

            var tail = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
            return tail
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.Contains("[error]", StringComparison.OrdinalIgnoreCase)
                               || line.Contains("[warning]", StringComparison.OrdinalIgnoreCase))
                .TakeLast(Math.Max(1, maxLines))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string? ResolveUpmLogPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "Unity", "Editor", "upm.log");
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Logs", "Unity", "upm.log");
        }

        return Path.Combine(home, ".config", "unity3d", "upm.log");
    }

    private static int ResolveEnvMilliseconds(string name, int defaultValue, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (!int.TryParse(raw, out var parsed))
        {
            return defaultValue;
        }

        return Math.Clamp(parsed, min, max);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (!property.TryGetInt32(out var value))
        {
            return null;
        }

        return value;
    }

    private static string NormalizeLockSource(string rawSource)
    {
        if (rawSource.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            return "Git";
        }

        if (rawSource.Equals("registry", StringComparison.OrdinalIgnoreCase))
        {
            return "Registry";
        }

        if (rawSource.Equals("builtIn", StringComparison.OrdinalIgnoreCase)
            || rawSource.Equals("builtin", StringComparison.OrdinalIgnoreCase))
        {
            return "BuiltIn";
        }

        if (rawSource.Equals("embedded", StringComparison.OrdinalIgnoreCase)
            || rawSource.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            return "Local";
        }

        return rawSource;
    }

    private static string ResolveManifestSource(string version)
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

    private sealed record RegistryTargetResolutionResult(bool Ok, string Message, string Target)
    {
        public static RegistryTargetResolutionResult Fail(string message)
            => new(false, message, string.Empty);

        public static RegistryTargetResolutionResult Success(string target)
            => new(true, string.Empty, target);
    }

    private sealed record ScopedRegistryConfig(string Url, List<string> Scopes);
    private sealed record ExternalLatestInfo(string PackageId, string? LatestVersionOrTag, bool IsOutdated, string? Error);
    private sealed record RegistryLatestResult(bool Ok, string? LatestVersion, string? Error)
    {
        public static RegistryLatestResult Success(string version)
            => new(true, version, null);

        public static RegistryLatestResult Fail(string error)
            => new(false, null, error);
    }

    private sealed record RegistryDependencyResult(bool Ok, Dictionary<string, string> Dependencies, string? Error)
    {
        public static RegistryDependencyResult Success(Dictionary<string, string> dependencies)
            => new(true, dependencies, null);

        public static RegistryDependencyResult Fail(string error)
            => new(false, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), error);
    }

    private sealed record RegistryVersionExistsResult(bool Ok, bool Exists, string? Error)
    {
        public static RegistryVersionExistsResult Success(bool exists)
            => new(true, exists, null);

        public static RegistryVersionExistsResult Fail(string error)
            => new(false, false, error);
    }

    private sealed record RemoveDependencyImpactResult(bool Ok, string Message, List<string> Dependents)
    {
        public static RemoveDependencyImpactResult Success(List<string> dependents)
            => new(true, string.Empty, dependents);

        public static RemoveDependencyImpactResult Fail(string message)
            => new(false, message, []);
    }

    private sealed record GitLatestResult(bool Ok, string? Tag, string? Hash, string? Error)
    {
        public static GitLatestResult Success(string tag, string hash)
            => new(true, tag, hash, null);

        public static GitLatestResult Fail(string error)
            => new(false, null, null, error);
    }

    private sealed record GitTagRef(string Tag, string Hash);

    private sealed record UpmInstallSpec(
        string PackageId,
        string ManifestValue,
        string TargetType,
        string Source,
        string DisplayVersion);

    private sealed class SemVerStringComparer : IComparer<string>
    {
        public static readonly SemVerStringComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var left = Parse(x);
            var right = Parse(y);
            return left.CompareTo(right);
        }

        private static ParsedSemVer Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new ParsedSemVer([0, 0, 0], []);
            }

            var trimmed = raw.Trim();
            if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            {
                trimmed = trimmed[1..];
            }

            var plus = trimmed.IndexOf('+');
            if (plus >= 0)
            {
                trimmed = trimmed[..plus];
            }

            var dash = trimmed.IndexOf('-');
            var core = dash >= 0 ? trimmed[..dash] : trimmed;
            var pre = dash >= 0 && dash + 1 < trimmed.Length ? trimmed[(dash + 1)..] : string.Empty;

            var numbers = core
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Take(3)
                .Select(segment => int.TryParse(segment, out var value) ? value : 0)
                .ToList();
            while (numbers.Count < 3)
            {
                numbers.Add(0);
            }

            var prerelease = string.IsNullOrWhiteSpace(pre)
                ? []
                : pre.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            return new ParsedSemVer(numbers, prerelease);
        }

        private sealed record ParsedSemVer(List<int> Core, List<string> Prerelease) : IComparable<ParsedSemVer>
        {
            public int CompareTo(ParsedSemVer? other)
            {
                if (other is null)
                {
                    return 1;
                }

                for (var i = 0; i < 3; i++)
                {
                    var cmp = Core[i].CompareTo(other.Core[i]);
                    if (cmp != 0)
                    {
                        return cmp;
                    }
                }

                var leftHasPre = Prerelease.Count > 0;
                var rightHasPre = other.Prerelease.Count > 0;
                if (!leftHasPre && !rightHasPre)
                {
                    return 0;
                }

                if (!leftHasPre)
                {
                    return 1;
                }

                if (!rightHasPre)
                {
                    return -1;
                }

                var max = Math.Max(Prerelease.Count, other.Prerelease.Count);
                for (var i = 0; i < max; i++)
                {
                    if (i >= Prerelease.Count)
                    {
                        return -1;
                    }

                    if (i >= other.Prerelease.Count)
                    {
                        return 1;
                    }

                    var left = Prerelease[i];
                    var right = other.Prerelease[i];
                    var leftIsInt = int.TryParse(left, out var leftInt);
                    var rightIsInt = int.TryParse(right, out var rightInt);
                    if (leftIsInt && rightIsInt)
                    {
                        var cmp = leftInt.CompareTo(rightInt);
                        if (cmp != 0)
                        {
                            return cmp;
                        }

                        continue;
                    }

                    if (leftIsInt && !rightIsInt)
                    {
                        return -1;
                    }

                    if (!leftIsInt && rightIsInt)
                    {
                        return 1;
                    }

                    var lexical = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
                    if (lexical != 0)
                    {
                        return lexical;
                    }
                }

                return 0;
            }
        }
    }

    private sealed record PackagesLockState(
        bool Exists,
        DateTime LastWriteTimeUtc,
        long Length);

    private sealed record PackagesLockEntry(
        string PackageId,
        string Version,
        string Source,
        string Hash,
        int Depth);

    private sealed record PackagesLockGraph(
        List<PackagesLockEntry> AllPackages,
        Dictionary<string, PackagesLockEntry> PackagesById,
        int DirectCount,
        int TransitiveCount);

    private sealed record UpmLockValidationResult(
        bool Ok,
        string Message,
        string? ResolvedVersion,
        string? ResolvedSource,
        string? ResolvedHash,
        int DirectCount,
        int TransitiveCount)
    {
        public static UpmLockValidationResult Fail(string message)
            => new(false, message, null, null, null, 0, 0);

        public static UpmLockValidationResult Success(
            string? version,
            string? source,
            string? hash,
            int directCount,
            int transitiveCount)
            => new(true, string.Empty, version, source, hash, directCount, transitiveCount);
    }

    private sealed record UpmMutationResult(
        bool Ok,
        string Message,
        string PackageId,
        string? Version,
        string Source,
        string TargetType,
        bool UpdatedExisting,
        string? Hash,
        int DirectCount,
        int TransitiveCount)
    {
        public static UpmMutationResult Fail(string message)
            => new(false, message, string.Empty, null, string.Empty, string.Empty, false, null, 0, 0);

        public static UpmMutationResult Success(
            string packageId,
            string? version,
            string source,
            string targetType,
            bool updatedExisting,
            string? hash,
            int directCount,
            int transitiveCount)
            => new(true, string.Empty, packageId, version, source, targetType, updatedExisting, hash, directCount, transitiveCount);
    }

    private sealed record UpmListResult(bool Ok, string Message, List<UpmPackageEntry> Packages)
    {
        public static UpmListResult Fail(string message)
            => new(false, message, []);

        public static UpmListResult Success(List<UpmPackageEntry> packages, string message)
            => new(true, message, packages);
    }
}
