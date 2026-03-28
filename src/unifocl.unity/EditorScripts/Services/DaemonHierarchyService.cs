#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityText = UnityEngine.UI.Text;

namespace UniFocl.EditorBridge
{
    internal static class DaemonHierarchyService
    {
        private static readonly object Sync = new();
        private const int SceneRootNodeId = int.MaxValue;
        private static int _snapshotVersion = 1;
        private static int _lastSnapshotHash;
        private static bool _hasSnapshotHash;
        private static GameObject? _loadedPrefabRoot;
        private static string _loadedPrefabPath = string.Empty;

        private sealed class UiTextMirror
        {
            public string Name { get; set; } = "Text";
            public string Text { get; set; } = "New Text";
            public Color Color { get; set; } = Color.black;
            public TextAnchor Alignment { get; set; } = TextAnchor.MiddleCenter;
        }

        public static string BuildSnapshotPayload()
        {
            lock (Sync)
            {
                var snapshot = BuildSnapshot();
                return JsonUtility.ToJson(snapshot);
            }
        }

        public static bool TryLoadPrefabSnapshotRoot(string prefabPath, out string? error)
        {
            error = null;
            lock (Sync)
            {
                var normalizedPath = NormalizeAssetPath(prefabPath);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    error = "prefab path is empty";
                    return false;
                }

                if (_loadedPrefabRoot is not null
                    && _loadedPrefabRoot
                    && _loadedPrefabPath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                try
                {
                    ClearLoadedPrefabSnapshotRootNoLock();
                    _loadedPrefabRoot = PrefabUtility.LoadPrefabContents(normalizedPath);
                    _loadedPrefabPath = normalizedPath;
                    if (_loadedPrefabRoot is null)
                    {
                        error = $"prefab load failed: {normalizedPath}";
                        _loadedPrefabPath = string.Empty;
                        return false;
                    }

                    _snapshotVersion++;
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _loadedPrefabRoot = null;
                    _loadedPrefabPath = string.Empty;
                    return false;
                }
            }
        }

        public static void ClearLoadedPrefabSnapshotRoot()
        {
            lock (Sync)
            {
                ClearLoadedPrefabSnapshotRootNoLock();
            }
        }

        public static void PersistLoadedPrefabSnapshotRootIfAny(string source, bool markDirty)
        {
            lock (Sync)
            {
                if (_loadedPrefabRoot is null
                    || !_loadedPrefabRoot
                    || string.IsNullOrWhiteSpace(_loadedPrefabPath))
                {
                    return;
                }

                try
                {
                    if (markDirty)
                    {
                        EditorUtility.SetDirty(_loadedPrefabRoot);
                    }

                    var saved = PrefabUtility.SaveAsPrefabAsset(_loadedPrefabRoot, _loadedPrefabPath);
                    if (saved is null)
                    {
                        Debug.LogWarning(
                            $"[unifocl] Failed to save loaded prefab contents after {source}: '{_loadedPrefabPath}'.");
                        return;
                    }

                    AssetDatabase.SaveAssets();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[unifocl] Failed to persist loaded prefab contents after {source}: {ex.Message}");
                }
            }
        }

        public static bool HasLoadedMutationContext()
        {
            lock (Sync)
            {
                if (_loadedPrefabRoot is not null && _loadedPrefabRoot)
                {
                    return true;
                }

                return DaemonSceneManager.TryGetActiveScene(out _);
            }
        }

        public static bool TryGetCurrentHierarchyRoots(out string rootLabel, out GameObject[] roots)
        {
            lock (Sync)
            {
                if (_loadedPrefabRoot is not null && _loadedPrefabRoot)
                {
                    var prefabName = string.IsNullOrWhiteSpace(_loadedPrefabPath)
                        ? _loadedPrefabRoot.name
                        : Path.GetFileNameWithoutExtension(_loadedPrefabPath);
                    rootLabel = $"Prefab: {prefabName}";
                    roots = new[] { _loadedPrefabRoot };
                    return true;
                }

                var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
                if (prefabStage?.prefabContentsRoot is not null)
                {
                    var prefabPath = (prefabStage.assetPath ?? string.Empty).Replace('\\', '/');
                    var prefabName = string.IsNullOrWhiteSpace(prefabPath)
                        ? prefabStage.prefabContentsRoot.name
                        : Path.GetFileNameWithoutExtension(prefabPath);
                    rootLabel = $"Prefab: {prefabName}";
                    roots = new[] { prefabStage.prefabContentsRoot };
                    return true;
                }

                if (DaemonSceneManager.TryGetActiveScene(out var scene))
                {
                    rootLabel = ResolveSceneName(scene);
                    roots = scene.GetRootGameObjects();
                    return true;
                }
            }

            rootLabel = "No Scene";
            roots = Array.Empty<GameObject>();
            return false;
        }

