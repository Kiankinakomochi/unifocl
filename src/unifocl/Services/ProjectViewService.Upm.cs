using System.Text.Json;
using Spectre.Console;

internal sealed partial class ProjectViewService
{
    private async Task<ProjectCommandResponseDto> ExecuteUpmMutationWithRecoveryAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        ProjectCommandRequestDto request,
        List<string> outputs)
    {
        var response = await ExecuteProjectCommandAsync(session, request);
        if (response.Ok)
        {
            return response;
        }

        if (ProjectViewServiceUtils.DidResponseChannelInterruptAfterCompletion(response.Message))
        {
            outputs.Add("[yellow]upm[/]: command completed in daemon, but response channel was interrupted; verifying package state");
            return response;
        }

        if (!ProjectViewServiceUtils.ShouldRecoverUpmTimeout(response.Message))
        {
            return response;
        }

        if (ProjectViewServiceUtils.DidDaemonRuntimeRestart(response.Message))
        {
            outputs.Add("[yellow]upm[/]: daemon runtime restarted during command; retrying once on refreshed runtime");
        }
        else
        {
            outputs.Add("[yellow]upm[/]: timed out while daemon is reachable; restarting bridge and retrying once");
            await daemonControlService.HandleDaemonCommandAsync(
                input: "/daemon restart",
                trigger: "/daemon restart",
                runtime: daemonRuntime,
                session: session,
                log: line => outputs.Add(line),
                streamLog: outputs);
        }

        var bridgeReady = await EnsureModeContextAsync(session, daemonControlService, daemonRuntime, requireBridgeMode: true);
        if (!bridgeReady)
        {
            return new ProjectCommandResponseDto(
                false,
                "bridge restart after UPM timeout did not recover project command endpoint",
                null,
                null);
        }

        var retried = await ExecuteProjectCommandAsync(session, request);
        if (!retried.Ok && !string.IsNullOrWhiteSpace(retried.Message))
        {
            return new ProjectCommandResponseDto(
                false,
                $"{retried.Message} (after automatic bridge restart retry)",
                retried.Kind,
                retried.Content);
        }

        return retried;
    }

    private async Task<(bool Confirmed, string? Version)> TryConfirmUpmUpdateSucceededAsync(
        CliSessionState session,
        string packageId,
        string? expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(packageId) || !ProjectViewServiceUtils.IsRegistryPackageId(packageId))
        {
            return (false, null);
        }

        var payload = JsonSerializer.Serialize(
            new UpmListRequestPayload(false, true, false),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(1200);
            }

            var response = await ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("upm-list", null, null, payload));
            if (!response.Ok)
            {
                continue;
            }

            var current = ProjectViewServiceUtils.TryFindUpmPackageById(response.Content, packageId);
            if (current is null)
            {
                continue;
            }

            var installedVersion = string.IsNullOrWhiteSpace(current.Version) ? null : current.Version!;
            if (!string.IsNullOrWhiteSpace(expectedVersion)
                && string.Equals(installedVersion, expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                return (true, installedVersion);
            }

            if (!current.IsOutdated)
            {
                return (true, installedVersion);
            }
        }

        return (false, null);
    }

    private async Task<bool> HandleUpmCommandAsync(
        CliSessionState session,
        IReadOnlyList<string> tokens,
        List<string> outputs,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime)
    {
        if (tokens.Count < 2)
        {
            outputs.Add("[x] usage: upm <list|ls> [--outdated] [--builtin] [--git]");
            outputs.Add("[x] usage: upm <install|add|i> <target>");
            outputs.Add("[x] usage: upm <remove|rm|uninstall> <id>");
            outputs.Add("[x] usage: upm <update|u> [id]");
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
            if (!ProjectViewServiceUtils.TryNormalizeUpmInstallTarget(rawTarget, out var target, out var targetType, out var validationError))
            {
                outputs.Add($"[x] upm install failed: {validationError}");
                outputs.Add("accepted targets:");
                outputs.Add("- registry ID (com.unity.addressables)");
                outputs.Add("- git URL (https://github.com/user/repo.git?path=/subfolder#v1.0.0)");
                outputs.Add("- local path (file:../local-pkg)");
                return true;
            }

            var installBridgeReady = await RunTrackableProgressAsync(
                session,
                "preparing package manager",
                TimeSpan.FromSeconds(6),
                () => EnsureModeContextAsync(session, daemonControlService, daemonRuntime, requireBridgeMode: true));
            if (!installBridgeReady)
            {
                outputs.Add("[x] upm install failed: Bridge mode is unavailable; set UNITY_PATH or open Unity editor for this project");
                return true;
            }

            if (targetType.Equals("registry", StringComparison.OrdinalIgnoreCase))
            {
                var (registryPackageId, _) = ProjectViewServiceUtils.SplitRegistryTarget(target);
                var listResponse = await RunTrackableProgressAsync(
                    session,
                    "checking installed packages",
                    TimeSpan.FromSeconds(8),
                    () => ExecuteProjectCommandAsync(
                        session,
                        new ProjectCommandRequestDto(
                            "upm-list",
                            null,
                            null,
                            JsonSerializer.Serialize(
                                new UpmListRequestPayload(false, true, false),
                                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))));
                if (!listResponse.Ok)
                {
                    outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("upm install", listResponse.Message));
                    return true;
                }

                var existingPackage = ProjectViewServiceUtils.TryFindUpmPackageById(listResponse.Content, registryPackageId);
                if (existingPackage is not null)
                {
                    var existingPackageId = string.IsNullOrWhiteSpace(existingPackage.PackageId) ? registryPackageId : existingPackage.PackageId!;
                    var existingPackageVersion = string.IsNullOrWhiteSpace(existingPackage.Version) ? "-" : existingPackage.Version!;
                    RenderFrame(session.ProjectView);
                    var cleanInstallRequested = Console.IsInputRedirected
                        ? false
                        : AnsiConsole.Confirm(
                            $"Package [white]{Markup.Escape(existingPackageId)}[/] is already installed ([white]{Markup.Escape(existingPackageVersion)}[/]). Run clean install (remove then install)?",
                            defaultValue: false);
                    if (!cleanInstallRequested)
                    {
                        outputs.Add($"[i] install skipped: {Markup.Escape(existingPackageId)} is already installed");
                        return true;
                    }

                    var removePayload = JsonSerializer.Serialize(
                        new UpmRemoveRequestPayload(existingPackageId),
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    var removeResponse = await RunTrackableProgressAsync(
                        session,
                        $"clean uninstalling {existingPackageId}",
                        TimeSpan.FromSeconds(25),
                        () => ExecuteUpmMutationWithRecoveryAsync(
                            session,
                            daemonControlService,
                            daemonRuntime,
                            new ProjectCommandRequestDto("upm-remove", null, null, removePayload),
                            outputs));
                    if (!removeResponse.Ok)
                    {
                        outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("upm clean remove", removeResponse.Message));
                        return true;
                    }

                    outputs.Add($"[i] clean remove complete: {Markup.Escape(existingPackageId)}");
                }
            }

            var installPayload = JsonSerializer.Serialize(
                new UpmInstallRequestPayload(target),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var installResponse = await RunTrackableProgressAsync(
                session,
                $"installing {target}",
                TimeSpan.FromSeconds(35),
                () => ExecuteUpmMutationWithRecoveryAsync(
                    session,
                    daemonControlService,
                    daemonRuntime,
                    new ProjectCommandRequestDto("upm-install", null, null, installPayload),
                    outputs));

            if (!installResponse.Ok)
            {
                outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("upm install", installResponse.Message));
                return true;
            }

            UpmInstallResponsePayload? installParsed = null;
            if (!string.IsNullOrWhiteSpace(installResponse.Content))
            {
                try
                {
                    installParsed = JsonSerializer.Deserialize<UpmInstallResponsePayload>(
                        installResponse.Content,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                }
            }

            var installedId = string.IsNullOrWhiteSpace(installParsed?.PackageId) ? target : installParsed.PackageId!;
            var installedVersion = string.IsNullOrWhiteSpace(installParsed?.Version) ? null : installParsed.Version!;
            var source = string.IsNullOrWhiteSpace(installParsed?.Source) ? null : installParsed.Source!;
            var resolvedTargetType = string.IsNullOrWhiteSpace(installParsed?.TargetType) ? targetType : installParsed.TargetType!;
            outputs.Add(installedVersion is null
                ? $"[+] installed package: {Markup.Escape(installedId)}"
                : $"[+] installed package: {Markup.Escape(installedId)} v{Markup.Escape(installedVersion)}");
            if (!string.IsNullOrWhiteSpace(source))
            {
                outputs.Add($"[i] source: {Markup.Escape(source)}");
            }

            outputs.Add($"[i] target type: {Markup.Escape(resolvedTargetType)}");
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

            var removeBridgeReady = await RunTrackableProgressAsync(
                session,
                "preparing package manager",
                TimeSpan.FromSeconds(6),
                () => EnsureModeContextAsync(session, daemonControlService, daemonRuntime, requireBridgeMode: true));
            if (!removeBridgeReady)
            {
                outputs.Add("[x] upm remove failed: Bridge mode is unavailable; set UNITY_PATH or open Unity editor for this project");
                return true;
            }

            var removePayload = JsonSerializer.Serialize(
                new UpmRemoveRequestPayload(packageId),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var removeResponse = await RunTrackableProgressAsync(
                session,
                $"removing {packageId}",
                TimeSpan.FromSeconds(20),
                () => ExecuteUpmMutationWithRecoveryAsync(
                    session,
                    daemonControlService,
                    daemonRuntime,
                    new ProjectCommandRequestDto("upm-remove", null, null, removePayload),
                    outputs));
            if (!removeResponse.Ok)
            {
                outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("upm remove", removeResponse.Message));
                return true;
            }

            outputs.Add($"[+] removed package: {Markup.Escape(packageId)}");
            return true;
        }

        if (subcommand.Equals("update", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("u", StringComparison.OrdinalIgnoreCase))
        {
            var updateBridgeReady = await RunTrackableProgressAsync(
                session,
                "preparing package manager",
                TimeSpan.FromSeconds(6),
                () => EnsureModeContextAsync(session, daemonControlService, daemonRuntime, requireBridgeMode: true));
            if (!updateBridgeReady)
            {
                outputs.Add("[x] upm update failed: Bridge mode is unavailable; set UNITY_PATH or open Unity editor for this project");
                return true;
            }

            var updateListPayload = JsonSerializer.Serialize(
                new UpmListRequestPayload(false, true, false),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var updateListResponse = await RunTrackableProgressAsync(
                session,
                "reading package state",
                TimeSpan.FromSeconds(10),
                () => ExecuteProjectCommandAsync(
                    session,
                    new ProjectCommandRequestDto("upm-list", null, null, updateListPayload)));
            if (!updateListResponse.Ok)
            {
                outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("upm update", updateListResponse.Message));
                return true;
            }

            var updatePackages = ProjectViewServiceUtils.TryParseUpmPackages(updateListResponse.Content);
            if (updatePackages is null)
            {
                outputs.Add("[x] upm update failed: invalid package payload");
                return true;
            }

            var requestedId = tokens.Count >= 3
                ? ProjectViewServiceUtils.NormalizeLoadSelector(string.Join(' ', tokens.Skip(2)))
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(requestedId))
            {
                var target = updatePackages.FirstOrDefault(p =>
                    !string.IsNullOrWhiteSpace(p.PackageId)
                    && p.PackageId.Equals(requestedId, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    outputs.Add($"[x] upm update failed: package not installed: {Markup.Escape(requestedId)}");
                    return true;
                }

                if (target.Source?.Equals("BuiltIn", StringComparison.OrdinalIgnoreCase) == true)
                {
                    outputs.Add($"[i] update skipped: built-in package cannot be updated: {Markup.Escape(requestedId)}");
                    return true;
                }

                if (!target.IsOutdated)
                {
                    outputs.Add($"[i] already up to date: {Markup.Escape(requestedId)}");
                    return true;
                }

                var singleUpdatePayload = JsonSerializer.Serialize(
                    new UpmInstallRequestPayload(ProjectViewServiceUtils.ComposeRegistryInstallTarget(requestedId, target.LatestCompatibleVersion)),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var singleUpdateResponse = await RunTrackableProgressAsync(
                    session,
                    $"updating {requestedId}",
                    TimeSpan.FromSeconds(30),
                    () => ExecuteUpmMutationWithRecoveryAsync(
                        session,
                        daemonControlService,
                        daemonRuntime,
                        new ProjectCommandRequestDto("upm-install", null, null, singleUpdatePayload),
                        outputs));
                if (!singleUpdateResponse.Ok)
                {
                    var verified = await TryConfirmUpmUpdateSucceededAsync(
                        session,
                        requestedId,
                        target.LatestCompatibleVersion);
                    if (verified.Confirmed)
                    {
                        var confirmedOldVersion = string.IsNullOrWhiteSpace(target.Version) ? "-" : target.Version!;
                        var confirmedVersion = string.IsNullOrWhiteSpace(verified.Version)
                            ? ProjectViewServiceUtils.ResolveUpmUpdatedVersion(singleUpdateResponse.Content, target.LatestCompatibleVersion)
                            : verified.Version!;
                        outputs.Add("[i] update command timed out, but package state confirms success");
                        outputs.Add($"[+] updated package: {Markup.Escape(requestedId)} [grey]v{Markup.Escape(confirmedOldVersion)} -> v{Markup.Escape(confirmedVersion)}[/]");
                        return true;
                    }

                    outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("upm update", singleUpdateResponse.Message));
                    return true;
                }

                var oldVersion = string.IsNullOrWhiteSpace(target.Version) ? "-" : target.Version!;
                var newVersion = ProjectViewServiceUtils.ResolveUpmUpdatedVersion(singleUpdateResponse.Content, target.LatestCompatibleVersion);
                outputs.Add($"[+] updated package: {Markup.Escape(requestedId)} [grey]v{Markup.Escape(oldVersion)} -> v{Markup.Escape(newVersion)}[/]");
                return true;
            }

            var outdated = updatePackages
                .Where(p => p.IsOutdated
                            && !string.IsNullOrWhiteSpace(p.PackageId)
                            && !string.Equals(p.Source, "BuiltIn", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (outdated.Count == 0)
            {
                outputs.Add("[i] all packages are already up to date");
                return true;
            }

            outputs.Add($"[*] updating {outdated.Count} outdated package(s) safely");
            var successCount = 0;
            var failureCount = 0;
            foreach (var package in outdated)
            {
                var packageId = package.PackageId!;
                var bulkPayload = JsonSerializer.Serialize(
                    new UpmInstallRequestPayload(ProjectViewServiceUtils.ComposeRegistryInstallTarget(packageId, package.LatestCompatibleVersion)),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var bulkResponse = await RunTrackableProgressAsync(
                    session,
                    $"updating {packageId}",
                    TimeSpan.FromSeconds(30),
                    () => ExecuteUpmMutationWithRecoveryAsync(
                        session,
                        daemonControlService,
                        daemonRuntime,
                        new ProjectCommandRequestDto("upm-install", null, null, bulkPayload),
                        outputs));
                if (bulkResponse.Ok)
                {
                    successCount++;
                    var oldVersion = string.IsNullOrWhiteSpace(package.Version) ? "-" : package.Version!;
                    var newVersion = ProjectViewServiceUtils.ResolveUpmUpdatedVersion(bulkResponse.Content, package.LatestCompatibleVersion);
                    outputs.Add($"[+] updated: {Markup.Escape(packageId)} [grey]v{Markup.Escape(oldVersion)} -> v{Markup.Escape(newVersion)}[/]");
                }
                else
                {
                    var verified = await TryConfirmUpmUpdateSucceededAsync(
                        session,
                        packageId,
                        package.LatestCompatibleVersion);
                    if (verified.Confirmed)
                    {
                        successCount++;
                        var oldVersion = string.IsNullOrWhiteSpace(package.Version) ? "-" : package.Version!;
                        var confirmedVersion = string.IsNullOrWhiteSpace(verified.Version)
                            ? ProjectViewServiceUtils.ResolveUpmUpdatedVersion(bulkResponse.Content, package.LatestCompatibleVersion)
                            : verified.Version!;
                        outputs.Add("[i] update command timed out, but package state confirms success");
                        outputs.Add($"[+] updated: {Markup.Escape(packageId)} [grey]v{Markup.Escape(oldVersion)} -> v{Markup.Escape(confirmedVersion)}[/]");
                    }
                    else
                    {
                        failureCount++;
                        outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure($"upm update {packageId}", bulkResponse.Message));
                    }
                }
            }

            outputs.Add($"[i] update summary: success={successCount}, failed={failureCount}");
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

        var bridgeModeReady = await RunTrackableProgressAsync(
            session,
            "preparing package manager",
            TimeSpan.FromSeconds(6),
            () => EnsureModeContextAsync(session, daemonControlService, daemonRuntime, requireBridgeMode: true));
        if (!bridgeModeReady)
        {
            outputs.Add("[x] upm list failed: Bridge mode is unavailable; set UNITY_PATH or open Unity editor for this project");
            return true;
        }

        var payload = JsonSerializer.Serialize(
            new UpmListRequestPayload(includeOutdatedOnly, includeBuiltin, includeGitOnly),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var response = await RunTrackableProgressAsync(
            session,
            "loading package information",
            TimeSpan.FromSeconds(12),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("upm-list", null, null, payload)));

        if (!response.Ok)
        {
            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("upm list", response.Message));
            return true;
        }

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            outputs.Add("[x] upm list failed: daemon returned empty package payload");
            return true;
        }

        UpmListResponsePayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<UpmListResponsePayload>(
                response.Content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            outputs.Add($"[x] upm list failed: invalid package payload ({ex.Message})");
            return true;
        }

        var indexedPackages = (parsed?.Packages ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.PackageId))
            .OrderBy(p => ProjectViewServiceUtils.ResolvePackageDisplayName(p.DisplayName, p.PackageId!), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
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

        session.ProjectView.LastUpmPackages.Clear();
        session.ProjectView.LastUpmPackages.AddRange(indexedPackages);

        outputs.Add($"{indexedPackages.Count} package(s)");
        if (indexedPackages.Count == 0)
        {
            outputs.Add("no packages matched the selected filters");
            return true;
        }

        foreach (var package in indexedPackages)
        {
            var statusColor = ProjectViewServiceUtils.ResolveUpmStatusColor(package);
            var statusLabel = ProjectViewServiceUtils.ResolveUpmStatusLabel(package);
            outputs.Add(
                $"[{CliTheme.TextSecondary}]{package.Index}.[/] {Markup.Escape(package.DisplayName)} ({Markup.Escape(package.PackageId)}) v{Markup.Escape(package.Version)} [{CliTheme.TextSecondary}]{Markup.Escape(package.Source)}[/] [{statusColor}]{Markup.Escape(statusLabel)}[/]");
        }

        return true;
    }
}
