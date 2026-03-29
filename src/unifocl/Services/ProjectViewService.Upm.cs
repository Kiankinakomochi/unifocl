using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, string> UpmDisplayNameCache = new(StringComparer.OrdinalIgnoreCase);

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
        session.ProjectView.ExpandTranscriptForUpmList = false;

        if (subcommand.Equals("install", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("add", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("i", StringComparison.OrdinalIgnoreCase))
        {
            var rawTarget = tokens.Count >= 3
                ? string.Join(' ', tokens.Skip(2))
                : string.Empty;
            var installTargetResolution = await ResolveInstallTargetAsync(projectPath, rawTarget);
            if (!installTargetResolution.Ok)
            {
                outputs.Add($"[x] upm install failed: {installTargetResolution.Message}");
                return true;
            }

            if (installTargetResolution.UsedFriendlyName)
            {
                outputs.Add($"[i] resolved target: {Markup.Escape(installTargetResolution.Target)}");
            }

            var installResult = await RunTrackableProgressAsync(
                session,
                "editing Packages/manifest.json",
                TimeSpan.FromSeconds(6),
                () => InstallDependencyFromManifestTargetAsync(projectPath, installTargetResolution.Target));

            if (!installResult.Ok)
            {
                outputs.Add($"[x] upm install failed: {installResult.Message}");
                outputs.Add("manifest mode notes:");
                outputs.Add("- registry install supports package id or friendly name (version optional)");
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
                var resolved = await ResolvePackageIdFromFriendlyNameAsync(projectPath, rawId, preferInstalledOnly: true);
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    outputs.Add("[x] upm remove failed: package id or installed package friendly name is required (e.g., com.unity.addressables)");
                    return true;
                }

                packageId = resolved;
                outputs.Add($"[i] resolved package: {Markup.Escape(packageId)}");
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
                var resolved = await ResolvePackageIdFromFriendlyNameAsync(projectPath, tokens[2], preferInstalledOnly: false);
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    outputs.Add("[x] upm update failed: package id or package friendly name is required (e.g., com.unity.addressables)");
                    return true;
                }

                packageId = resolved;
                outputs.Add($"[i] resolved package: {Markup.Escape(packageId)}");
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
            "reading package manifest",
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
        if (!TryLoadManifest(projectPath, out _, out _, out var dependencies, out var manifestError))
        {
            return UpmListResult.Fail(manifestError);
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

        mapped = await ResolveFriendlyPackageDisplayNamesAsync(projectPath, mapped);

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

        var summary = includeOutdatedOnly
            ? "source=manifest.json; --outdated requires lock/registry resolution and is empty in strict manifest mode"
            : $"source=manifest.json, direct={indexed.Count}";
        return UpmListResult.Success(indexed, summary);
    }

    private static async Task<InstallTargetResolutionResult> ResolveInstallTargetAsync(string projectPath, string rawTarget)
    {
        var normalized = ProjectViewServiceUtils.NormalizeLoadSelector(rawTarget);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return InstallTargetResolutionResult.Fail("missing target");
        }

        if (ProjectViewServiceUtils.TryNormalizeUpmInstallTarget(
                normalized,
                out var normalizedTarget,
                out var targetType,
                out _))
        {
            if (!targetType.Equals("registry", StringComparison.OrdinalIgnoreCase))
            {
                return InstallTargetResolutionResult.Success(normalizedTarget, usedFriendlyName: false);
            }

            var (packageId, version) = ProjectViewServiceUtils.SplitRegistryTarget(normalizedTarget);
            if (!string.IsNullOrWhiteSpace(version))
            {
                return InstallTargetResolutionResult.Success(normalizedTarget, usedFriendlyName: false);
            }

            var latest = await ResolveRegistryLatestInstallTargetAsync(projectPath, packageId);
            if (!latest.Ok)
            {
                return InstallTargetResolutionResult.Fail(latest.Message);
            }

            return InstallTargetResolutionResult.Success(latest.Target, usedFriendlyName: false);
        }

        var (friendlyName, explicitVersion) = SplitFriendlyNameAndVersion(normalized);
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return InstallTargetResolutionResult.Fail("target must be package id, friendly name, Git URL, or file: path");
        }

        var resolvedPackageId = await ResolvePackageIdFromFriendlyNameAsync(projectPath, friendlyName, preferInstalledOnly: false);
        if (string.IsNullOrWhiteSpace(resolvedPackageId))
        {
            return InstallTargetResolutionResult.Fail($"could not resolve package from friendly name: {friendlyName}");
        }

        if (!string.IsNullOrWhiteSpace(explicitVersion))
        {
            var exactTarget = $"{resolvedPackageId}@{explicitVersion}";
            return InstallTargetResolutionResult.Success(exactTarget, usedFriendlyName: true);
        }

        var latestResult = await ResolveRegistryLatestInstallTargetAsync(projectPath, resolvedPackageId);
        if (!latestResult.Ok)
        {
            return InstallTargetResolutionResult.Fail(latestResult.Message);
        }

        return InstallTargetResolutionResult.Success(latestResult.Target, usedFriendlyName: true);
    }

    private static async Task<List<UpmListPackagePayload>> ResolveFriendlyPackageDisplayNamesAsync(
        string projectPath,
        List<UpmListPackagePayload> packages)
    {
        if (packages.Count == 0)
        {
            return packages;
        }

        var scopedRegistries = LoadScopedRegistries(projectPath);
        var tasks = packages.Select(async package =>
        {
            var packageId = package.PackageId?.Trim();
            if (string.IsNullOrWhiteSpace(packageId))
            {
                return package;
            }

            var source = package.Source ?? string.Empty;
            if (!source.Equals("Registry", StringComparison.OrdinalIgnoreCase))
            {
                return package;
            }

            var registryUrl = ResolveRegistryUrlForPackage(packageId, scopedRegistries);
            var cacheKey = $"{registryUrl.TrimEnd('/')}/{packageId}";
            if (!UpmDisplayNameCache.TryGetValue(cacheKey, out var displayName))
            {
                displayName = await TryFetchRegistryDisplayNameAsync(packageId, registryUrl);
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    UpmDisplayNameCache[cacheKey] = displayName;
                }
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                return package;
            }

            return package with { DisplayName = displayName };
        }).ToArray();

        var resolved = await Task.WhenAll(tasks);
        return resolved.ToList();
    }

    private static async Task<string?> ResolvePackageIdFromFriendlyNameAsync(
        string projectPath,
        string rawFriendlyName,
        bool preferInstalledOnly)
    {
        var friendlyName = ProjectViewServiceUtils.NormalizeLoadSelector(rawFriendlyName);
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return null;
        }

        if (ProjectViewServiceUtils.IsRegistryPackageId(friendlyName))
        {
            return friendlyName;
        }

        var normalizedFriendly = NormalizeFriendlyToken(friendlyName);
        if (string.IsNullOrWhiteSpace(normalizedFriendly))
        {
            return null;
        }

        var manifestDependencies = ReadManifestDependencyMap(projectPath);
        var scopedRegistries = LoadScopedRegistries(projectPath);
        foreach (var dependency in manifestDependencies)
        {
            var packageId = dependency.Key;
            if (NormalizeFriendlyToken(packageId).Equals(normalizedFriendly, StringComparison.Ordinal))
            {
                return packageId;
            }

            var source = ResolveManifestSource(dependency.Value);
            if (!source.Equals("Registry", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var registryUrl = ResolveRegistryUrlForPackage(packageId, scopedRegistries);
            var cacheKey = $"{registryUrl.TrimEnd('/')}/{packageId}";
            if (!UpmDisplayNameCache.TryGetValue(cacheKey, out var displayName))
            {
                displayName = await TryFetchRegistryDisplayNameAsync(packageId, registryUrl);
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    UpmDisplayNameCache[cacheKey] = displayName;
                }
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            if (NormalizeFriendlyToken(displayName).Equals(normalizedFriendly, StringComparison.Ordinal))
            {
                return packageId;
            }
        }

        if (preferInstalledOnly)
        {
            return null;
        }

        var registryUrls = scopedRegistries
            .Select(item => item.Url)
            .Append("https://packages.unity.com")
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var registryUrl in registryUrls)
        {
            var resolved = await TrySearchRegistryPackageIdByFriendlyNameAsync(registryUrl, friendlyName, normalizedFriendly);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
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

    private sealed record UpmInstallSpec(
        string PackageId,
        string ManifestValue,
        string TargetType,
        string Source,
        string DisplayVersion);

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

    private sealed record ManifestMutationValidationResult(
        bool Ok,
        string Message,
        int DirectCount,
        string? ResolvedVersion,
        string? ResolvedSource)
    {
        public static ManifestMutationValidationResult Fail(string message)
            => new(false, message, 0, null, null);

        public static ManifestMutationValidationResult Success(
            int directCount,
            string? resolvedVersion,
            string? resolvedSource)
            => new(true, string.Empty, directCount, resolvedVersion, resolvedSource);
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

    private sealed record InstallTargetResolutionResult(
        bool Ok,
        string Message,
        string Target,
        bool UsedFriendlyName)
    {
        public static InstallTargetResolutionResult Fail(string message)
            => new(false, message, string.Empty, false);

        public static InstallTargetResolutionResult Success(string target, bool usedFriendlyName)
            => new(true, string.Empty, target, usedFriendlyName);
    }

    private sealed record UpmListResult(bool Ok, string Message, List<UpmPackageEntry> Packages)
    {
        public static UpmListResult Fail(string message)
            => new(false, message, []);

        public static UpmListResult Success(List<UpmPackageEntry> packages, string message)
            => new(true, message, packages);
    }
}