        public static string ExecuteCommand(string payload)
        {
            HierarchyCommandRequest? request;
            try
            {
                request = JsonUtility.FromJson<HierarchyCommandRequest>(payload);
            }
            catch
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "invalid hierarchy command payload" });
            }

            if (request is null || string.IsNullOrWhiteSpace(request.action))
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "missing hierarchy command payload" });
            }

            if (DaemonMutationTransactionCoordinator.IsHierarchyMutation(request.action)
                && !HasLoadedMutationContext())
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse
                {
                    ok = false,
                    message = "no loaded scene or prefab context; load a scene/prefab first"
                });
            }

            if (DaemonMutationTransactionCoordinator.IsHierarchyMutation(request.action))
            {
                var decision = DaemonMutationTransactionCoordinator.ValidateHierarchyIntent(request.action, request.intent);
                if (!decision.Accepted)
                {
                    return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = decision.Message });
                }

                if (decision.IsDryRun)
                {
                    return ExecuteDryRunCommand(request);
                }
            }

            lock (Sync)
            {
                return ExecuteCommandCore(request);
            }
        }

        private static string ExecuteDryRunCommand(HierarchyCommandRequest request)
        {
            lock (Sync)
            {
                var beforeSnapshotVersion = _snapshotVersion;
                var beforeHash = _lastSnapshotHash;
                var beforeHasHash = _hasSnapshotHash;
                var snapshotTarget = ResolveDryRunSnapshotTarget(request);
                var beforeJson = snapshotTarget is null
                    ? BuildSnapshotPayload()
                    : DaemonDryRunDiffService.SnapshotObject(snapshotTarget);
                var undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("unifocl hierarchy dry-run");

                // Capture which scenes were already dirty before the dry-run so we can
                // restore clean scenes that Undo.RevertAllDownToGroup leaves dirty.
                var (preDryRunScenes, preDryRunDirty) = DaemonDryRunSceneRestoreService.CaptureDirtyState();

                string? failedPayload = null;
                HierarchyCommandResponse? successParsed = null;
                string? afterJson = null;

                try
                {
                    // Use an explicit using-block (not `using var`) so the dry-run scope
                    // exits before the scene-restore call below, which needs IsActive = false
                    // to allow EditorSceneManager.SaveScene to write through.
                    using (DaemonDryRunContext.Enter())
                    {
                        var responsePayload = ExecuteCommandCore(request);
                        var parsed = JsonUtility.FromJson<HierarchyCommandResponse>(responsePayload);
                        if (parsed is null || !parsed.ok)
                        {
                            Undo.RevertAllDownToGroup(undoGroup);
                            _snapshotVersion = beforeSnapshotVersion;
                            _lastSnapshotHash = beforeHash;
                            _hasSnapshotHash = beforeHasHash;
                            failedPayload = responsePayload;
                        }
                        else
                        {
                            afterJson = snapshotTarget is null
                                ? BuildSnapshotPayload()
                                : DaemonDryRunDiffService.SnapshotObject(snapshotTarget);
                            Undo.RevertAllDownToGroup(undoGroup);
                            _snapshotVersion = beforeSnapshotVersion;
                            _lastSnapshotHash = beforeHash;
                            _hasSnapshotHash = beforeHasHash;
                            successParsed = parsed;
                        }
                    }

                    // DaemonDryRunContext.IsActive is now false — safe to save scenes.
                    // Saves scenes that became dirty as a side-effect of the undo, and for
                    // UVC-controlled (read-only) scenes performs checkout → save → revert so
                    // no spurious VCS checkout is left for the user to clean up manually.
                    DaemonDryRunSceneRestoreService.RestorePreviouslyCleanScenes(preDryRunScenes, preDryRunDirty);

                    if (failedPayload is not null)
                    {
                        return failedPayload;
                    }

                    successParsed!.message = "dry-run preview";
                    successParsed.content = DaemonDryRunDiffService.BuildJsonDiffPayload("hierarchy mutation preview", beforeJson, afterJson!);
                    return JsonUtility.ToJson(successParsed);
                }
                catch (Exception ex)
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                    _snapshotVersion = beforeSnapshotVersion;
                    _lastSnapshotHash = beforeHash;
                    _hasSnapshotHash = beforeHasHash;
                    // The using-block disposes the scope before the catch runs, so
                    // IsActive is already false here — safe to call RestorePreviouslyCleanScenes.
                    DaemonDryRunSceneRestoreService.RestorePreviouslyCleanScenes(preDryRunScenes, preDryRunDirty);
                    return JsonUtility.ToJson(new HierarchyCommandResponse
                    {
                        ok = false,
                        message = $"hierarchy dry-run failed: {ex.Message}"
                    });
                }
            }
        }

        private static string ExecuteCommandCore(HierarchyCommandRequest request)
        {
            if (request.action.Equals("mk", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteCreate(request);
            }

            if (request.action.Equals("toggle", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteToggle(request);
            }

            if (request.action.Equals("rm", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteRemove(request);
            }

            if (request.action.Equals("rename", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteRename(request);
            }

            if (request.action.Equals("mv", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteMove(request);
            }

            return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = $"unsupported action: {request.action}" });
        }

        private static GameObject? ResolveDryRunSnapshotTarget(HierarchyCommandRequest request)
        {
            if (request.targetId == 0)
            {
                return null;
            }

            return EditorUtility.InstanceIDToObject(request.targetId) as GameObject;
        }

        public static string ExecuteSearch(string payload)
        {
            HierarchySearchRequest? request;
            try
            {
                request = JsonUtility.FromJson<HierarchySearchRequest>(payload);
            }
            catch
            {
                return JsonUtility.ToJson(new HierarchySearchResponse
                {
                    ok = false,
                    message = "invalid hierarchy search payload",
                    results = Array.Empty<HierarchySearchResult>()
                });
            }

            if (request is null || string.IsNullOrWhiteSpace(request.query))
            {
                return JsonUtility.ToJson(new HierarchySearchResponse
                {
                    ok = false,
                    message = "search query is required",
                    results = Array.Empty<HierarchySearchResult>()
                });
            }

            var matches = new List<HierarchySearchResult>();
            lock (Sync)
            {
                var snapshot = BuildSnapshot();
                var origin = request.parentId != 0
                    ? FindNode(snapshot.root, request.parentId) ?? snapshot.root
                    : snapshot.root;
                var originPath = BuildPath(snapshot.root, origin.id);
                CollectMatches(origin, originPath, request.query, matches);
            }

            var limit = Mathf.Clamp(request.limit <= 0 ? 20 : request.limit, 1, 50);
            var top = matches
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.path, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToArray();

            return JsonUtility.ToJson(new HierarchySearchResponse
            {
                ok = true,
                results = top,
                message = string.Empty
            });
        }

        private static string ExecuteCreate(HierarchyCommandRequest request)
        {
            if (!DaemonSceneManager.TryGetActiveScene(out var activeScene))
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "active scene is not valid" });
            }

            var normalizedType = NormalizeMkType(request);
            if (string.IsNullOrWhiteSpace(normalizedType))
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "mk requires a type (e.g. mk Canvas)" });
            }

            var count = Mathf.Clamp(request.count <= 0 ? 1 : request.count, 1, 100);
            if (normalizedType.Equals("LegacyNamedEmpty", StringComparison.OrdinalIgnoreCase))
            {
                count = 1;
            }

            var parentId = request.parentId != 0 ? request.parentId : SceneRootNodeId;
            Transform? parentTransform = null;
            Scene parentScene = activeScene;
            if (parentId != SceneRootNodeId)
            {
                var parentObject = EditorUtility.InstanceIDToObject(parentId) as GameObject;
                if (parentObject is null)
                {
                    return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = $"parent id not found: {parentId}" });
                }

                parentTransform = parentObject.transform;
                parentScene = parentObject.scene;
            }

            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("unifocl hierarchy create");
            GameObject? lastCreated = null;
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var created = CreateTypedObject(normalizedType, request, parentTransform, parentScene, activeScene, i, out var error);
                    if (created is null)
                    {
                        Undo.RevertAllDownToGroup(undoGroup);
                        return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = error ?? $"mk failed for type: {normalizedType}" });
                    }

                    Undo.RegisterCreatedObjectUndo(created, "unifocl create hierarchy object");
                    DaemonScenePersistenceService.RecordPrefabInstanceMutation(created);
                    lastCreated = created;
                }
            }
            catch (Exception ex)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = $"mk failed: {ex.Message}" });
            }

            if (lastCreated is null)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "mk did not create any objects" });
            }

            Undo.CollapseUndoOperations(undoGroup);
            DaemonScenePersistenceService.PersistMutationScenes("hierarchy mutation", lastCreated.scene);
            _snapshotVersion++;

            return JsonUtility.ToJson(new HierarchyCommandResponse
            {
                ok = true,
                message = count <= 1 ? $"created {normalizedType}" : $"created {normalizedType} x{count}",
                nodeId = lastCreated.GetInstanceID(),
                isActive = lastCreated.activeSelf,
                assignedName = lastCreated.name
            });
        }

        private static string NormalizeMkType(HierarchyCommandRequest request)
        {
            if (request.primitive)
            {
                return "Cube";
            }

            if (!string.IsNullOrWhiteSpace(request.type))
            {
                return request.type.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.name))
            {
                return "LegacyNamedEmpty";
            }

            return string.Empty;
        }

        private static GameObject? CreateTypedObject(
            string rawType,
            HierarchyCommandRequest request,
            Transform? parentTransform,
            Scene parentScene,
            Scene activeScene,
            int iteration,
            out string? error)
        {
            error = null;
            var explicitName = string.IsNullOrWhiteSpace(request.name) ? null : request.name.Trim();
            var normalized = rawType.Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();

            var uiResources = new DefaultControls.Resources();
            switch (normalized)
            {
                case "legacynamedempty":
                    return CreateAndParent(new GameObject(request.name.Trim()), parentTransform, parentScene, activeScene, explicitName);
                case "canvas":
                    return CreateCanvas(parentTransform, parentScene, activeScene, explicitName);
                case "panel":
                    return CreateUiControl(DefaultControls.CreatePanel(uiResources), "Panel", parentTransform, parentScene, activeScene, explicitName);
                case "text":
                case "tmp":
                    return CreateUiControl(
                        CreateTextFromMirror(new UiTextMirror
                        {
                            Name = explicitName ?? (normalized == "tmp" ? "Text (TMP)" : "Text"),
                            Text = "New Text",
                            Color = Color.black,
                            Alignment = TextAnchor.MiddleCenter
                        }),
                        "Text",
                        parentTransform,
                        parentScene,
                        activeScene,
                        explicitName);
                case "image":
                    return CreateUiControl(DefaultControls.CreateImage(uiResources), "Image", parentTransform, parentScene, activeScene, explicitName);
                case "button":
                    return CreateUiControl(CreateButtonFromMirror(uiResources), "Button", parentTransform, parentScene, activeScene, explicitName);
                case "toggle":
                    return CreateUiControl(CreateToggleFromMirror(uiResources), "Toggle", parentTransform, parentScene, activeScene, explicitName);
                case "slider":
                    return CreateUiControl(DefaultControls.CreateSlider(uiResources), "Slider", parentTransform, parentScene, activeScene, explicitName);
                case "scrollbar":
                    return CreateUiControl(DefaultControls.CreateScrollbar(uiResources), "Scrollbar", parentTransform, parentScene, activeScene, explicitName);
                case "scrollview":
                    return CreateUiControl(DefaultControls.CreateScrollView(uiResources), "Scroll View", parentTransform, parentScene, activeScene, explicitName);
                case "eventsystem":
                    return CreateEventSystem(parentTransform, parentScene, activeScene, explicitName);
                case "cube":
                    return CreateAndParent(GameObject.CreatePrimitive(PrimitiveType.Cube), parentTransform, parentScene, activeScene, explicitName);
                case "sphere":
                    return CreateAndParent(GameObject.CreatePrimitive(PrimitiveType.Sphere), parentTransform, parentScene, activeScene, explicitName);
                case "capsule":
                    return CreateAndParent(GameObject.CreatePrimitive(PrimitiveType.Capsule), parentTransform, parentScene, activeScene, explicitName);
                case "cylinder":
                    return CreateAndParent(GameObject.CreatePrimitive(PrimitiveType.Cylinder), parentTransform, parentScene, activeScene, explicitName);
                case "plane":
                    return CreateAndParent(GameObject.CreatePrimitive(PrimitiveType.Plane), parentTransform, parentScene, activeScene, explicitName);
                case "quad":
                    return CreateAndParent(GameObject.CreatePrimitive(PrimitiveType.Quad), parentTransform, parentScene, activeScene, explicitName);
                case "dirlight":
                case "directionallight":
                    return CreateLight(explicitName ?? "Directional Light", LightType.Directional, parentTransform, parentScene, activeScene, explicitName);
                case "pointlight":
                    return CreateLight(explicitName ?? "Point Light", LightType.Point, parentTransform, parentScene, activeScene, explicitName);
                case "spotlight":
                    return CreateLight(explicitName ?? "Spot Light", LightType.Spot, parentTransform, parentScene, activeScene, explicitName);
                case "arealight":
                case "reflectionprobe":
                    return CreateReflectionProbe(parentTransform, parentScene, activeScene, explicitName);
                case "sprite":
                    return CreateAndParent(new GameObject("Sprite", typeof(SpriteRenderer)), parentTransform, parentScene, activeScene, explicitName);
                case "spritemask":
                    return CreateAndParent(new GameObject("Sprite Mask", typeof(SpriteMask)), parentTransform, parentScene, activeScene, explicitName);
                case "camera":
                    return CreateAndParent(new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)), parentTransform, parentScene, activeScene, explicitName);
                case "audiosource":
                    return CreateAndParent(new GameObject("Audio Source", typeof(AudioSource)), parentTransform, parentScene, activeScene, explicitName);
                case "empty":
                    return CreateAndParent(new GameObject("GameObject"), parentTransform, parentScene, activeScene, explicitName);
                case "emptychild":
                {
                    var childParent = ResolveTargetTransform(request.targetId, parentTransform, out error);
                    if (childParent is null && request.targetId != 0)
                    {
                        return null;
                    }

                    // When childParent is null (scene root), fall through to scene-root creation
                    // just like the "empty" case — parentTransform == null is valid.
                    var effectiveParent = childParent ?? parentTransform;
                    var effectiveScene = effectiveParent is not null ? effectiveParent.gameObject.scene : activeScene;
                    return CreateAndParent(new GameObject("GameObject"), effectiveParent, effectiveScene, activeScene, explicitName);
                }
                case "emptyparent":
                    return CreateEmptyParent(request.targetId, activeScene, explicitName, out error);
                default:
                    error = $"unsupported mk type: {rawType}";
                    return null;
            }
        }

        private static Transform? ResolveTargetTransform(int targetId, Transform? fallback, out string? error)
        {
            error = null;
            if (targetId == 0)
            {
                return fallback;
            }

            var targetObject = EditorUtility.InstanceIDToObject(targetId) as GameObject;
            if (targetObject is null)
            {
                error = $"target id not found: {targetId}";
                return null;
            }

            return targetObject.transform;
        }

        private static GameObject CreateAndParent(GameObject created, Transform? parentTransform, Scene parentScene, Scene activeScene, string? explicitName = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitName))
            {
                created.name = explicitName!;
            }

            if (parentTransform is not null)
            {
                // Move the root object to destination scene before parenting.
                // Moving after parenting can throw "Gameobject is not a root in a scene".
                if (created.scene != parentScene)
                {
                    SceneManager.MoveGameObjectToScene(created, parentScene);
                }

                // Check uniqueness BEFORE parenting: GetUniqueNameForSibling inspects existing
                // siblings of parentTransform. If called after parenting, the newly added object
                // is already in the sibling list and the name appears "taken", causing Unity to
                // append " (1)" even when no conflict exists.
                created.name = GameObjectUtility.GetUniqueNameForSibling(parentTransform, created.name);
                Undo.SetTransformParent(created.transform, parentTransform, "unifocl create hierarchy object");
                return created;
            }

            SceneManager.MoveGameObjectToScene(created, activeScene);
            return created;
        }

        private static GameObject CreateCanvas(Transform? parentTransform, Scene parentScene, Scene activeScene, string? explicitName = null)
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvasComponent = canvas.GetComponent<Canvas>();
            if (canvasComponent is not null)
            {
                canvasComponent.renderMode = RenderMode.ScreenSpaceOverlay;
            }

            return CreateAndParent(canvas, parentTransform, parentScene, activeScene, explicitName);
        }

        private static GameObject CreateUiControl(GameObject created, string fallbackName, Transform? parentTransform, Scene parentScene, Scene activeScene, string? explicitName = null)
        {
            var effectiveParent = parentTransform;
            var effectiveScene = parentScene;
            if (effectiveParent is null)
            {
                var canvas = EnsureSceneCanvas(activeScene);
                effectiveParent = canvas.transform;
                effectiveScene = canvas.scene;
            }

            EnsureEventSystemExists(activeScene);
            created.name = !string.IsNullOrWhiteSpace(explicitName)
                ? explicitName!
                : (string.IsNullOrWhiteSpace(created.name) ? fallbackName : created.name);
            return CreateAndParent(created, effectiveParent, effectiveScene, activeScene, explicitName);
        }

        private static GameObject CreateTextFromMirror(UiTextMirror mirror)
        {
            var go = new GameObject(mirror.Name, typeof(RectTransform), typeof(UnityText));
            var uiText = go.GetComponent<UnityText>();
            if (uiText is not null)
            {
                uiText.text = mirror.Text;
                uiText.color = mirror.Color;
                uiText.alignment = mirror.Alignment;
            }

            return go;
        }

        private static GameObject CreateButtonFromMirror(DefaultControls.Resources resources)
        {
            var button = DefaultControls.CreateButton(resources);
            ApplyTextMirrors(button);
            return button;
        }

        private static GameObject CreateToggleFromMirror(DefaultControls.Resources resources)
        {
            var toggle = DefaultControls.CreateToggle(resources);
            ApplyTextMirrors(toggle);
            return toggle;
        }

        private static void ApplyTextMirrors(GameObject root)
        {
            var legacyTexts = root.GetComponentsInChildren<UnityText>(true);
            foreach (var legacy in legacyTexts)
            {
                var mirror = new UiTextMirror
                {
                    Name = legacy.gameObject.name,
                    Text = string.IsNullOrWhiteSpace(legacy.text) ? "Text" : legacy.text,
                    Color = legacy.color,
                    Alignment = legacy.alignment
                };

                legacy.text = mirror.Text;
                legacy.color = mirror.Color;
                legacy.alignment = mirror.Alignment;
            }
        }

        private static GameObject CreateLight(string name, LightType type, Transform? parentTransform, Scene parentScene, Scene activeScene, string? explicitName = null)
        {
            var go = new GameObject(name, typeof(Light));
            var light = go.GetComponent<Light>();
            if (light is not null)
            {
                light.type = type;
            }

            return CreateAndParent(go, parentTransform, parentScene, activeScene, explicitName);
        }

        private static GameObject CreateReflectionProbe(Transform? parentTransform, Scene parentScene, Scene activeScene, string? explicitName = null)
        {
            var probe = new GameObject("Reflection Probe", typeof(ReflectionProbe));
            return CreateAndParent(probe, parentTransform, parentScene, activeScene, explicitName);
        }

        private static GameObject CreateEventSystem(Transform? parentTransform, Scene parentScene, Scene activeScene, string? explicitName = null)
        {
            var eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            return CreateAndParent(eventSystem, parentTransform, parentScene, activeScene, explicitName);
        }

        private static GameObject EnsureSceneCanvas(Scene scene)
        {
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (canvas.gameObject.scene == scene)
                {
                    return canvas.gameObject;
                }
            }

            var created = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvasComponent = created.GetComponent<Canvas>();
            if (canvasComponent is not null)
            {
                canvasComponent.renderMode = RenderMode.ScreenSpaceOverlay;
            }

            SceneManager.MoveGameObjectToScene(created, scene);
            return created;
        }

        private static void EnsureEventSystemExists(Scene scene)
        {
            foreach (var eventSystem in UnityEngine.Object.FindObjectsOfType<EventSystem>())
            {
                if (eventSystem.gameObject.scene == scene)
                {
                    return;
                }
            }

            var created = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            SceneManager.MoveGameObjectToScene(created, scene);
        }

        private static GameObject? CreateEmptyParent(int targetId, Scene activeScene, string? explicitName, out string? error)
        {
            error = null;
            if (targetId == 0)
            {
                error = "EmptyParent requires target id";
                return null;
            }

            var target = EditorUtility.InstanceIDToObject(targetId) as GameObject;
            if (target is null)
            {
                error = $"target id not found: {targetId}";
                return null;
            }

            var wrapper = new GameObject(string.IsNullOrWhiteSpace(explicitName) ? "GameObject" : explicitName!);
            var previousParent = target.transform.parent;
            if (previousParent is not null)
            {
                Undo.SetTransformParent(wrapper.transform, previousParent, "unifocl create empty parent");
                SceneManager.MoveGameObjectToScene(wrapper, previousParent.gameObject.scene);
            }
            else
            {
                SceneManager.MoveGameObjectToScene(wrapper, target.scene.IsValid() ? target.scene : activeScene);
            }

            wrapper.transform.position = target.transform.position;
            wrapper.transform.rotation = target.transform.rotation;
            Undo.SetTransformParent(target.transform, wrapper.transform, "unifocl create empty parent");
            DaemonScenePersistenceService.RecordPrefabInstanceMutation(target);
            DaemonScenePersistenceService.RecordPrefabInstanceMutation(wrapper);
            return wrapper;
        }

        private static string ExecuteToggle(HierarchyCommandRequest request)
        {
            if (request.targetId == 0)
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "toggle requires target id" });
            }

            var target = EditorUtility.InstanceIDToObject(request.targetId) as GameObject;
            if (target is null)
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = $"target id not found: {request.targetId}" });
            }

            var nextValue = !target.activeSelf;
            if (!TrySetGameObjectBooleanProperty(target, "m_IsActive", nextValue, out var changed, out var error))
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = error ?? "toggle failed" });
            }

            if (!changed)
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse
                {
                    ok = true,
                    message = "unchanged",
                    nodeId = target.GetInstanceID(),
                    isActive = target.activeSelf
                });
            }

            DaemonScenePersistenceService.PersistMutationScenes("hierarchy mutation", target.scene);
            _snapshotVersion++;

            return JsonUtility.ToJson(new HierarchyCommandResponse
            {
                ok = true,
                message = "toggled",
                nodeId = target.GetInstanceID(),
                isActive = target.activeSelf
            });
        }

        private static string ExecuteRemove(HierarchyCommandRequest request)
        {
            if (request.targetId == 0)
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "rm requires target id" });
            }

            var target = EditorUtility.InstanceIDToObject(request.targetId) as GameObject;
            if (target is null)
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = $"target id not found: {request.targetId}" });
            }

            var scene = target.scene;
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("unifocl hierarchy remove");
            try
            {
                Undo.DestroyObjectImmediate(target);
                Undo.CollapseUndoOperations(undoGroup);
            }
            catch (Exception ex)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = $"remove failed: {ex.Message}" });
            }

            if (scene.IsValid())
            {
                DaemonScenePersistenceService.PersistMutationScenes("hierarchy mutation", scene);
            }

            _snapshotVersion++;

            return JsonUtility.ToJson(new HierarchyCommandResponse
            {
                ok = true,
                message = "removed",
                nodeId = request.targetId,
                isActive = false
            });
        }

        private static string ExecuteRename(HierarchyCommandRequest request)
        {
            if (request.targetId == 0)
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "rename requires target id" });
            }

            if (string.IsNullOrWhiteSpace(request.name))
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "rename requires a name" });
            }

            var target = EditorUtility.InstanceIDToObject(request.targetId) as GameObject;
            if (target is null)
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = $"target id not found: {request.targetId}" });
            }

            if (!TrySetGameObjectStringProperty(target, "m_Name", request.name.Trim(), out var changed, out var error))
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = error ?? "rename failed" });
            }

            if (!changed)
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse
                {
                    ok = true,
                    message = "unchanged",
                    nodeId = target.GetInstanceID(),
                    isActive = target.activeSelf,
                    assignedName = target.name
                });
            }

            DaemonScenePersistenceService.PersistMutationScenes("hierarchy mutation", target.scene);
            _snapshotVersion++;

            return JsonUtility.ToJson(new HierarchyCommandResponse
            {
                ok = true,
                message = "renamed",
                nodeId = target.GetInstanceID(),
                isActive = target.activeSelf,
                assignedName = target.name
            });
        }

        private static string ExecuteMove(HierarchyCommandRequest request)
        {
            if (request.targetId == 0)
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "mv requires target id" });
            }

            var target = EditorUtility.InstanceIDToObject(request.targetId) as GameObject;
            if (target is null)
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = $"target id not found: {request.targetId}" });
            }

            var nextParentId = request.parentId == 0 ? SceneRootNodeId : request.parentId;
            if (nextParentId == request.targetId)
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "cannot move object under itself" });
            }

            if (nextParentId == SceneRootNodeId)
            {
                if (!DaemonSceneManager.TryGetActiveScene(out var activeScene))
                {
                    return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "active scene is not valid" });
                }

                Undo.SetTransformParent(target.transform, null, "unifocl move hierarchy object");
                var previousScene = target.scene;
                SceneManager.MoveGameObjectToScene(target, activeScene);
                DaemonScenePersistenceService.RecordPrefabInstanceMutation(target);
                DaemonScenePersistenceService.PersistMutationScenes("hierarchy mutation", previousScene, activeScene);
                _snapshotVersion++;
                return JsonUtility.ToJson(new HierarchyCommandResponse
                {
                    ok = true,
                    message = "moved",
                    nodeId = target.GetInstanceID(),
                    isActive = target.activeSelf,
                    assignedName = target.name
                });
            }

            var parentObject = EditorUtility.InstanceIDToObject(nextParentId) as GameObject;
            if (parentObject is null)
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = $"parent id not found: {nextParentId}" });
            }

            var cursor = parentObject.transform;
            while (cursor is not null)
            {
                if (cursor == target.transform)
                {
                    return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "cannot move object under its descendant" });
                }

                cursor = cursor.parent;
            }

            var previousParentScene = target.scene;
            Undo.SetTransformParent(target.transform, parentObject.transform, "unifocl move hierarchy object");
            SceneManager.MoveGameObjectToScene(target, parentObject.scene);
            DaemonScenePersistenceService.RecordPrefabInstanceMutation(target);
            DaemonScenePersistenceService.PersistMutationScenes("hierarchy mutation", previousParentScene, parentObject.scene);
            _snapshotVersion++;

            return JsonUtility.ToJson(new HierarchyCommandResponse
            {
                ok = true,
                message = "moved",
                nodeId = target.GetInstanceID(),
                isActive = target.activeSelf,
                assignedName = target.name
            });
        }

        private static HierarchySnapshotResponse BuildSnapshot()
        {
            string sceneName;
            HierarchyNodeDto[] children;
            if (TryBuildLoadedPrefabSnapshot(out sceneName, out children))
            {
            }
            else if (!TryBuildPrefabStageSnapshot(out sceneName, out children))
            {
                var hasActiveScene = DaemonSceneManager.TryGetActiveScene(out var scene);
                sceneName = ResolveSceneName(scene);
                children = hasActiveScene
                    ? scene.GetRootGameObjects()
                        .Select(ToDto)
                        .OrderBy(node => node.name, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : Array.Empty<HierarchyNodeDto>();
            }

            var hash = ComputeHierarchyHash(sceneName, children);
            if (_hasSnapshotHash)
            {
                if (_lastSnapshotHash != hash)
                {
                    _snapshotVersion++;
                }
            }
            else
            {
                _hasSnapshotHash = true;
            }

            _lastSnapshotHash = hash;

            return new HierarchySnapshotResponse
            {
                scene = sceneName,
                snapshotVersion = _snapshotVersion,
                root = new HierarchyNodeDto
                {
                    id = SceneRootNodeId,
                    name = sceneName,
                    active = true,
                    children = children
                }
            };
        }

        private static bool TryBuildLoadedPrefabSnapshot(out string sceneName, out HierarchyNodeDto[] children)
        {
            sceneName = string.Empty;
            children = Array.Empty<HierarchyNodeDto>();

            if (_loadedPrefabRoot is null || !_loadedPrefabRoot)
            {
                return false;
            }

            var prefabName = string.IsNullOrWhiteSpace(_loadedPrefabPath)
                ? _loadedPrefabRoot.name
                : Path.GetFileNameWithoutExtension(_loadedPrefabPath);
            sceneName = $"Prefab: {prefabName}";
            children = new[]
            {
                ToDto(_loadedPrefabRoot)
            };
            return true;
        }

        private static bool TryBuildPrefabStageSnapshot(out string sceneName, out HierarchyNodeDto[] children)
        {
            sceneName = string.Empty;
            children = Array.Empty<HierarchyNodeDto>();

            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage?.prefabContentsRoot is null)
            {
                return false;
            }

            var prefabPath = (prefabStage.assetPath ?? string.Empty).Replace('\\', '/');
            var prefabName = string.IsNullOrWhiteSpace(prefabPath)
                ? prefabStage.prefabContentsRoot.name
                : Path.GetFileNameWithoutExtension(prefabPath);
            sceneName = $"Prefab: {prefabName}";
            children = new[]
            {
                ToDto(prefabStage.prefabContentsRoot)
            };
            return true;
        }

        private static void ClearLoadedPrefabSnapshotRootNoLock()
        {
            if (_loadedPrefabRoot is not null && _loadedPrefabRoot)
            {
                try
                {
                    PrefabUtility.UnloadPrefabContents(_loadedPrefabRoot);
                }
                catch
                {
                }
            }

            _loadedPrefabRoot = null;
            _loadedPrefabPath = string.Empty;
        }

        private static string NormalizeAssetPath(string? path)
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }

        private static HierarchyNodeDto ToDto(GameObject gameObject)
        {
            var transform = gameObject.transform;
            var children = new HierarchyNodeDto[transform.childCount];
            for (var i = 0; i < transform.childCount; i++)
            {
                children[i] = ToDto(transform.GetChild(i).gameObject);
            }

            return new HierarchyNodeDto
            {
                id = gameObject.GetInstanceID(),
                name = gameObject.name,
                active = gameObject.activeSelf,
                children = children
            };
        }

        private static HierarchyNodeDto? FindNode(HierarchyNodeDto node, int id)
        {
            if (node.id == id)
            {
                return node;
            }

            foreach (var child in node.children)
            {
                var found = FindNode(child, id);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        private static void CollectMatches(HierarchyNodeDto node, string path, string query, List<HierarchySearchResult> output)
        {
            if (TryScore(query, path, out var pathScore))
            {
                output.Add(new HierarchySearchResult
                {
                    nodeId = node.id,
                    path = path,
                    active = node.active,
                    score = pathScore
                });
            }
            else if (TryScore(query, node.name, out var nameScore))
            {
                output.Add(new HierarchySearchResult
                {
                    nodeId = node.id,
                    path = path,
                    active = node.active,
                    score = nameScore
                });
            }

            foreach (var child in node.children)
            {
                var childPath = path.EndsWith("/", StringComparison.Ordinal) ? path + child.name : path + "/" + child.name;
                CollectMatches(child, childPath, query, output);
            }
        }

        private static bool TryScore(string query, string candidate, out double score)
        {
            score = 0;
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            var q = query.Trim().ToLowerInvariant();
            var c = candidate.ToLowerInvariant();
            var containsIndex = c.IndexOf(q, StringComparison.Ordinal);
            if (containsIndex >= 0)
            {
                score = 1.0 - (containsIndex / (double)Math.Max(1, c.Length));
                return true;
            }

            var qi = 0;
            var ci = 0;
            var hits = 0;
            while (qi < q.Length && ci < c.Length)
            {
                if (q[qi] == c[ci])
                {
                    hits++;
                    qi++;
                }

                ci++;
            }

            if (qi != q.Length)
            {
                return false;
            }

            score = hits / (double)Math.Max(q.Length, c.Length);
            return true;
        }

        private static string BuildPath(HierarchyNodeDto root, int targetId)
        {
            var segments = new List<string>();
            if (!TryCollectPath(root, targetId, segments))
            {
                return "/" + root.name;
            }

            segments.Reverse();
            return "/" + string.Join('/', segments);
        }

        private static bool TryCollectPath(HierarchyNodeDto node, int targetId, List<string> segments)
        {
            if (node.id == targetId)
            {
                segments.Add(node.name);
                return true;
            }

            foreach (var child in node.children)
            {
                if (TryCollectPath(child, targetId, segments))
                {
                    segments.Add(node.name);
                    return true;
                }
            }

            return false;
        }

        private static string ResolveSceneName(Scene scene)
        {
            if (!scene.IsValid())
            {
                return "No Scene";
            }

            return string.IsNullOrWhiteSpace(scene.name) ? "Untitled Scene" : scene.name;
        }

        private static bool TrySetGameObjectBooleanProperty(
            GameObject target,
            string propertyPath,
            bool nextValue,
            out bool changed,
            out string? error)
        {
            changed = false;
            error = null;
            var serializedObject = new SerializedObject(target);
            var property = serializedObject.FindProperty(propertyPath);
            if (property is null || property.propertyType != SerializedPropertyType.Boolean)
            {
                error = $"property '{propertyPath}' is unavailable for toggle";
                return false;
            }

            serializedObject.Update();
            if (property.boolValue == nextValue)
            {
                return true;
            }

            Undo.RecordObject(target, "unifocl hierarchy serialized mutation");
            property.boolValue = nextValue;
            serializedObject.ApplyModifiedProperties();
            DaemonScenePersistenceService.RecordPrefabInstanceMutation(target);
            changed = true;
            return true;
        }

        private static bool TrySetGameObjectStringProperty(
            GameObject target,
            string propertyPath,
            string nextValue,
            out bool changed,
            out string? error)
        {
            changed = false;
            error = null;
            var serializedObject = new SerializedObject(target);
            var property = serializedObject.FindProperty(propertyPath);
            if (property is null || property.propertyType != SerializedPropertyType.String)
            {
                error = $"property '{propertyPath}' is unavailable for rename";
                return false;
            }

            serializedObject.Update();
            if (string.Equals(property.stringValue, nextValue, StringComparison.Ordinal))
            {
                return true;
            }

            Undo.RecordObject(target, "unifocl hierarchy serialized mutation");
            property.stringValue = nextValue;
            serializedObject.ApplyModifiedProperties();
            DaemonScenePersistenceService.RecordPrefabInstanceMutation(target);
            changed = true;
            return true;
        }

        private static int ComputeHierarchyHash(string sceneName, IReadOnlyList<HierarchyNodeDto> roots)
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(sceneName);
                for (var i = 0; i < roots.Count; i++)
                {
                    hash = (hash * 397) ^ ComputeNodeHash(roots[i]);
                }

                return hash;
            }
        }

        private static int ComputeNodeHash(HierarchyNodeDto node)
        {
            unchecked
            {
                var hash = node.id;
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(node.name);
                hash = (hash * 397) ^ (node.active ? 1 : 0);
                for (var i = 0; i < node.children.Length; i++)
                {
                    hash = (hash * 397) ^ ComputeNodeHash(node.children[i]);
                }

                return hash;
            }
        }
    }
}
#endif
