#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UniFocl.EditorBridge
{
    internal static class DaemonHierarchyService
    {
        private static readonly object Sync = new();
        private const int SceneRootNodeId = int.MaxValue;
        private static int _snapshotVersion = 1;
        private static int _lastSnapshotHash;
        private static bool _hasSnapshotHash;

        public static string BuildSnapshotPayload()
        {
            lock (Sync)
            {
                var snapshot = BuildSnapshot();
                return JsonUtility.ToJson(snapshot);
            }
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

            lock (Sync)
            {
                if (request.action.Equals("mk", StringComparison.OrdinalIgnoreCase))
                {
                    return ExecuteCreate(request);
                }

                if (request.action.Equals("toggle", StringComparison.OrdinalIgnoreCase))
                {
                    return ExecuteToggle(request);
                }

                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = $"unsupported action: {request.action}" });
            }
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
            if (string.IsNullOrWhiteSpace(request.name))
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "mk requires a name" });
            }

            var activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid())
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "active scene is not valid" });
            }

            var parentId = request.parentId != 0 ? request.parentId : SceneRootNodeId;
            var created = request.primitive
                ? GameObject.CreatePrimitive(PrimitiveType.Cube)
                : new GameObject();
            created.name = request.name.Trim();

            if (parentId == SceneRootNodeId)
            {
                SceneManager.MoveGameObjectToScene(created, activeScene);
            }
            else
            {
                var parentObject = EditorUtility.InstanceIDToObject(parentId) as GameObject;
                if (parentObject is null)
                {
                    UnityEngine.Object.DestroyImmediate(created);
                    return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = $"parent id not found: {parentId}" });
                }

                created.transform.SetParent(parentObject.transform, false);
                SceneManager.MoveGameObjectToScene(created, parentObject.scene);
            }

            EditorSceneManager.MarkSceneDirty(created.scene);
            _snapshotVersion++;

            return JsonUtility.ToJson(new HierarchyCommandResponse
            {
                ok = true,
                message = "created",
                nodeId = created.GetInstanceID(),
                isActive = created.activeSelf
            });
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

            Undo.RecordObject(target, "unifocl toggle hierarchy active");
            target.SetActive(!target.activeSelf);
            EditorSceneManager.MarkSceneDirty(target.scene);
            _snapshotVersion++;

            return JsonUtility.ToJson(new HierarchyCommandResponse
            {
                ok = true,
                message = "toggled",
                nodeId = target.GetInstanceID(),
                isActive = target.activeSelf
            });
        }

        private static HierarchySnapshotResponse BuildSnapshot()
        {
            var scene = SceneManager.GetActiveScene();
            var sceneName = ResolveSceneName(scene);
            var children = scene.IsValid()
                ? scene.GetRootGameObjects()
                    .Select(ToDto)
                    .OrderBy(node => node.name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<HierarchyNodeDto>();

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
