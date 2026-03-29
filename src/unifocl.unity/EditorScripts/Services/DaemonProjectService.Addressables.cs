#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static partial class DaemonProjectService
    {
        private const string AddressablesEditorAssemblyName = "Unity.Addressables.Editor";

        private static string ExecuteAddressablesCommand(ProjectCommandRequest request)
        {
            AddressablesCommandRequest? options;
            try
            {
                options = JsonUtility.FromJson<AddressablesCommandRequest>(request.content ?? string.Empty);
            }
            catch (Exception ex)
            {
                return BuildAddressablesError($"invalid addressables request payload: {ex.Message}");
            }

            if (options is null || string.IsNullOrWhiteSpace(options.operation))
            {
                return BuildAddressablesError("addressables operation is required");
            }

            var operation = options.operation.Trim().ToLowerInvariant();
            switch (operation)
            {
                case "init":
                    return ExecuteAddressablesInit();
                case "profile-list":
                    return ExecuteAddressablesProfileList();
                case "profile-set":
                    return ExecuteAddressablesProfileSet(options.name);
                case "group-list":
                    return ExecuteAddressablesGroupList();
                case "group-create":
                    return ExecuteAddressablesGroupCreate(options.name, options.setDefault);
                case "group-remove":
                    return ExecuteAddressablesGroupRemove(options.name);
                case "entry-add":
                    return ExecuteAddressablesEntryAdd(options.assetPath, options.groupName);
                case "entry-remove":
                    return ExecuteAddressablesEntryRemove(options.assetPath);
                case "entry-rename":
                    return ExecuteAddressablesEntryRename(options.assetPath, options.address);
                case "entry-label":
                    return ExecuteAddressablesEntryLabel(options.assetPath, options.label, options.remove);
                case "bulk-add":
                    return ExecuteAddressablesBulkAdd(options.folder, options.groupName, options.type);
                case "bulk-label":
                    return ExecuteAddressablesBulkLabel(options.folder, options.label, options.type, options.remove);
                case "analyze":
                    return ExecuteAddressablesAnalyze(options.duplicate);
                default:
                    return BuildAddressablesError($"unsupported addressables operation: {options.operation}");
            }
        }

        private static string ExecuteAddressablesInit()
        {
            if (!TryGetAddressableSettings(createIfMissing: true, out var settings, out var _, out var error))
            {
                return BuildAddressablesError(error ?? "failed to initialize Addressables settings");
            }

            MarkAddressablesDirty(settings!);
            return BuildAddressablesOk("Addressables settings are ready", "addressables-init");
        }

        private static string ExecuteAddressablesProfileList()
        {
            if (!TryGetAddressableSettings(createIfMissing: false, out var settings, out var _, out var error))
            {
                return BuildAddressablesError(error ?? "failed to load Addressables settings");
            }

            var profileSettings = GetPropertyValue(settings!, "profileSettings");
            if (profileSettings is null)
            {
                return BuildAddressablesError("Addressables profile settings are unavailable");
            }

            var activeProfileId = GetPropertyString(settings!, "activeProfileId");
            var profiles = EnumerateProfiles(profileSettings).ToList();
            var variableNames = GetProfileVariableNames(profileSettings).ToList();

            var response = new AddressablesProfileListResponse
            {
                activeProfileId = activeProfileId ?? string.Empty,
                activeProfileName = ResolveProfileName(profiles, activeProfileId),
                profileCount = profiles.Count,
                variableCount = variableNames.Count,
                profiles = profiles
                    .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                    .Select(profile => new AddressablesProfileDto
                    {
                        id = profile.id,
                        name = profile.name,
                        isActive = string.Equals(profile.id, activeProfileId, StringComparison.Ordinal),
                        variables = variableNames
                            .Select(variableName =>
                            {
                                var raw = ResolveProfileRawValue(profileSettings, profile.id, variableName);
                                return new AddressablesProfileVariableDto
                                {
                                    name = variableName,
                                    rawValue = raw,
                                    evaluatedValue = EvaluateProfileValue(profileSettings, profile.id, raw)
                                };
                            })
                            .OrderBy(variable => variable.name, StringComparer.OrdinalIgnoreCase)
                            .ToArray()
                    })
                    .ToArray()
            };

            return BuildAddressablesOk(
                $"listed {response.profileCount} profile(s)",
                "addressables-profile-list",
                JsonUtility.ToJson(response, true));
        }

        private static string ExecuteAddressablesProfileSet(string? profileName)
        {
            var normalized = (profileName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return BuildAddressablesError("profile name is required");
            }

            if (!TryGetAddressableSettings(createIfMissing: false, out var settings, out var _, out var error))
            {
                return BuildAddressablesError(error ?? "failed to load Addressables settings");
            }

            var profileSettings = GetPropertyValue(settings!, "profileSettings");
            if (profileSettings is null)
            {
                return BuildAddressablesError("Addressables profile settings are unavailable");
            }

            var profileId = ResolveProfileId(profileSettings, normalized);
            if (string.IsNullOrWhiteSpace(profileId))
            {
                return BuildAddressablesError($"profile not found: {normalized}");
            }

            if (!TrySetPropertyValue(settings!, "activeProfileId", profileId))
            {
                return BuildAddressablesError("failed to set active profile id");
            }

            MarkAddressablesDirty(settings!);
            return BuildAddressablesOk($"active profile set to {normalized}", "addressables-profile-set");
        }

        private static string ExecuteAddressablesGroupList()
        {
            if (!TryGetAddressableSettings(createIfMissing: false, out var settings, out var _, out var error))
            {
                return BuildAddressablesError(error ?? "failed to load Addressables settings");
            }

            var groups = EnumerateGroups(settings!).Select(group =>
            {
                var (packingMode, compression) = ResolveGroupPacking(group);
                return new AddressablesGroupDto
                {
                    name = GetUnityObjectName(group),
                    packingMode = packingMode,
                    compression = compression,
                    entryCount = EnumerateEntries(group).Count()
                };
            })
            .OrderBy(group => group.name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

            var response = new AddressablesGroupListResponse
            {
                count = groups.Length,
                groups = groups
            };
            return BuildAddressablesOk(
                $"listed {response.count} group(s)",
                "addressables-group-list",
                JsonUtility.ToJson(response, true));
        }

        private static string ExecuteAddressablesGroupCreate(string? groupName, bool setDefault)
        {
            var normalized = (groupName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return BuildAddressablesError("group name is required");
            }

            if (!TryGetAddressableSettings(createIfMissing: false, out var settings, out var _, out var error))
            {
                return BuildAddressablesError(error ?? "failed to load Addressables settings");
            }

            if (FindGroupByName(settings!, normalized) is not null)
            {
                return BuildAddressablesError($"group already exists: {normalized}");
            }

            var createGroupMethod = FindMethod(settings!.GetType(), "CreateGroup", 6);
            if (createGroupMethod is null)
            {
                return BuildAddressablesError("Addressables CreateGroup API is unavailable");
            }

            object? createdGroup;
            try
            {
                createdGroup = createGroupMethod.Invoke(
                    settings,
                    new object[] { normalized, false, false, false, null!, null! });
            }
            catch (Exception ex)
            {
                return BuildAddressablesError($"failed to create group: {ex.GetType().Name}: {ex.Message}");
            }

            if (createdGroup is null)
            {
                return BuildAddressablesError("group creation returned null");
            }

            if (setDefault)
            {
                if (!TrySetDefaultGroup(settings, createdGroup))
                {
                    return BuildAddressablesError("group created, but failed to set as default");
                }
            }

            MarkAddressablesDirty(settings);
            return BuildAddressablesOk($"created group: {normalized}", "addressables-group-create");
        }

        private static string ExecuteAddressablesGroupRemove(string? groupName)
        {
            var normalized = (groupName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return BuildAddressablesError("group name is required");
            }

            if (!TryGetAddressableSettings(createIfMissing: false, out var settings, out var _, out var error))
            {
                return BuildAddressablesError(error ?? "failed to load Addressables settings");
            }

            var group = FindGroupByName(settings!, normalized);
            if (group is null)
            {
                return BuildAddressablesError($"group not found: {normalized}");
            }

            var removedCount = 0;
            foreach (var entry in EnumerateEntries(group).ToList())
            {
                var guid = GetPropertyString(entry, "guid");
                if (string.IsNullOrWhiteSpace(guid))
                {
                    continue;
                }

                if (TryRemoveAssetEntry(settings!, guid))
                {
                    removedCount++;
                }
            }

            if (!TryRemoveGroup(settings!, group))
            {
                return BuildAddressablesError($"failed to remove group: {normalized}");
            }

            MarkAddressablesDirty(settings!);
            return BuildAddressablesOk(
                $"removed group: {normalized} (unmarked {removedCount} entries)",
                "addressables-group-remove");
        }

        private static string ExecuteAddressablesEntryAdd(string? assetPath, string? groupName)
        {
            var normalizedAssetPath = NormalizeAssetPath(assetPath);
            var normalizedGroup = (groupName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedAssetPath) || string.IsNullOrWhiteSpace(normalizedGroup))
            {
                return BuildAddressablesError("entry add requires assetPath and groupName");
            }

            if (!TryGetAddressableSettings(createIfMissing: false, out var settings, out var _, out var error))
            {
                return BuildAddressablesError(error ?? "failed to load Addressables settings");
            }

            var guid = AssetDatabase.AssetPathToGUID(normalizedAssetPath);
            if (string.IsNullOrWhiteSpace(guid))
            {
                return BuildAddressablesError($"asset was not found: {normalizedAssetPath}");
            }

            var group = FindGroupByName(settings!, normalizedGroup);
            if (group is null)
            {
                return BuildAddressablesError($"group not found: {normalizedGroup}");
            }

            var entry = TryCreateOrMoveEntry(settings!, guid, group);
            if (entry is null)
            {
                return BuildAddressablesError("failed to add/move Addressables entry");
            }

            MarkAddressablesDirty(settings!);
            return BuildAddressablesOk($"added entry: {normalizedAssetPath} -> {normalizedGroup}", "addressables-entry-add");
        }

        private static string ExecuteAddressablesEntryRemove(string? assetPath)
        {
            var normalizedAssetPath = NormalizeAssetPath(assetPath);
            if (string.IsNullOrWhiteSpace(normalizedAssetPath))
            {
                return BuildAddressablesError("entry remove requires assetPath");
            }

            if (!TryGetAddressableSettings(createIfMissing: false, out var settings, out var _, out var error))
            {
                return BuildAddressablesError(error ?? "failed to load Addressables settings");
            }

            var guid = AssetDatabase.AssetPathToGUID(normalizedAssetPath);
            if (string.IsNullOrWhiteSpace(guid))
            {
                return BuildAddressablesError($"asset was not found: {normalizedAssetPath}");
            }

            if (!TryRemoveAssetEntry(settings!, guid))
            {
                return BuildAddressablesError($"Addressables entry not found: {normalizedAssetPath}");
            }

            MarkAddressablesDirty(settings!);
            return BuildAddressablesOk($"removed entry: {normalizedAssetPath}", "addressables-entry-remove");
        }

        private static string ExecuteAddressablesEntryRename(string? assetPath, string? newAddress)
        {
            var normalizedAssetPath = NormalizeAssetPath(assetPath);
            var normalizedAddress = (newAddress ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedAssetPath) || string.IsNullOrWhiteSpace(normalizedAddress))
            {
                return BuildAddressablesError("entry rename requires assetPath and address");
            }

            if (!TryGetAddressableSettings(createIfMissing: false, out var settings, out var _, out var error))
            {
                return BuildAddressablesError(error ?? "failed to load Addressables settings");
            }

            var guid = AssetDatabase.AssetPathToGUID(normalizedAssetPath);
            if (string.IsNullOrWhiteSpace(guid))
            {
                return BuildAddressablesError($"asset was not found: {normalizedAssetPath}");
            }

            var entry = TryFindAssetEntry(settings!, guid);
            if (entry is null)
            {
                return BuildAddressablesError($"Addressables entry not found: {normalizedAssetPath}");
            }

            if (!TrySetPropertyValue(entry, "address", normalizedAddress))
            {
                return BuildAddressablesError("failed to set entry address");
            }

            MarkAddressablesDirty(settings!);
            return BuildAddressablesOk($"renamed entry address: {normalizedAssetPath} -> {normalizedAddress}", "addressables-entry-rename");
        }

        private static string ExecuteAddressablesEntryLabel(string? assetPath, string? label, bool remove)
        {
            var normalizedAssetPath = NormalizeAssetPath(assetPath);
            var normalizedLabel = (label ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedAssetPath) || string.IsNullOrWhiteSpace(normalizedLabel))
            {
                return BuildAddressablesError("entry label requires assetPath and label");
            }

            if (!TryGetAddressableSettings(createIfMissing: false, out var settings, out var _, out var error))
            {
                return BuildAddressablesError(error ?? "failed to load Addressables settings");
            }

            var guid = AssetDatabase.AssetPathToGUID(normalizedAssetPath);
            if (string.IsNullOrWhiteSpace(guid))
            {
                return BuildAddressablesError($"asset was not found: {normalizedAssetPath}");
            }

            var entry = TryFindAssetEntry(settings!, guid);
            if (entry is null)
            {
                return BuildAddressablesError($"Addressables entry not found: {normalizedAssetPath}");
            }

            if (!remove)
            {
                TryAddAddressablesLabel(settings!, normalizedLabel);
            }

            if (!TrySetEntryLabel(entry, normalizedLabel, !remove))
            {
                return BuildAddressablesError("failed to update Addressables label");
            }

            MarkAddressablesDirty(settings!);
            return BuildAddressablesOk(
                remove
                    ? $"removed label '{normalizedLabel}' from {normalizedAssetPath}"
                    : $"added label '{normalizedLabel}' to {normalizedAssetPath}",
                "addressables-entry-label");
        }

        private static string ExecuteAddressablesBulkAdd(string? folder, string? groupName, string? typeName)
        {
            var normalizedFolder = NormalizeAssetPath(folder);
            var normalizedGroupName = (groupName ?? string.Empty).Trim();
            var normalizedType = (typeName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedFolder) || string.IsNullOrWhiteSpace(normalizedGroupName))
            {
                return BuildAddressablesError("bulk add requires folder and groupName");
            }

            if (!TryGetAddressableSettings(createIfMissing: false, out var settings, out var _, out var error))
            {
                return BuildAddressablesError(error ?? "failed to load Addressables settings");
            }

            var targetGroup = FindGroupByName(settings!, normalizedGroupName);
            if (targetGroup is null)
            {
                return BuildAddressablesError($"group not found: {normalizedGroupName}");
            }

            var assets = FindAssetsInFolder(normalizedFolder, normalizedType, includeFolders: false, out error);
            if (!string.IsNullOrWhiteSpace(error))
            {
                return BuildAddressablesError(error!);
            }

            if (assets.Count == 0)
            {
                return BuildAddressablesError($"no assets matched in {normalizedFolder}");
            }

            var snapshot = new List<EntryMutationSnapshot>(assets.Count);
            foreach (var assetPath in assets)
            {
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (string.IsNullOrWhiteSpace(guid))
                {
                    return BuildAddressablesError($"failed to resolve GUID: {assetPath}");
                }

                var existing = TryFindAssetEntry(settings!, guid);
                snapshot.Add(CaptureEntrySnapshot(settings!, guid, existing));
            }

            var moved = 0;
            try
            {
                foreach (var state in snapshot)
                {
                    var entry = TryCreateOrMoveEntry(settings!, state.guid, targetGroup);
                    if (entry is null)
                    {
                        throw new InvalidOperationException($"failed to add or move entry: {state.assetPath}");
                    }

                    moved++;
                }
            }
            catch (Exception ex)
            {
                RestoreEntrySnapshots(settings!, snapshot);
                return BuildAddressablesError($"bulk add failed and was rolled back: {ex.Message}");
            }

            MarkAddressablesDirty(settings!);
            var response = new AddressablesBulkAddResponse
            {
                folder = normalizedFolder,
                groupName = normalizedGroupName,
                type = normalizedType,
                count = moved,
                assets = snapshot.Select(item => item.assetPath).ToArray()
            };
            return BuildAddressablesOk(
                $"bulk added {moved} asset(s) to {normalizedGroupName}",
                "addressables-bulk-add",
                JsonUtility.ToJson(response, true));
        }

        private static string ExecuteAddressablesBulkLabel(string? folder, string? label, string? typeName, bool remove)
        {
            var normalizedFolder = NormalizeAssetPath(folder);
            var normalizedLabel = (label ?? string.Empty).Trim();
            var normalizedType = (typeName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedFolder) || string.IsNullOrWhiteSpace(normalizedLabel))
            {
                return BuildAddressablesError("bulk label requires folder and label");
            }

            if (!TryGetAddressableSettings(createIfMissing: false, out var settings, out var _, out var error))
            {
                return BuildAddressablesError(error ?? "failed to load Addressables settings");
            }

            var assets = FindAssetsInFolder(normalizedFolder, normalizedType, includeFolders: true, out error);
            if (!string.IsNullOrWhiteSpace(error))
            {
                return BuildAddressablesError(error!);
            }

            if (assets.Count == 0)
            {
                return BuildAddressablesError($"no assets matched in {normalizedFolder}");
            }

            var snapshot = new List<EntryMutationSnapshot>(assets.Count);
            foreach (var assetPath in assets)
            {
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (string.IsNullOrWhiteSpace(guid))
                {
                    return BuildAddressablesError($"failed to resolve GUID: {assetPath}");
                }

                var existing = TryFindAssetEntry(settings!, guid);
                if (existing is null)
                {
                    return BuildAddressablesError($"asset is not marked Addressable: {assetPath}");
                }

                snapshot.Add(CaptureEntrySnapshot(settings!, guid, existing));
            }

            if (!remove)
            {
                TryAddAddressablesLabel(settings!, normalizedLabel);
            }

            var changed = 0;
            try
            {
                foreach (var state in snapshot)
                {
                    var entry = TryFindAssetEntry(settings!, state.guid);
                    if (entry is null)
                    {
                        throw new InvalidOperationException($"entry was missing during bulk label: {state.assetPath}");
                    }

                    if (!TrySetEntryLabel(entry, normalizedLabel, !remove))
                    {
                        throw new InvalidOperationException($"failed to update label on {state.assetPath}");
                    }

                    changed++;
                }
            }
            catch (Exception ex)
            {
                RestoreEntrySnapshots(settings!, snapshot);
                return BuildAddressablesError($"bulk label failed and was rolled back: {ex.Message}");
            }

            MarkAddressablesDirty(settings!);
            var response = new AddressablesBulkLabelResponse
            {
                folder = normalizedFolder,
                label = normalizedLabel,
                type = normalizedType,
                removed = remove,
                count = changed,
                assets = snapshot.Select(item => item.assetPath).ToArray()
            };
            return BuildAddressablesOk(
                remove
                    ? $"bulk removed label '{normalizedLabel}' from {changed} asset(s)"
                    : $"bulk added label '{normalizedLabel}' to {changed} asset(s)",
                "addressables-bulk-label",
                JsonUtility.ToJson(response, true));
        }

        private static string ExecuteAddressablesAnalyze(bool duplicateOnly)
        {
            if (!TryGetAddressableSettings(createIfMissing: false, out var settings, out var _, out var error))
            {
                return BuildAddressablesError(error ?? "failed to load Addressables settings");
            }

            if (duplicateOnly)
            {
                var duplicateReport = BuildDuplicateDependencyReport(settings!);
                return BuildAddressablesOk(
                    "duplicate dependency analysis complete",
                    "addressables-analyze-duplicate",
                    JsonUtility.ToJson(duplicateReport, true));
            }

            var report = BuildAddressablesReport(settings!);
            return BuildAddressablesOk(
                "addressables analysis complete",
                "addressables-analyze",
                JsonUtility.ToJson(report, true));
        }

        private static AddressablesAnalyzeResponse BuildAddressablesReport(object settings)
        {
            var groups = new List<AddressablesAnalyzeGroupDto>();
            var totalEntries = 0;
            foreach (var group in EnumerateGroups(settings)
                         .OrderBy(GetUnityObjectName, StringComparer.OrdinalIgnoreCase))
            {
                var entries = new List<AddressablesAnalyzeEntryDto>();
                var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in EnumerateEntries(group))
                {
                    var guid = GetPropertyString(entry, "guid");
                    if (string.IsNullOrWhiteSpace(guid))
                    {
                        continue;
                    }

                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrWhiteSpace(assetPath))
                    {
                        continue;
                    }

                    var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath)?.Name ?? "Unknown";
                    if (!byType.TryGetValue(assetType, out var typeCount))
                    {
                        typeCount = 0;
                    }

                    byType[assetType] = typeCount + 1;
                    entries.Add(new AddressablesAnalyzeEntryDto
                    {
                        guid = guid,
                        assetPath = assetPath,
                        address = GetPropertyString(entry, "address"),
                        assetType = assetType,
                        labels = GetEntryLabels(entry).OrderBy(label => label, StringComparer.OrdinalIgnoreCase).ToArray()
                    });
                }

                totalEntries += entries.Count;
                var (packingMode, compression) = ResolveGroupPacking(group);
                groups.Add(new AddressablesAnalyzeGroupDto
                {
                    name = GetUnityObjectName(group),
                    packingMode = packingMode,
                    compression = compression,
                    entryCount = entries.Count,
                    assetsByType = byType
                        .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(kv => new AddressablesAnalyzeTypeBucketDto
                        {
                            assetType = kv.Key,
                            count = kv.Value
                        })
                        .ToArray(),
                    entries = entries
                        .OrderBy(e => e.assetPath, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                });
            }

            return new AddressablesAnalyzeResponse
            {
                generatedAtUtc = DateTime.UtcNow.ToString("O"),
                groupCount = groups.Count,
                entryCount = totalEntries,
                groups = groups.ToArray()
            };
        }

        private static AddressablesDuplicateResponse BuildDuplicateDependencyReport(object settings)
        {
            var entryRefs = CollectAddressableEntries(settings).ToList();
            var explicitAssets = entryRefs
                .Select(entry => entry.assetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var map = new Dictionary<string, DuplicateAggregate>(StringComparer.OrdinalIgnoreCase);
            foreach (var entryRef in entryRefs)
            {
                if (string.IsNullOrWhiteSpace(entryRef.assetPath))
                {
                    continue;
                }

                var dependencies = AssetDatabase.GetDependencies(entryRef.assetPath, true);
                foreach (var dependency in dependencies)
                {
                    if (string.IsNullOrWhiteSpace(dependency))
                    {
                        continue;
                    }

                    if (dependency.Equals(entryRef.assetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (explicitAssets.Contains(dependency))
                    {
                        continue;
                    }

                    if (!map.TryGetValue(dependency, out var aggregate))
                    {
                        aggregate = new DuplicateAggregate();
                        map[dependency] = aggregate;
                    }

                    aggregate.groupNames.Add(entryRef.groupName);
                    aggregate.referenceKeys.Add(string.IsNullOrWhiteSpace(entryRef.address)
                        ? entryRef.assetPath
                        : $"{entryRef.address} ({entryRef.assetPath})");
                }
            }

            var duplicates = map
                .Where(kv => kv.Value.groupNames.Count > 1)
                .OrderByDescending(kv => kv.Value.groupNames.Count)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new AddressablesDuplicateEntryDto
                {
                    assetPath = kv.Key,
                    groupCount = kv.Value.groupNames.Count,
                    referencedByGroups = kv.Value.groupNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                    referencedByEntries = kv.Value.referenceKeys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()
                })
                .ToArray();

            return new AddressablesDuplicateResponse
            {
                generatedAtUtc = DateTime.UtcNow.ToString("O"),
                rule = "Check Duplicate Bundle Dependencies",
                duplicateCount = duplicates.Length,
                duplicates = duplicates
            };
        }

        private static bool TryGetAddressableSettings(
            bool createIfMissing,
            out object? settings,
            out Assembly? addressablesAssembly,
            out string? error)
        {
            settings = null;
            addressablesAssembly = null;
            error = null;

            if (!TryGetAddressablesEditorAssembly(out addressablesAssembly, out error))
            {
                return false;
            }

            var defaultObjectType = addressablesAssembly!.GetType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettingsDefaultObject");
            if (defaultObjectType is null)
            {
                error = "Addressables default settings API was not found in this Unity version";
                return false;
            }

            settings = GetPropertyValue(defaultObjectType, "Settings");
            if (settings is not null)
            {
                return true;
            }

            if (!createIfMissing)
            {
                error = "Addressables settings were not found. Run: addressable init";
                return false;
            }

            var getSettingsMethod = FindMethod(defaultObjectType, "GetSettings", 1);
            if (getSettingsMethod is not null)
            {
                try
                {
                    settings = getSettingsMethod.Invoke(null, new object[] { true });
                }
                catch
                {
                    settings = null;
                }
            }

            if (settings is null)
            {
                var settingsType = addressablesAssembly.GetType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettings");
                var createMethod = settingsType is null ? null : FindMethod(settingsType, "Create", 4);
                if (createMethod is not null)
                {
                    try
                    {
                        settings = createMethod.Invoke(
                            null,
                            new object[] { "Assets/AddressableAssetsData", "AddressableAssetSettings", true, true });
                    }
                    catch
                    {
                        settings = null;
                    }
                }
            }

            if (settings is null)
            {
                settings = GetPropertyValue(defaultObjectType, "Settings");
            }

            if (settings is null)
            {
                error = "failed to create Addressables settings";
                return false;
            }

            return true;
        }

        private static bool TryGetAddressablesEditorAssembly(out Assembly? addressablesAssembly, out string? error)
        {
            addressablesAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(
                    assembly.GetName().Name,
                    AddressablesEditorAssemblyName,
                    StringComparison.Ordinal));
            if (addressablesAssembly is not null)
            {
                error = null;
                return true;
            }

            error = "Addressables package is not available in the Unity editor (install com.unity.addressables)";
            return false;
        }

        private static IEnumerable<ProfileRef> EnumerateProfiles(object profileSettings)
        {
            var profiles = GetPropertyValue(profileSettings, "profiles") as IEnumerable;
            if (profiles is not null)
            {
                foreach (var profile in profiles)
                {
                    if (profile is null)
                    {
                        continue;
                    }

                    var id = GetPropertyString(profile, "id");
                    var name = GetPropertyString(profile, "profileName");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    yield return new ProfileRef(id, name);
                }

                yield break;
            }

            var names = InvokeEnumerableStrings(profileSettings, "GetAllProfileNames").ToList();
            foreach (var name in names)
            {
                var id = ResolveProfileId(profileSettings, name);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    yield return new ProfileRef(id!, name);
                }
            }
        }

        private static IEnumerable<string> GetProfileVariableNames(object profileSettings)
        {
            var all = InvokeEnumerableStrings(profileSettings, "GetAllVariableNames").ToList();
            if (all.Count > 0)
            {
                return all;
            }

            return InvokeEnumerableStrings(profileSettings, "GetVariableNames");
        }

        private static string ResolveProfileName(IEnumerable<ProfileRef> profiles, string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return string.Empty;
            }

            var found = profiles.FirstOrDefault(profile => profile.id == id);
            return string.IsNullOrWhiteSpace(found.name) ? string.Empty : found.name;
        }

        private static string? ResolveProfileId(object profileSettings, string profileName)
        {
            var direct = InvokeString(profileSettings, "GetProfileId", profileName);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            var matched = EnumerateProfiles(profileSettings)
                .FirstOrDefault(profile => profile.name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(matched.id) ? null : matched.id;
        }

        private static string ResolveProfileRawValue(object profileSettings, string profileId, string variableName)
        {
            var byName = InvokeString(profileSettings, "GetValueByName", profileId, variableName);
            if (!string.IsNullOrWhiteSpace(byName))
            {
                return byName;
            }

            var variableId = ResolveVariableId(profileSettings, variableName);
            if (!string.IsNullOrWhiteSpace(variableId))
            {
                var byId = InvokeString(profileSettings, "GetValueById", profileId, variableId!);
                if (!string.IsNullOrWhiteSpace(byId))
                {
                    return byId;
                }
            }

            return string.Empty;
        }

        private static string EvaluateProfileValue(object profileSettings, string profileId, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var evaluated = InvokeString(profileSettings, "EvaluateString", profileId, value);
            if (!string.IsNullOrWhiteSpace(evaluated))
            {
                return evaluated;
            }

            evaluated = InvokeString(profileSettings, "EvaluateString", value, profileId);
            return evaluated ?? value;
        }

        private static string? ResolveVariableId(object profileSettings, string variableName)
        {
            var byMethod = InvokeString(profileSettings, "GetVariableId", variableName)
                           ?? InvokeString(profileSettings, "GetProfileEntryId", variableName)
                           ?? InvokeString(profileSettings, "GetProfileDataId", variableName);
            if (!string.IsNullOrWhiteSpace(byMethod))
            {
                return byMethod;
            }

            var profileData = InvokeObject(profileSettings, "GetProfileDataByName", variableName);
            if (profileData is null)
            {
                return null;
            }

            return GetPropertyString(profileData, "Id")
                   ?? GetPropertyString(profileData, "id");
        }

        private static IEnumerable<object> EnumerateGroups(object settings)
        {
            var groups = GetPropertyValue(settings, "groups") as IEnumerable;
            if (groups is null)
            {
                return Enumerable.Empty<object>();
            }

            return groups.Cast<object>().Where(group => group is not null)!;
        }

        private static object? FindGroupByName(object settings, string groupName)
        {
            return EnumerateGroups(settings).FirstOrDefault(group =>
                GetUnityObjectName(group).Equals(groupName, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<object> EnumerateEntries(object group)
        {
            var entries = GetPropertyValue(group, "entries") as IEnumerable;
            if (entries is null)
            {
                return Enumerable.Empty<object>();
            }

            return entries.Cast<object>().Where(entry => entry is not null)!;
        }

        private static (string packingMode, string compression) ResolveGroupPacking(object group)
        {
            var schemas = GetPropertyValue(group, "Schemas") as IEnumerable;
            if (schemas is null)
            {
                return ("Unknown", "Unknown");
            }

            foreach (var schema in schemas)
            {
                if (schema is null)
                {
                    continue;
                }

                var typeName = schema.GetType().Name;
                if (!typeName.Contains("BundledAssetGroupSchema", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var packing = GetPropertyString(schema, "BundleMode")
                              ?? GetPropertyString(schema, "PackingMode")
                              ?? "Unknown";
                var compression = GetPropertyString(schema, "Compression")
                                  ?? GetPropertyString(schema, "BundleCompression")
                                  ?? "Unknown";
                return (packing, compression);
            }

            return ("Unknown", "Unknown");
        }

        private static bool TrySetDefaultGroup(object settings, object group)
        {
            var setMethod = FindMethod(settings.GetType(), "SetDefaultGroup", 1);
            if (setMethod is not null)
            {
                try
                {
                    setMethod.Invoke(settings, new[] { group });
                    return true;
                }
                catch
                {
                }
            }

            return TrySetPropertyValue(settings, "DefaultGroup", group);
        }

        private static bool TryRemoveGroup(object settings, object group)
        {
            var removeMethod = FindMethod(settings.GetType(), "RemoveGroup", 1)
                               ?? FindMethod(settings.GetType(), "RemoveGroup", 2);
            if (removeMethod is null)
            {
                return false;
            }

            var parameterCount = removeMethod.GetParameters().Length;
            try
            {
                if (parameterCount == 1)
                {
                    removeMethod.Invoke(settings, new[] { group });
                }
                else
                {
                    removeMethod.Invoke(settings, new[] { group, false });
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object? TryCreateOrMoveEntry(object settings, string guid, object group)
        {
            var method = FindMethod(settings.GetType(), "CreateOrMoveEntry", 4)
                         ?? FindMethod(settings.GetType(), "CreateOrMoveEntry", 3)
                         ?? FindMethod(settings.GetType(), "CreateOrMoveEntry", 2);
            if (method is null)
            {
                return null;
            }

            var parameterCount = method.GetParameters().Length;
            try
            {
                return parameterCount switch
                {
                    4 => method.Invoke(settings, new object[] { guid, group, false, false }),
                    3 => method.Invoke(settings, new object[] { guid, group, false }),
                    _ => method.Invoke(settings, new object[] { guid, group })
                };
            }
            catch
            {
                return null;
            }
        }

        private static object? TryFindAssetEntry(object settings, string guid)
        {
            var findMethod = FindMethod(settings.GetType(), "FindAssetEntry", 1)
                             ?? FindMethod(settings.GetType(), "FindAssetEntry", 2);
            if (findMethod is null)
            {
                return null;
            }

            try
            {
                return findMethod.GetParameters().Length == 1
                    ? findMethod.Invoke(settings, new object[] { guid })
                    : findMethod.Invoke(settings, new object[] { guid, false });
            }
            catch
            {
                return null;
            }
        }

        private static bool TryRemoveAssetEntry(object settings, string guid)
        {
            var removeMethod = FindMethod(settings.GetType(), "RemoveAssetEntry", 2)
                               ?? FindMethod(settings.GetType(), "RemoveAssetEntry", 1);
            if (removeMethod is null)
            {
                return false;
            }

            try
            {
                if (removeMethod.GetParameters().Length == 2)
                {
                    removeMethod.Invoke(settings, new object[] { guid, false });
                }
                else
                {
                    removeMethod.Invoke(settings, new object[] { guid });
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryAddAddressablesLabel(object settings, string label)
        {
            var addMethod = FindMethod(settings.GetType(), "AddLabel", 2)
                            ?? FindMethod(settings.GetType(), "AddLabel", 1);
            if (addMethod is null)
            {
                return false;
            }

            try
            {
                if (addMethod.GetParameters().Length == 2)
                {
                    addMethod.Invoke(settings, new object[] { label, false });
                }
                else
                {
                    addMethod.Invoke(settings, new object[] { label });
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetEntryLabel(object entry, string label, bool enabled)
        {
            var setMethod = FindMethod(entry.GetType(), "SetLabel", 4)
                            ?? FindMethod(entry.GetType(), "SetLabel", 3)
                            ?? FindMethod(entry.GetType(), "SetLabel", 2);
            if (setMethod is null)
            {
                return false;
            }

            var parameterCount = setMethod.GetParameters().Length;
            try
            {
                switch (parameterCount)
                {
                    case 4:
                        setMethod.Invoke(entry, new object[] { label, enabled, false, false });
                        break;
                    case 3:
                        setMethod.Invoke(entry, new object[] { label, enabled, false });
                        break;
                    default:
                        setMethod.Invoke(entry, new object[] { label, enabled });
                        break;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> GetEntryLabels(object entry)
        {
            var labels = GetPropertyValue(entry, "labels") as IEnumerable;
            if (labels is null)
            {
                return Enumerable.Empty<string>();
            }

            return labels.Cast<object>()
                .Where(label => label is not null)
                .Select(label => label.ToString() ?? string.Empty)
                .Where(label => !string.IsNullOrWhiteSpace(label));
        }

        private static IEnumerable<AddressableEntryRef> CollectAddressableEntries(object settings)
        {
            foreach (var group in EnumerateGroups(settings))
            {
                var groupName = GetUnityObjectName(group);
                foreach (var entry in EnumerateEntries(group))
                {
                    var guid = GetPropertyString(entry, "guid");
                    if (string.IsNullOrWhiteSpace(guid))
                    {
                        continue;
                    }

                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrWhiteSpace(assetPath))
                    {
                        continue;
                    }

                    yield return new AddressableEntryRef(
                        groupName,
                        assetPath,
                        GetPropertyString(entry, "address"));
                }
            }
        }

        private static List<string> FindAssetsInFolder(
            string folder,
            string typeName,
            bool includeFolders,
            out string? error)
        {
            error = null;
            if (!AssetDatabase.IsValidFolder(folder))
            {
                error = $"folder was not found: {folder}";
                return new List<string>();
            }

            var filter = string.IsNullOrWhiteSpace(typeName)
                ? string.Empty
                : $"t:{typeName}";
            var guids = AssetDatabase.FindAssets(filter, new[] { folder });
            var results = new List<string>(guids.Length);
            foreach (var guid in guids)
            {
                if (string.IsNullOrWhiteSpace(guid))
                {
                    continue;
                }

                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!includeFolders && AssetDatabase.IsValidFolder(path))
                {
                    continue;
                }

                results.Add(path);
            }

            return results
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static EntryMutationSnapshot CaptureEntrySnapshot(object settings, string guid, object? entry)
        {
            var assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (entry is null)
            {
                return new EntryMutationSnapshot
                {
                    guid = guid,
                    assetPath = assetPath,
                    existed = false,
                    groupName = string.Empty,
                    address = string.Empty,
                    labels = Array.Empty<string>()
                };
            }

            var parentGroup = GetPropertyValue(entry, "parentGroup");
            return new EntryMutationSnapshot
            {
                guid = guid,
                assetPath = assetPath,
                existed = true,
                groupName = parentGroup is null ? string.Empty : GetUnityObjectName(parentGroup),
                address = GetPropertyString(entry, "address") ?? string.Empty,
                labels = GetEntryLabels(entry).ToArray()
            };
        }

        private static void RestoreEntrySnapshots(object settings, IReadOnlyList<EntryMutationSnapshot> snapshots)
        {
            foreach (var state in snapshots)
            {
                if (!state.existed)
                {
                    TryRemoveAssetEntry(settings, state.guid);
                    continue;
                }

                var group = FindGroupByName(settings, state.groupName);
                if (group is null)
                {
                    continue;
                }

                var entry = TryCreateOrMoveEntry(settings, state.guid, group);
                if (entry is null)
                {
                    continue;
                }

                TrySetPropertyValue(entry, "address", state.address);
                var originalLabels = new HashSet<string>(state.labels ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                foreach (var existingLabel in GetEntryLabels(entry))
                {
                    if (originalLabels.Contains(existingLabel))
                    {
                        continue;
                    }

                    TrySetEntryLabel(entry, existingLabel, false);
                }

                foreach (var label in state.labels)
                {
                    if (string.IsNullOrWhiteSpace(label))
                    {
                        continue;
                    }

                    TryAddAddressablesLabel(settings, label);
                    TrySetEntryLabel(entry, label, true);
                }
            }
        }

        private static void MarkAddressablesDirty(object settings)
        {
            if (settings is UnityEngine.Object unityObject)
            {
                EditorUtility.SetDirty(unityObject);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static string BuildAddressablesOk(string message, string kind, string content = "")
        {
            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = message,
                kind = kind,
                content = content ?? string.Empty
            });
        }

        private static string BuildAddressablesError(string message)
        {
            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = false,
                message = message,
                kind = "addressables-error",
                content = string.Empty
            });
        }

        private static string NormalizeAssetPath(string? assetPath)
        {
            return (assetPath ?? string.Empty).Trim().Replace('\\', '/');
        }

        private static string GetUnityObjectName(object value)
        {
            if (value is UnityEngine.Object unityObject)
            {
                return unityObject.name ?? string.Empty;
            }

            return GetPropertyString(value, "Name")
                   ?? GetPropertyString(value, "name")
                   ?? string.Empty;
        }

        private static object? GetPropertyValue(object target, string propertyName)
        {
            if (target is null || string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            var type = target as Type ?? target.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var property = type.GetProperty(propertyName, flags);
            if (property is null)
            {
                return null;
            }

            try
            {
                return property.GetValue(target is Type ? null : target, null);
            }
            catch
            {
                return null;
            }
        }

        private static string? GetPropertyString(object target, string propertyName)
        {
            var value = GetPropertyValue(target, propertyName);
            return value?.ToString();
        }

        private static bool TrySetPropertyValue(object target, string propertyName, object? value)
        {
            var type = target.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var property = type.GetProperty(propertyName, flags);
            if (property is null || !property.CanWrite)
            {
                return false;
            }

            try
            {
                property.SetValue(target, value, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static MethodInfo? FindMethod(Type type, string methodName, int parameterCount)
        {
            return type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .FirstOrDefault(method =>
                    method.Name.Equals(methodName, StringComparison.Ordinal)
                    && method.GetParameters().Length == parameterCount);
        }

        private static IEnumerable<string> InvokeEnumerableStrings(object target, string methodName)
        {
            var method = FindMethod(target.GetType(), methodName, 0);
            if (method is null)
            {
                return Enumerable.Empty<string>();
            }

            object? value;
            try
            {
                value = method.Invoke(target, Array.Empty<object>());
            }
            catch
            {
                return Enumerable.Empty<string>();
            }

            if (value is not IEnumerable enumerable)
            {
                return Enumerable.Empty<string>();
            }

            var list = new List<string>();
            foreach (var item in enumerable)
            {
                if (item is null)
                {
                    continue;
                }

                var str = item.ToString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    list.Add(str);
                }
            }

            return list;
        }

        private static string? InvokeString(object target, string methodName, params object[] args)
        {
            var method = FindMethod(target.GetType(), methodName, args.Length);
            if (method is null)
            {
                return null;
            }

            try
            {
                var value = method.Invoke(target, args);
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static object? InvokeObject(object target, string methodName, params object[] args)
        {
            var method = FindMethod(target.GetType(), methodName, args.Length);
            if (method is null)
            {
                return null;
            }

            try
            {
                return method.Invoke(target, args);
            }
            catch
            {
                return null;
            }
        }

        [Serializable]
        private sealed class AddressablesCommandRequest
        {
            public string operation = string.Empty;
            public string name = string.Empty;
            public string assetPath = string.Empty;
            public string folder = string.Empty;
            public string groupName = string.Empty;
            public string address = string.Empty;
            public string label = string.Empty;
            public string type = string.Empty;
            public bool setDefault;
            public bool remove;
            public bool duplicate;
        }

        [Serializable]
        private sealed class AddressablesProfileListResponse
        {
            public string activeProfileId = string.Empty;
            public string activeProfileName = string.Empty;
            public int profileCount;
            public int variableCount;
            public AddressablesProfileDto[] profiles = Array.Empty<AddressablesProfileDto>();
        }

        [Serializable]
        private sealed class AddressablesProfileDto
        {
            public string id = string.Empty;
            public string name = string.Empty;
            public bool isActive;
            public AddressablesProfileVariableDto[] variables = Array.Empty<AddressablesProfileVariableDto>();
        }

        [Serializable]
        private sealed class AddressablesProfileVariableDto
        {
            public string name = string.Empty;
            public string rawValue = string.Empty;
            public string evaluatedValue = string.Empty;
        }

        [Serializable]
        private sealed class AddressablesGroupListResponse
        {
            public int count;
            public AddressablesGroupDto[] groups = Array.Empty<AddressablesGroupDto>();
        }

        [Serializable]
        private sealed class AddressablesGroupDto
        {
            public string name = string.Empty;
            public string packingMode = string.Empty;
            public string compression = string.Empty;
            public int entryCount;
        }

        [Serializable]
        private sealed class AddressablesAnalyzeResponse
        {
            public string generatedAtUtc = string.Empty;
            public int groupCount;
            public int entryCount;
            public AddressablesAnalyzeGroupDto[] groups = Array.Empty<AddressablesAnalyzeGroupDto>();
        }

        [Serializable]
        private sealed class AddressablesAnalyzeGroupDto
        {
            public string name = string.Empty;
            public string packingMode = string.Empty;
            public string compression = string.Empty;
            public int entryCount;
            public AddressablesAnalyzeTypeBucketDto[] assetsByType = Array.Empty<AddressablesAnalyzeTypeBucketDto>();
            public AddressablesAnalyzeEntryDto[] entries = Array.Empty<AddressablesAnalyzeEntryDto>();
        }

        [Serializable]
        private sealed class AddressablesAnalyzeTypeBucketDto
        {
            public string assetType = string.Empty;
            public int count;
        }

        [Serializable]
        private sealed class AddressablesAnalyzeEntryDto
        {
            public string guid = string.Empty;
            public string assetPath = string.Empty;
            public string address = string.Empty;
            public string assetType = string.Empty;
            public string[] labels = Array.Empty<string>();
        }

        [Serializable]
        private sealed class AddressablesDuplicateResponse
        {
            public string generatedAtUtc = string.Empty;
            public string rule = string.Empty;
            public int duplicateCount;
            public AddressablesDuplicateEntryDto[] duplicates = Array.Empty<AddressablesDuplicateEntryDto>();
        }

        [Serializable]
        private sealed class AddressablesDuplicateEntryDto
        {
            public string assetPath = string.Empty;
            public int groupCount;
            public string[] referencedByGroups = Array.Empty<string>();
            public string[] referencedByEntries = Array.Empty<string>();
        }

        [Serializable]
        private sealed class AddressablesBulkAddResponse
        {
            public string folder = string.Empty;
            public string groupName = string.Empty;
            public string type = string.Empty;
            public int count;
            public string[] assets = Array.Empty<string>();
        }

        [Serializable]
        private sealed class AddressablesBulkLabelResponse
        {
            public string folder = string.Empty;
            public string label = string.Empty;
            public string type = string.Empty;
            public bool removed;
            public int count;
            public string[] assets = Array.Empty<string>();
        }

        private sealed class DuplicateAggregate
        {
            public readonly HashSet<string> groupNames = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> referenceKeys = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class EntryMutationSnapshot
        {
            public string guid = string.Empty;
            public string assetPath = string.Empty;
            public bool existed;
            public string groupName = string.Empty;
            public string address = string.Empty;
            public string[] labels = Array.Empty<string>();
        }

        private readonly struct ProfileRef
        {
            public ProfileRef(string id, string name)
            {
                this.id = id;
                this.name = name;
            }

            public readonly string id;
            public readonly string name;
        }

        private readonly struct AddressableEntryRef
        {
            public AddressableEntryRef(string groupName, string assetPath, string? address)
            {
                this.groupName = groupName;
                this.assetPath = assetPath;
                this.address = address ?? string.Empty;
            }

            public readonly string groupName;
            public readonly string assetPath;
            public readonly string address;
        }
    }
}
#endif
