#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static class DaemonHierarchyService
    {
        private static readonly object Sync = new();
        private static HierarchyNodeState _root = BuildSeedHierarchy();
        private static int _snapshotVersion = 1;
        private static int _nextId = ComputeMaxId(_root) + 1;

        public static string BuildSnapshotPayload()
        {
            lock (Sync)
            {
                return JsonUtility.ToJson(new HierarchySnapshotResponse
                {
                    scene = "Arena",
                    snapshotVersion = _snapshotVersion,
                    root = ToDto(_root)
                });
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
                var origin = request.parentId > 0
                    ? FindNode(_root, request.parentId) ?? _root
                    : _root;
                var originPath = BuildPath(_root, origin.id);
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

            var parentId = request.parentId > 0 ? request.parentId : _root.id;
            var parent = FindNode(_root, parentId);
            if (parent is null)
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = $"parent id not found: {parentId}" });
            }

            var created = new HierarchyNodeState(_nextId++, request.name.Trim(), true, new List<HierarchyNodeState>());
            parent.children.Add(created);
            _snapshotVersion++;
            return JsonUtility.ToJson(new HierarchyCommandResponse
            {
                ok = true,
                message = "created",
                nodeId = created.id,
                isActive = created.active
            });
        }

        private static string ExecuteToggle(HierarchyCommandRequest request)
        {
            if (request.targetId <= 0)
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = "toggle requires target id" });
            }

            var node = FindNode(_root, request.targetId);
            if (node is null)
            {
                return JsonUtility.ToJson(new HierarchyCommandResponse { ok = false, message = $"target id not found: {request.targetId}" });
            }

            node.active = !node.active;
            _snapshotVersion++;
            return JsonUtility.ToJson(new HierarchyCommandResponse
            {
                ok = true,
                message = "toggled",
                nodeId = node.id,
                isActive = node.active
            });
        }

        private static HierarchyNodeDto ToDto(HierarchyNodeState node)
        {
            return new HierarchyNodeDto
            {
                id = node.id,
                name = node.name,
                active = node.active,
                children = node.children.Select(ToDto).ToArray()
            };
        }

        private static HierarchyNodeState? FindNode(HierarchyNodeState node, int id)
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

        private static void CollectMatches(HierarchyNodeState node, string path, string query, List<HierarchySearchResult> output)
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

        private static int ComputeMaxId(HierarchyNodeState node)
        {
            var max = node.id;
            foreach (var child in node.children)
            {
                var childMax = ComputeMaxId(child);
                if (childMax > max)
                {
                    max = childMax;
                }
            }

            return max;
        }

        private static HierarchyNodeState BuildSeedHierarchy()
        {
            return new HierarchyNodeState(
                100,
                "Player",
                true,
                new List<HierarchyNodeState>
                {
                    new(101, "WeaponMount", true, new List<HierarchyNodeState>
                    {
                        new(102, "LeftBlaster", true, new List<HierarchyNodeState>()),
                        new(103, "RightBlaster", false, new List<HierarchyNodeState>())
                    }),
                    new(104, "Mesh", true, new List<HierarchyNodeState>
                    {
                        new(105, "PlayerModel", true, new List<HierarchyNodeState>())
                    }),
                    new(106, "Audio", false, new List<HierarchyNodeState>()),
                    new(107, "CapsuleCollider", true, new List<HierarchyNodeState>()),
                    new(108, "Rigidbody", true, new List<HierarchyNodeState>())
                });
        }

        private static string BuildPath(HierarchyNodeState root, int targetId)
        {
            var segments = new List<string>();
            if (!TryCollectPath(root, targetId, segments))
            {
                return "/" + root.name;
            }

            segments.Reverse();
            return "/" + string.Join('/', segments);
        }

        private static bool TryCollectPath(HierarchyNodeState node, int targetId, List<string> segments)
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
    }
}
#endif
