#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// A preview sandbox that spans several commands: everything that runs between <see cref="Begin"/> and
    /// <see cref="End"/> is undone, and the report says what it changed and what could not be undone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While a session is open, <see cref="DaemonDryRunContext.IsActive"/> is true, so unifocl's own
    /// services skip their durable writes and <see cref="DaemonDryRunAssetModificationProcessor"/> refuses
    /// AssetDatabase saves, moves and deletes. That is what lets it wrap any command, including Unity's
    /// own Pipeline commands, which know nothing about unifocl.
    /// </para>
    /// <para>
    /// Ending a session undoes the work in layers, from precise to blunt: Unity's Undo back to the group
    /// the session opened; reloading scenes that were saved when the session began but still differ;
    /// deleting files the session created and restoring the backed-up settings and package manifests.
    /// Anything left after that is reported as not reverted.
    /// </para>
    /// <para>
    /// The session lives in <c>Library/unifocl/sandbox/</c>, so a domain reload (a command that adds a
    /// script, for example) doesn't lose it, and it ends by itself when it expires.
    /// </para>
    /// </remarks>
    internal static class DaemonSandboxSession
    {
        private const int MaxObjectsToFingerprint = 20000;
        private const int MaxReportedItems = 200;

        private static readonly string[] ScanRoots = { "Assets", "ProjectSettings", "Packages" };
        private static readonly string[] BackedUpFiles = { "Packages/manifest.json", "Packages/packages-lock.json" };
        private const string BackedUpDirectory = "ProjectSettings";

        private static SandboxState? s_State;
        private static DateTime s_NextExpiryCheckUtc;

        public static bool IsActive => s_State is not null;

        public static string? ActiveId => s_State?.id;

        private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();

        private static string SandboxDirectory => Path.Combine(ProjectRoot, "Library", "unifocl", "sandbox");

        private static string StatePath => Path.Combine(SandboxDirectory, "state.json");

        private static string BackupDirectory => Path.Combine(SandboxDirectory, "backup");

        [InitializeOnLoadMethod]
        private static void Restore()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    s_State = JsonUtility.FromJson<SandboxState>(File.ReadAllText(StatePath));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] could not read the open sandbox session; discarding it: {ex.Message}");
                s_State = null;
            }

            EditorApplication.update -= CheckExpiry;
            EditorApplication.update += CheckExpiry;
        }

        /// <summary>Opens a session, or returns an error when one is already open.</summary>
        public static string? Begin(string target, int ttlSeconds, out SandboxBeginInfo info)
        {
            info = new SandboxBeginInfo();
            if (s_State is not null)
            {
                return $"a sandbox session is already open ({s_State.id}, for '{s_State.target}'); end it first";
            }

            var now = DateTime.UtcNow;
            var state = new SandboxState
            {
                id = Guid.NewGuid().ToString("N").Substring(0, 12),
                target = target ?? string.Empty,
                startedAtUtc = now.ToString("O"),
                expiresAtUtc = now.AddSeconds(Math.Max(10, ttlSeconds)).ToString("O"),
                undoGroup = DaemonUndo.BeginIsolatedGroup("[unifocl sandbox]"),
                scenes = CaptureSceneSetup(),
                objects = CaptureObjects(out var truncated),
                fingerprintsTruncated = truncated,
                files = ScanFiles(),
            };

            BackUpFiles();
            s_State = state;
            Save();

            info.id = state.id;
            info.expiresAtUtc = state.expiresAtUtc;
            return null;
        }

        /// <summary>Ends the open session, undoing what ran in it, and returns the report.</summary>
        public static SandboxReport? End(out string? error)
        {
            error = null;
            var state = s_State;
            if (state is null)
            {
                error = "no sandbox session is open";
                return null;
            }

            var report = new SandboxReport { id = state.id, target = state.target, startedAtUtc = state.startedAtUtc };
            report.blocked.AddRange(state.blocked);
            var before = state.objects.ToDictionary(o => o.key, o => o);
            var changed = CaptureObjects(out _);
            DiffObjects(before, changed, report);

            // Writes must be allowed again before reverting: reloading a scene or deleting a created asset
            // goes through the same AssetDatabase hooks the session blocks.
            s_State = null;
            DeleteState();

            Undo.RevertAllDownToGroup(state.undoGroup);
            RestoreSceneSetup(state, report);
            RevertFiles(state, report);
            AssetDatabase.Refresh();
            ReloadScenesThatStillDiffer(state, before, report);

            var residual = CaptureObjects(out _);
            var leftover = new SandboxReport();
            DiffObjects(before, residual, leftover);
            report.notReverted.AddRange(leftover.objectsAdded.Select(p => $"object still present: {p}"));
            report.notReverted.AddRange(leftover.objectsRemoved.Select(p => $"object still missing: {p}"));
            report.notReverted.AddRange(leftover.objectsModified.Select(p => $"object still modified: {p}"));

            // A scene that was saved before the session but is dirty now only carries the reverted state.
            ClearDirtyOnScenesThatWereSaved(state);

            report.fingerprintsTruncated = state.fingerprintsTruncated;
            report.endedAtUtc = DateTime.UtcNow.ToString("O");
            report.reverted = report.notReverted.Count == 0;
            Trim(report);
            return report;
        }

        public static SandboxStatus Status() => s_State is null
            ? new SandboxStatus()
            : new SandboxStatus
            {
                active = true,
                id = s_State.id,
                target = s_State.target,
                startedAtUtc = s_State.startedAtUtc,
                expiresAtUtc = s_State.expiresAtUtc,
            };

        /// <summary>Notes an AssetDatabase write that the session refused.</summary>
        public static void RecordBlocked(string operation, string path)
        {
            if (s_State is null || s_State.blocked.Count >= MaxReportedItems)
            {
                return;
            }

            s_State.blocked.Add($"{operation}: {path}");
            Save();
        }

        private static void CheckExpiry()
        {
            var now = DateTime.UtcNow;
            if (s_State is null || now < s_NextExpiryCheckUtc)
            {
                return;
            }

            s_NextExpiryCheckUtc = now.AddSeconds(1);
            if (!DateTime.TryParse(s_State.expiresAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expires)
                || now < expires
                || EditorApplication.isCompiling
                || EditorApplication.isUpdating)
            {
                return;
            }

            var id = s_State.id;
            var report = End(out _);
            Debug.LogWarning(
                $"[unifocl] sandbox session {id} expired before it was ended; its changes were undone" +
                (report is { reverted: false } ? $", except: {string.Join("; ", report.notReverted.Take(5))}" : "."));
        }

        // ── Scenes ──────────────────────────────────────────────────────────────────────────────

        private static List<SandboxScene> CaptureSceneSetup()
        {
            var scenes = new List<SandboxScene>();
            var active = SceneManager.GetActiveScene();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                scenes.Add(new SandboxScene
                {
                    path = scene.path,
                    isLoaded = scene.isLoaded,
                    isActive = scene == active,
                    wasDirty = scene.isDirty,
                });
            }

            return scenes;
        }

        private static void RestoreSceneSetup(SandboxState state, SandboxReport report)
        {
            var now = CaptureSceneSetup();
            var same = now.Count == state.scenes.Count
                       && now.Zip(state.scenes, (a, b) => a.path == b.path && a.isLoaded == b.isLoaded).All(x => x);
            if (same || state.scenes.Count == 0 || state.scenes.Any(s => string.IsNullOrEmpty(s.path)))
            {
                if (!same)
                {
                    report.notReverted.Add("the set of open scenes changed and includes an unsaved scene, so it was left as it is");
                }

                return;
            }

            var setup = state.scenes.Select(s => new SceneSetup { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray();
            EditorSceneManager.RestoreSceneManagerSetup(setup);
            report.reverts.Add("reopened the scenes that were open before the session");
            if (state.scenes.Any(s => s.wasDirty))
            {
                report.notReverted.Add("scenes were switched, which discarded their unsaved changes from before the session");
            }
        }

        private static void ReloadScenesThatStillDiffer(
            SandboxState state,
            Dictionary<string, SandboxObject> before,
            SandboxReport report)
        {
            var residual = CaptureObjects(out _);
            var leftover = new SandboxReport();
            DiffObjects(before, residual, leftover);
            var differing = new HashSet<string>(
                leftover.objectsAdded.Concat(leftover.objectsRemoved).Concat(leftover.objectsModified).Select(SceneOf));

            // Only a single open scene is reloaded: reopening one of several would close and reorder the
            // others. The residual check that follows reports what is left in a multi-scene setup.
            if (state.scenes.Count != 1 || SceneManager.sceneCount != 1)
            {
                return;
            }

            var scene = state.scenes[0];
            if (scene.wasDirty || !scene.isLoaded || string.IsNullOrEmpty(scene.path) || !differing.Contains(scene.path)
                || !SceneManager.GetSceneByPath(scene.path).IsValid())
            {
                return;
            }

            // Saved when the session began, so the file on disk is the state to return to. Changes made
            // without Undo (raw API calls) are only undone this way.
            EditorSceneManager.OpenScene(scene.path, OpenSceneMode.Single);
            report.reverts.Add($"reloaded {scene.path} from disk");
        }

        private static void ClearDirtyOnScenesThatWereSaved(SandboxState state)
        {
            var scenes = new List<Scene>();
            var wasDirty = new List<bool>();
            foreach (var entry in state.scenes)
            {
                var scene = SceneManager.GetSceneByPath(entry.path);
                if (scene.IsValid())
                {
                    scenes.Add(scene);
                    wasDirty.Add(entry.wasDirty);
                }
            }

            DaemonDryRunSceneRestoreService.RestorePreviouslyCleanScenes(scenes.ToArray(), wasDirty.ToArray());
        }

        private static string SceneOf(string objectKey)
        {
            var separator = objectKey.IndexOf("::", StringComparison.Ordinal);
            return separator < 0 ? objectKey : objectKey.Substring(0, separator);
        }

        // ── Objects ─────────────────────────────────────────────────────────────────────────────

        private static List<SandboxObject> CaptureObjects(out bool truncated)
        {
            truncated = false;
            var objects = new List<SandboxObject>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                {
                    continue;
                }

                var sceneKey = string.IsNullOrEmpty(scene.path) ? $"<unsaved:{scene.name}>" : scene.path;
                foreach (var root in scene.GetRootGameObjects())
                {
                    Walk(root.transform, sceneKey + "::" + root.name, objects, ref truncated);
                }
            }

            return objects;
        }

        private static void Walk(Transform transform, string key, List<SandboxObject> objects, ref bool truncated)
        {
            var go = transform.gameObject;
            var fingerprint = objects.Count < MaxObjectsToFingerprint;
            truncated |= !fingerprint;
            objects.Add(new SandboxObject
            {
                key = key,
                state = DescribeObject(go, fingerprint),
            });

            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                seen.TryGetValue(child.name, out var count);
                seen[child.name] = count + 1;
                Walk(child, $"{key}/{child.name}{(count == 0 ? string.Empty : $"#{count}")}", objects, ref truncated);
            }
        }

        private static string DescribeObject(GameObject go, bool fingerprint)
        {
            var sb = new StringBuilder();
            sb.Append(go.activeSelf ? "active" : "inactive").Append(" tag=").Append(go.tag).Append(" layer=").Append(go.layer);
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null)
                {
                    sb.Append(" | <missing script>");
                    continue;
                }

                sb.Append(" | ").Append(component.GetType().Name);
                if (fingerprint)
                {
                    sb.Append(':').Append(Hash(EditorJsonUtility.ToJson(component)).ToString("x8"));
                }
            }

            return sb.ToString();
        }

        private static void DiffObjects(Dictionary<string, SandboxObject> before, List<SandboxObject> after, SandboxReport report)
        {
            var afterKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in after)
            {
                afterKeys.Add(entry.key);
                if (!before.TryGetValue(entry.key, out var old))
                {
                    report.objectsAdded.Add(entry.key);
                }
                else if (!string.Equals(old.state, entry.state, StringComparison.Ordinal))
                {
                    report.objectsModified.Add($"{entry.key} ({DescribeChange(old.state, entry.state)})");
                }
            }

            report.objectsRemoved.AddRange(before.Keys.Where(key => !afterKeys.Contains(key)));
        }

        private static string DescribeChange(string before, string after)
        {
            var oldParts = before.Split(new[] { " | " }, StringSplitOptions.None);
            var newParts = after.Split(new[] { " | " }, StringSplitOptions.None);
            var changes = new List<string>();
            if (oldParts[0] != newParts[0])
            {
                changes.Add(newParts[0]);
            }

            static string TypeOf(string part) => part.Split(':')[0];
            var oldTypes = oldParts.Skip(1).ToList();
            var newTypes = newParts.Skip(1).ToList();
            changes.AddRange(newTypes.Where(p => oldTypes.All(o => TypeOf(o) != TypeOf(p))).Select(p => "+" + TypeOf(p)));
            changes.AddRange(oldTypes.Where(p => newTypes.All(n => TypeOf(n) != TypeOf(p))).Select(p => "-" + TypeOf(p)));
            changes.AddRange(newTypes.Where(p => oldTypes.Any(o => TypeOf(o) == TypeOf(p) && o != p)).Select(p => "~" + TypeOf(p)));
            return changes.Count == 0 ? "changed" : string.Join(", ", changes.Distinct());
        }

        private static uint Hash(string value)
        {
            // FNV-1a: stable across domain reloads, unlike string.GetHashCode.
            var hash = 2166136261u;
            foreach (var ch in value)
            {
                hash = (hash ^ ch) * 16777619u;
            }

            return hash;
        }

        // ── Files ───────────────────────────────────────────────────────────────────────────────

        private static List<SandboxFile> ScanFiles()
        {
            var files = new List<SandboxFile>();
            foreach (var root in ScanRoots)
            {
                var full = Path.Combine(ProjectRoot, root);
                if (!Directory.Exists(full))
                {
                    continue;
                }

                foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                {
                    files.Add(new SandboxFile
                    {
                        path = Relative(entry.FullName),
                        directory = entry is DirectoryInfo,
                        size = entry is FileInfo file ? file.Length : 0,
                        modifiedTicks = entry is FileInfo ? entry.LastWriteTimeUtc.Ticks : 0,
                    });
                }
            }

            return files;
        }

        private static void RevertFiles(SandboxState state, SandboxReport report)
        {
            var before = state.files.ToDictionary(f => f.path, f => f, StringComparer.Ordinal);
            var now = ScanFiles();
            var nowPaths = new HashSet<string>(now.Select(f => f.path), StringComparer.Ordinal);

            // Deepest first, so a created folder's contents go before the folder.
            foreach (var created in now.Where(f => !before.ContainsKey(f.path)).OrderByDescending(f => f.path.Length))
            {
                var full = Path.Combine(ProjectRoot, created.path);
                try
                {
                    if (created.directory)
                    {
                        if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                        {
                            Directory.Delete(full);
                        }
                    }
                    else if (File.Exists(full))
                    {
                        File.Delete(full);
                    }

                    report.filesCreated.Add(created.path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    report.notReverted.Add($"could not delete created {created.path}: {ex.Message}");
                }
            }

            if (report.filesCreated.Count > 0)
            {
                report.reverts.Add($"deleted {report.filesCreated.Count} created file(s)");
            }

            foreach (var old in state.files.Where(f => !f.directory))
            {
                var current = now.FirstOrDefault(f => f.path == old.path);
                var missing = !nowPaths.Contains(old.path);
                var modified = !missing && current is not null && (current.size != old.size || current.modifiedTicks != old.modifiedTicks);
                if (!missing && !modified)
                {
                    continue;
                }

                var backup = Path.Combine(BackupDirectory, old.path);
                if (File.Exists(backup))
                {
                    var full = Path.Combine(ProjectRoot, old.path);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.Copy(backup, full, overwrite: true);
                    File.SetLastWriteTimeUtc(full, new DateTime(old.modifiedTicks, DateTimeKind.Utc));
                    report.filesRestored.Add(old.path);
                }
                else
                {
                    report.notReverted.Add($"{(missing ? "deleted" : "modified")} on disk: {old.path}");
                }
            }

            foreach (var old in state.files.Where(f => f.directory && !nowPaths.Contains(f.path)))
            {
                report.notReverted.Add($"deleted on disk: {old.path}/");
            }
        }

        private static void BackUpFiles()
        {
            if (Directory.Exists(BackupDirectory))
            {
                Directory.Delete(BackupDirectory, recursive: true);
            }

            var paths = BackedUpFiles.ToList();
            var settings = Path.Combine(ProjectRoot, BackedUpDirectory);
            if (Directory.Exists(settings))
            {
                paths.AddRange(Directory.EnumerateFiles(settings, "*", SearchOption.AllDirectories).Select(Relative));
            }

            foreach (var path in paths)
            {
                var source = Path.Combine(ProjectRoot, path);
                if (!File.Exists(source))
                {
                    continue;
                }

                var target = Path.Combine(BackupDirectory, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }
        }

        private static string Relative(string fullPath) =>
            fullPath.Substring(ProjectRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');

        // ── State ───────────────────────────────────────────────────────────────────────────────

        private static void Save()
        {
            if (s_State is null)
            {
                return;
            }

            Directory.CreateDirectory(SandboxDirectory);
            File.WriteAllText(StatePath, JsonUtility.ToJson(s_State));
        }

        private static void DeleteState()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    File.Delete(StatePath);
                }

                if (Directory.Exists(BackupDirectory))
                {
                    Directory.Delete(BackupDirectory, recursive: true);
                }
            }
            catch (IOException ex)
            {
                Debug.LogWarning($"[unifocl] could not clear the sandbox state: {ex.Message}");
            }
        }

        private static void Trim(SandboxReport report)
        {
            static void Cap(List<string> list)
            {
                if (list.Count > MaxReportedItems)
                {
                    var extra = list.Count - MaxReportedItems;
                    list.RemoveRange(MaxReportedItems, extra);
                    list.Add($"... and {extra} more");
                }
            }

            Cap(report.objectsAdded);
            Cap(report.objectsRemoved);
            Cap(report.objectsModified);
            Cap(report.filesCreated);
            Cap(report.filesRestored);
            Cap(report.notReverted);
        }

        // ── Models ──────────────────────────────────────────────────────────────────────────────

        [Serializable]
        private sealed class SandboxState
        {
            public string id = string.Empty;
            public string target = string.Empty;
            public string startedAtUtc = string.Empty;
            public string expiresAtUtc = string.Empty;
            public int undoGroup;
            public bool fingerprintsTruncated;
            public List<SandboxScene> scenes = new();
            public List<SandboxObject> objects = new();
            public List<SandboxFile> files = new();
            public List<string> blocked = new();
        }

        [Serializable]
        private sealed class SandboxScene
        {
            public string path = string.Empty;
            public bool isLoaded;
            public bool isActive;
            public bool wasDirty;
        }

        [Serializable]
        private sealed class SandboxObject
        {
            public string key = string.Empty;
            public string state = string.Empty;
        }

        [Serializable]
        private sealed class SandboxFile
        {
            public string path = string.Empty;
            public bool directory;
            public long size;
            public long modifiedTicks;
        }
    }

    internal sealed class SandboxBeginInfo
    {
        public string id = string.Empty;
        public string expiresAtUtc = string.Empty;
    }

    [Serializable]
    internal sealed class SandboxStatus
    {
        public bool active;
        public string id = string.Empty;
        public string target = string.Empty;
        public string startedAtUtc = string.Empty;
        public string expiresAtUtc = string.Empty;
    }

    /// <summary>What a sandbox session changed, what ending it undid, and what it could not undo.</summary>
    [Serializable]
    internal sealed class SandboxReport
    {
        public string id = string.Empty;
        public string target = string.Empty;
        public string startedAtUtc = string.Empty;
        public string endedAtUtc = string.Empty;

        /// <summary>True when everything the session changed was undone.</summary>
        public bool reverted;

        /// <summary>The scene objects the session added, removed or changed, before they were undone.</summary>
        public List<string> objectsAdded = new();
        public List<string> objectsRemoved = new();
        public List<string> objectsModified = new();

        /// <summary>Component state is only compared for the first objects of a very large scene.</summary>
        public bool fingerprintsTruncated;

        /// <summary>AssetDatabase saves, moves and deletes the session refused.</summary>
        public List<string> blocked = new();

        /// <summary>Files the session created, which ending it deleted.</summary>
        public List<string> filesCreated = new();

        /// <summary>Settings and package manifests the session changed, which ending it restored.</summary>
        public List<string> filesRestored = new();

        /// <summary>How ending the session undid the work, beyond Unity's Undo.</summary>
        public List<string> reverts = new();

        /// <summary>Changes that are still in place.</summary>
        public List<string> notReverted = new();
    }
}
#endif
