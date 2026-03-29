#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static partial class DaemonProjectService
    {
        private static void TryPersistLastOpenedScenePath(string sceneAssetPath)
        {
            if (string.IsNullOrWhiteSpace(sceneAssetPath))
            {
                return;
            }

            var normalized = sceneAssetPath.Replace('\\', '/');
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || !normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                var markerPath = Path.Combine(GetProjectRoot(), LastOpenedSceneMarkerRelativePath.Replace('/', Path.DirectorySeparatorChar));
                var markerDirectory = Path.GetDirectoryName(markerPath);
                if (!string.IsNullOrWhiteSpace(markerDirectory))
                {
                    Directory.CreateDirectory(markerDirectory);
                }

                File.WriteAllText(markerPath, normalized + Environment.NewLine);
            }
            catch
            {
                // best effort only
            }
        }

        private static string ExecuteWithRollbackStash(
            ProjectCommandRequest request,
            string mutationName,
            Func<string> executeMutation,
            IEnumerable<string> preMutationTargets,
            IEnumerable<string>? rollbackCleanupTargets = null)
        {
            return ExecuteWithFileSystemCriticalSection(() =>
            {
                if (request.intent is null)
                {
                    return executeMutation();
                }

                if (request.intent.flags.dryRun)
                {
                    var payload = BuildFileDryRunPayload(request, mutationName, preMutationTargets, rollbackCleanupTargets);
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = true,
                        message = "dry-run preview",
                        kind = "dry-run",
                        content = payload
                    });
                }

                var projectRoot = GetProjectRoot();
                var stashRoot = BuildTransactionStashRoot(projectRoot, request.intent.transactionId, mutationName);
                var stashEntries = new List<StashEntry>();
                try
                {
                    if (!EnsureVcsWriteAccess(request, preMutationTargets.Concat(rollbackCleanupTargets ?? Array.Empty<string>()), out var vcsError))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = vcsError ?? $"failed UVCS preflight for {mutationName}"
                        });
                    }

                    Directory.CreateDirectory(stashRoot);
                    foreach (var assetPath in preMutationTargets
                                 .Where(path => !string.IsNullOrWhiteSpace(path))
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!TryStashAssetWithMeta(projectRoot, stashRoot, assetPath, stashEntries, out var stashError))
                        {
                            return JsonUtility.ToJson(new ProjectCommandResponse
                            {
                                ok = false,
                                message = stashError ?? $"failed to stash target before {mutationName}: {assetPath}"
                            });
                        }
                    }

                    var responsePayload = executeMutation();
                    if (TryReadProjectResponseStatus(responsePayload, out var ok) && ok)
                    {
                        DeleteDirectorySafe(stashRoot);
                        return responsePayload;
                    }

                    if (!TryRevertFromStash(projectRoot, request, stashEntries, rollbackCleanupTargets, out var revertError))
                    {
                        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                        DeleteDirectorySafe(stashRoot);
                        return JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = $"{mutationName} failed and rollback also failed: {revertError}"
                        });
                    }

                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    DeleteDirectorySafe(stashRoot);
                    return responsePayload;
                }
                catch (Exception ex)
                {
                    TryRevertFromStash(projectRoot, request, stashEntries, rollbackCleanupTargets, out _);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    DeleteDirectorySafe(stashRoot);
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = $"{mutationName} exception: {ex.Message}"
                    });
                }
            });
        }

        private static string ExecuteWithFileSystemCriticalSection(Func<string> operation)
        {
            FileSystemMutationSemaphore.Wait();
            try
            {
                return operation();
            }
            finally
            {
                FileSystemMutationSemaphore.Release();
            }
        }

        private static string BuildTransactionStashRoot(string projectRoot, string transactionId, string mutationName)
        {
            var safeTransactionId = string.IsNullOrWhiteSpace(transactionId)
                ? $"tx-{DateTime.UtcNow:yyyyMMddHHmmssfff}"
                : SanitizePathToken(transactionId);
            var safeMutationName = SanitizePathToken(mutationName);
            var nonce = Guid.NewGuid().ToString("N")[..8];
            var root = ResolveTransactionStashBase(projectRoot);
            return Path.Combine(root, $"{safeTransactionId}-{safeMutationName}-{nonce}");
        }

        private static string ResolveTransactionStashBase(string projectRoot)
        {
            var overrideRoot = Environment.GetEnvironmentVariable("UNIFOCL_PROJECT_STASH_ROOT");
            var baseRoot = string.IsNullOrWhiteSpace(overrideRoot)
                ? Path.Combine(Path.GetTempPath(), "unifocl-stash")
                : Path.GetFullPath(overrideRoot);
            var digest = ComputeSha256Hex(projectRoot);
            if (digest.Length > 12)
            {
                digest = digest.Substring(0, 12);
            }

            return Path.Combine(baseRoot, digest);
        }

        private static string ComputeSha256Hex(string value)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }

        private static string SanitizePathToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return "transaction";
            }

            var chars = token
                .Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-')
                .ToArray();
            return new string(chars);
        }

        private static bool TryStashAssetWithMeta(
            string projectRoot,
            string stashRoot,
            string assetPath,
            List<StashEntry> entries,
            out string? error)
        {
            error = null;
            if (!TryStashSinglePath(projectRoot, stashRoot, assetPath, entries, out error))
            {
                return false;
            }

            if (!TryStashSinglePath(projectRoot, stashRoot, assetPath + ".meta", entries, out error))
            {
                return false;
            }

            return true;
        }

        private static bool TryStashSinglePath(
            string projectRoot,
            string stashRoot,
            string relativePath,
            List<StashEntry> entries,
            out string? error)
        {
            error = null;
            if (entries.Any(entry => entry.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var absolutePath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var existsAsFile = File.Exists(absolutePath);
            var existsAsDirectory = Directory.Exists(absolutePath);
            if (!existsAsFile && !existsAsDirectory)
            {
                return true;
            }

            var stashPath = Path.Combine(
                stashRoot,
                "entries",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (existsAsDirectory)
                {
                    CopyDirectoryRecursive(absolutePath, stashPath);
                }
                else
                {
                    var stashDirectory = Path.GetDirectoryName(stashPath);
                    if (!string.IsNullOrWhiteSpace(stashDirectory))
                    {
                        Directory.CreateDirectory(stashDirectory);
                    }

                    File.Copy(absolutePath, stashPath, overwrite: true);
                }

                entries.Add(new StashEntry(relativePath, stashPath, existsAsDirectory));
                return true;
            }
            catch (Exception ex)
            {
                error = $"failed to stash '{relativePath}': {ex.Message}";
                return false;
            }
        }

        private static bool TryRevertFromStash(
            string projectRoot,
            ProjectCommandRequest request,
            IReadOnlyList<StashEntry> entries,
            IEnumerable<string>? rollbackCleanupTargets,
            out string? error)
        {
            error = null;
            try
            {
                if (!EnsureVcsWriteAccess(
                        request,
                        entries.Select(entry => entry.RelativePath)
                            .Concat(rollbackCleanupTargets ?? Array.Empty<string>()),
                        out var vcsError))
                {
                    error = vcsError ?? "failed UVCS preflight while reverting rollback stash";
                    return false;
                }

                if (rollbackCleanupTargets is not null)
                {
                    foreach (var cleanupTarget in rollbackCleanupTargets
                                 .Where(path => !string.IsNullOrWhiteSpace(path))
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        DeleteAssetPathIfExists(projectRoot, cleanupTarget);
                        DeleteAssetPathIfExists(projectRoot, cleanupTarget + ".meta");
                    }
                }

                foreach (var entry in entries)
                {
                    var targetPath = Path.Combine(projectRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (entry.IsDirectory)
                    {
                        if (Directory.Exists(targetPath))
                        {
                            Directory.Delete(targetPath, recursive: true);
                        }

                        CopyDirectoryRecursive(entry.StashPath, targetPath);
                    }
                    else
                    {
                        var targetDirectory = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrWhiteSpace(targetDirectory))
                        {
                            Directory.CreateDirectory(targetDirectory);
                        }

                        File.Copy(entry.StashPath, targetPath, overwrite: true);
                    }
                }

                // The stash restored original content, so each checked-out file now matches
                // its pre-mutation state. Attempt to revert the UVC checkout (Unchanged mode)
                // so the workspace is left clean without requiring manual VCS intervention.
                // Files whose content still differs from the server (e.g. the user had their
                // own local changes before the mutation was attempted) are not touched because
                // Provider.Revert(Unchanged) is a no-op when content diverges from the server.
                var mode = request.intent?.flags?.vcsMode ?? string.Empty;
                if (mode.Equals(VcsModeUvcsAll, StringComparison.OrdinalIgnoreCase)
                    || mode.Equals(VcsModeUvcsHybridGitIgnore, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var entry in entries)
                    {
                        DaemonDryRunSceneRestoreService.TryRevertViaUvcs(entry.RelativePath);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void DeleteAssetPathIfExists(string projectRoot, string relativePath)
        {
            var absolutePath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
                return;
            }

            if (Directory.Exists(absolutePath))
            {
                Directory.Delete(absolutePath, recursive: true);
            }
        }

        private static void CopyDirectoryRecursive(string sourceDirectory, string targetDirectory)
        {
            Directory.CreateDirectory(targetDirectory);
            foreach (var file in Directory.GetFiles(sourceDirectory))
            {
                var targetFile = Path.Combine(targetDirectory, Path.GetFileName(file));
                File.Copy(file, targetFile, overwrite: true);
            }

            foreach (var directory in Directory.GetDirectories(sourceDirectory))
            {
                var targetSubdirectory = Path.Combine(targetDirectory, Path.GetFileName(directory));
                CopyDirectoryRecursive(directory, targetSubdirectory);
            }
        }

        private static void ApplyVcsMetadataToChanges(ProjectCommandRequest request, List<MutationPathChange> changes)
        {
            var ownedLookup = request.intent?.flags?.vcsOwnedPaths?
                .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.path))
                .GroupBy(entry => NormalizeAssetPath(entry.path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, MutationVcsOwnedPath>(StringComparer.OrdinalIgnoreCase);

            foreach (var change in changes)
            {
                var normalizedPath = NormalizeAssetPath(change.path);
                if (ownedLookup.TryGetValue(normalizedPath, out var owner))
                {
                    change.owner = owner.owner ?? string.Empty;
                    change.requiresCheckout = owner.requiresCheckout;
                }
            }
        }

        private static bool EnsureVcsWriteAccess(
            ProjectCommandRequest request,
            IEnumerable<string> candidatePaths,
            out string? error)
        {
            error = null;
            if (request.intent is null || request.intent.flags is null)
            {
                return true;
            }

            var mode = request.intent.flags.vcsMode ?? string.Empty;
            if (!mode.Equals(VcsModeUvcsAll, StringComparison.OrdinalIgnoreCase)
                && !mode.Equals(VcsModeUvcsHybridGitIgnore, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var ownedLookup = request.intent.flags.vcsOwnedPaths?
                .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.path))
                .GroupBy(entry => NormalizeAssetPath(entry.path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, MutationVcsOwnedPath>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawPath in candidatePaths
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Select(NormalizeAssetPath)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!RequiresUvcsCheckoutForPath(mode, rawPath, ownedLookup))
                {
                    continue;
                }

                if (!EnsureUvcsCheckoutAndWritable(rawPath, out error))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool RequiresUvcsCheckoutForPath(
            string mode,
            string path,
            IReadOnlyDictionary<string, MutationVcsOwnedPath> ownedLookup)
        {
            if (ownedLookup.TryGetValue(path, out var owned))
            {
                return string.Equals(owned.owner, VcsOwnerUvcs, StringComparison.OrdinalIgnoreCase) || owned.requiresCheckout;
            }

            return mode.Equals(VcsModeUvcsAll, StringComparison.OrdinalIgnoreCase);
        }

        private static bool EnsureUvcsCheckoutAndWritable(string assetPath, out string? error)
        {
            error = null;
            var projectRoot = GetProjectRoot();
            var absolutePath = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            var existsAsFile = File.Exists(absolutePath);
            var existsAsDirectory = Directory.Exists(absolutePath);
            if (!existsAsFile && !existsAsDirectory)
            {
                return true;
            }

            var checkoutAttempted = TryCheckoutViaUvcs(assetPath, out var checkoutError);
            if (!checkoutAttempted && !string.IsNullOrWhiteSpace(checkoutError))
            {
                error = $"UVCS checkout failed for '{assetPath}': {checkoutError}";
                return false;
            }

            if (existsAsFile && IsReadOnlyFile(absolutePath))
            {
                error = $"UVCS checkout did not unlock writable file '{assetPath}'. Open Unity Version Control and check out the file.";
                return false;
            }

            return true;
        }

        private static bool IsReadOnlyFile(string absolutePath)
        {
            try
            {
                var attributes = File.GetAttributes(absolutePath);
                return (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryCheckoutViaUvcs(string assetPath, out string? error)
        {
            error = null;
            try
            {
                var providerType = Type.GetType("UnityEditor.VersionControl.Provider, UnityEditor");
                if (providerType is null)
                {
                    error = "UnityEditor.VersionControl.Provider is unavailable in this Unity runtime";
                    return false;
                }

                var checkoutModeType = Type.GetType("UnityEditor.VersionControl.CheckoutMode, UnityEditor");
                if (checkoutModeType is null)
                {
                    error = "UnityEditor.VersionControl.CheckoutMode is unavailable in this Unity runtime";
                    return false;
                }

                var checkoutMethod = providerType.GetMethod(
                    "Checkout",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), checkoutModeType },
                    null);
                if (checkoutMethod is null)
                {
                    error = "Provider.Checkout(string, CheckoutMode) API is unavailable";
                    return false;
                }

                var checkoutMode = Enum.GetValues(checkoutModeType).GetValue(0);
                var task = checkoutMethod.Invoke(null, new[] { assetPath, checkoutMode });
                if (task is null)
                {
                    error = "Provider.Checkout returned null task";
                    return false;
                }

                var taskType = task.GetType();
                var waitMethod = taskType.GetMethod("Wait", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                waitMethod?.Invoke(task, null);

                var successProperty = taskType.GetProperty("success", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (successProperty?.GetValue(task) is bool success && !success)
                {
                    var textProperty = taskType.GetProperty("text", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    var text = textProperty?.GetValue(task)?.ToString();
                    error = string.IsNullOrWhiteSpace(text) ? "checkout task reported failure" : text;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string NormalizeAssetPath(string path)
        {
            return (path ?? string.Empty)
                .Replace('\\', '/')
                .Trim()
                .TrimStart('/');
        }

        private static bool TryReadProjectResponseStatus(string payload, out bool ok)
        {
            ok = false;
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            try
            {
                var parsed = JsonUtility.FromJson<ProjectCommandResponse>(payload);
                if (parsed is null)
                {
                    return false;
                }

                ok = parsed.ok;
                return true;
            }
            catch
            {
                return false;
            }
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

        private static string GetProjectRoot()
        {
            return Directory.GetParent(Application.dataPath)?.FullName
                   ?? throw new InvalidOperationException("failed to resolve Unity project root");
        }

        private readonly struct StashEntry
        {
            public StashEntry(string relativePath, string stashPath, bool isDirectory)
            {
                RelativePath = relativePath;
                StashPath = stashPath;
                IsDirectory = isDirectory;
            }

            public string RelativePath { get; }
            public string StashPath { get; }
            public bool IsDirectory { get; }
        }
    }
}
#nullable restore
#endif
