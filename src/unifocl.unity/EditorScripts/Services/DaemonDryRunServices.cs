#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UniFocl.EditorBridge
{
    internal static class DaemonDryRunContext
    {
        private static readonly AsyncLocal<int> Depth = new();

        public static bool IsActive => Depth.Value > 0;

        public static IDisposable Enter()
        {
            Depth.Value = Depth.Value + 1;
            return new ExitScope();
        }

        private sealed class ExitScope : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                var current = Depth.Value;
                Depth.Value = current > 0 ? current - 1 : 0;
                _disposed = true;
            }
        }
    }

    internal static class DaemonDryRunDiffService
    {
        public static string BuildJsonDiffPayload(string summary, string beforeJson, string afterJson)
        {
            var lines = BuildUnifiedDiffLines(beforeJson, afterJson);
            var payload = new MutationDryRunDiffPayload
            {
                summary = summary,
                format = "unified",
                before = beforeJson,
                after = afterJson,
                lines = lines.ToArray(),
                changes = Array.Empty<MutationPathChange>()
            };
            return JsonUtility.ToJson(payload);
        }

        public static string BuildFileDiffPayload(string summary, IEnumerable<MutationPathChange> changes)
        {
            var buffered = changes.ToList();
            var lines = new List<string>
            {
                "--- before",
                "+++ after"
            };
            foreach (var change in buffered)
            {
                var path = change.path ?? string.Empty;
                var nextPath = string.IsNullOrWhiteSpace(change.nextPath) ? "-" : change.nextPath!;
                var metaPath = string.IsNullOrWhiteSpace(change.metaPath) ? "-" : change.metaPath!;
                var owner = string.IsNullOrWhiteSpace(change.owner) ? "unknown" : change.owner;
                var checkout = change.requiresCheckout ? "required" : "no";
                lines.Add($"- {change.action}: {path}");
                lines.Add($"+ {change.action}: {nextPath}");
                lines.Add($"~ meta: {metaPath}");
                lines.Add($"~ owner: {owner} (checkout={checkout})");
            }

            var payload = new MutationDryRunDiffPayload
            {
                summary = summary,
                format = "unified",
                before = string.Empty,
                after = string.Empty,
                lines = lines.ToArray(),
                changes = buffered.ToArray()
            };
            return JsonUtility.ToJson(payload);
        }

        private static List<string> BuildUnifiedDiffLines(string beforeJson, string afterJson)
        {
            var before = SplitLines(beforeJson);
            var after = SplitLines(afterJson);
            var lines = new List<string>
            {
                "--- before",
                "+++ after"
            };

            var max = Math.Max(before.Length, after.Length);
            for (var i = 0; i < max; i++)
            {
                var hasBefore = i < before.Length;
                var hasAfter = i < after.Length;
                if (hasBefore && hasAfter)
                {
                    if (string.Equals(before[i], after[i], StringComparison.Ordinal))
                    {
                        lines.Add($" {before[i]}");
                    }
                    else
                    {
                        lines.Add($"-{before[i]}");
                        lines.Add($"+{after[i]}");
                    }

                    continue;
                }

                if (hasBefore)
                {
                    lines.Add($"-{before[i]}");
                }
                else if (hasAfter)
                {
                    lines.Add($"+{after[i]}");
                }
            }

            return lines;
        }

        private static string[] SplitLines(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.None);
        }

        public static string SnapshotObject(UnityEngine.Object target)
        {
            return target is null ? "{}" : EditorJsonUtility.ToJson(target, prettyPrint: true);
        }
    }

    /// <summary>
    /// Cleans up scene dirty state left behind by a dry-run + Undo.RevertAllDownToGroup pair.
    ///
    /// Root cause: Undo.RevertAllDownToGroup reverts in-memory mutations but marks affected
    /// scenes as dirty. For UVC-controlled scenes (read-only files), this dirty flag persists
    /// and cannot be cleared without a VCS checkout, causing Unity Version Control to show a
    /// spurious "checked out but unchanged" entry the next time any save is triggered.
    ///
    /// Fix: after the dry-run scope exits, save each scene that was clean before the dry-run
    /// but is now dirty. For UVC-controlled scenes the file will be read-only, so we issue a
    /// Provider.Checkout first, save to clear the dirty flag, then Provider.Revert (Unchanged)
    /// to release the checkout — leaving the workspace in exactly the state it was before the
    /// dry-run.
    /// </summary>
    internal static class DaemonDryRunSceneRestoreService
    {
        /// <summary>
        /// Captures the dirty state of all currently loaded, persistent scenes.
        /// Call this immediately <em>before</em> entering a dry-run scope.
        /// </summary>
        public static (Scene[] Scenes, bool[] IsDirty) CaptureDirtyState()
        {
            var count = SceneManager.sceneCount;
            var scenes = new Scene[count];
            var dirty = new bool[count];
            for (var i = 0; i < count; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                scenes[i] = scene;
                dirty[i] = scene.isDirty;
            }

            return (scenes, dirty);
        }

        /// <summary>
        /// For each scene that was clean before the dry-run but is now dirty, saves it so the
        /// dirty flag is cleared. For UVC-controlled (read-only) scenes a checkout is issued
        /// before saving and reverted (unchanged) afterwards so no spurious VCS state remains.
        /// Must be called <em>after</em> the dry-run scope has been disposed (IsActive = false).
        /// </summary>
        public static void RestorePreviouslyCleanScenes(Scene[] scenes, bool[] wasDirty)
        {
            for (var i = 0; i < scenes.Length && i < wasDirty.Length; i++)
            {
                var scene = scenes[i];
                if (wasDirty[i])
                {
                    continue;
                }

                if (!scene.isDirty || !scene.IsValid() || !scene.isLoaded || string.IsNullOrWhiteSpace(scene.path))
                {
                    continue;
                }

                if (EditorSceneManager.IsPreviewScene(scene))
                {
                    continue;
                }

                TrySaveAndRevertUvcsCheckout(scene);
            }
        }

        private static void TrySaveAndRevertUvcsCheckout(Scene scene)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return;
            }

            var scenePath = scene.path;
            var absolutePath = Path.Combine(projectRoot!, scenePath.Replace('/', Path.DirectorySeparatorChar));

            // Check whether the file is read-only (UVC exclusive lock).
            var wasReadOnly = File.Exists(absolutePath) && IsReadOnly(absolutePath);

            if (wasReadOnly)
            {
                // Explicitly check out so the save can write through.
                TryCheckoutViaUvcs(scenePath, out _);
            }

            try
            {
                EditorSceneManager.SaveScene(scene);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] dry-run scene restore: failed to save '{scenePath}': {ex.Message}");
                return;
            }

            if (wasReadOnly)
            {
                // The save wrote identical content (dry-run was fully reverted), so release
                // the checkout via Provider.Revert(Unchanged) — a no-op if content differs.
                TryRevertViaUvcs(scenePath);
            }
        }

        private static bool IsReadOnly(string absolutePath)
        {
            try
            {
                return (File.GetAttributes(absolutePath) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
            }
            catch
            {
                return false;
            }
        }

        private static void TryCheckoutViaUvcs(string assetPath, out string? error)
        {
            error = null;
            try
            {
                var providerType = Type.GetType("UnityEditor.VersionControl.Provider, UnityEditor");
                if (providerType is null)
                {
                    error = "UnityEditor.VersionControl.Provider is unavailable";
                    return;
                }

                var checkoutModeType = Type.GetType("UnityEditor.VersionControl.CheckoutMode, UnityEditor");
                if (checkoutModeType is null)
                {
                    error = "UnityEditor.VersionControl.CheckoutMode is unavailable";
                    return;
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
                    return;
                }

                var checkoutMode = Enum.GetValues(checkoutModeType).GetValue(0);
                var task = checkoutMethod.Invoke(null, new[] { assetPath, checkoutMode });
                if (task is null)
                {
                    return;
                }

                var waitMethod = task.GetType().GetMethod(
                    "Wait", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                waitMethod?.Invoke(task, null);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        /// <summary>
        /// Issues a UVC revert for <paramref name="assetPath"/> using RevertMode.Unchanged,
        /// which only discards the checkout if the file content still matches the server copy.
        /// Safe to call even when the file was not checked out — no-ops silently in that case.
        /// </summary>
        internal static void TryRevertViaUvcs(string assetPath)
        {
            try
            {
                var providerType = Type.GetType("UnityEditor.VersionControl.Provider, UnityEditor");
                if (providerType is null)
                {
                    return;
                }

                // Provider.GetAssetByPath(string) → Asset
                var getMethod = providerType.GetMethod(
                    "GetAssetByPath",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string) },
                    null);
                if (getMethod is null)
                {
                    return;
                }

                var asset = getMethod.Invoke(null, new object[] { assetPath });
                if (asset is null)
                {
                    return;
                }

                var assetListType = Type.GetType("UnityEditor.VersionControl.AssetList, UnityEditor");
                if (assetListType is null)
                {
                    return;
                }

                var assetList = Activator.CreateInstance(assetListType);
                var addMethod = assetListType.GetMethod("Add", new[] { asset.GetType() });
                addMethod?.Invoke(assetList, new[] { asset });

                var revertModeType = Type.GetType("UnityEditor.VersionControl.RevertMode, UnityEditor");
                if (revertModeType is null)
                {
                    return;
                }

                // Prefer "Unchanged" so we only release the checkout when the file content
                // matches the server, preventing accidental loss of real local changes.
                object revertMode;
                try
                {
                    revertMode = Enum.Parse(revertModeType, "Unchanged");
                }
                catch
                {
                    revertMode = Enum.GetValues(revertModeType).GetValue(0)!;
                }

                var revertMethod = providerType.GetMethod(
                    "Revert",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { assetListType, revertModeType },
                    null);
                if (revertMethod is null)
                {
                    return;
                }

                var task = revertMethod.Invoke(null, new[] { assetList, revertMode });
                if (task is null)
                {
                    return;
                }

                var waitMethod = task.GetType().GetMethod(
                    "Wait", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                waitMethod?.Invoke(task, null);
            }
            catch
            {
                // Best effort — leave the file checked out rather than throwing.
            }
        }
    }
}
#endif
