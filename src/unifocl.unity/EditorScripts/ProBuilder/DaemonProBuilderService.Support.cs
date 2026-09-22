// UNIFOCL_PROBUILDER mirrors the accompanying asmdef's define constraint. Unity supplies it through
// the asmdef's versionDefines when com.unity.probuilder 5.0+ is installed; the guard keeps the file
// compiling cleanly in harnesses that build these sources directly (e.g. compatcheck) without the
// ProBuilder assemblies on the reference path.
#if UNITY_EDITOR && UNIFOCL_PROBUILDER
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.SceneManagement;

namespace UniFocl.EditorBridge.ProBuilder
{
    internal static partial class DaemonProBuilderService
    {
        // A face counts as facing a direction when its world normal is strictly within 45 degrees of it
        // (cos 45° ≈ 0.70711). A face at exactly 45°, such as a bevel chamfer, sits between two directions
        // and belongs to neither, so "up" on a bevelled box selects only the top.
        private const float DirectionDotThreshold = 0.7075f;
        private const int MaxAmbiguousPathsReported = 5;
        private const string ProBuilderShapeTypeName = "UnityEngine.ProBuilder.Shapes.ProBuilderShape";

        private static readonly Dictionary<string, Vector3> FaceDirections = new(StringComparer.OrdinalIgnoreCase)
        {
            ["up"] = Vector3.up,
            ["top"] = Vector3.up,
            ["down"] = Vector3.down,
            ["bottom"] = Vector3.down,
            ["right"] = Vector3.right,
            ["left"] = Vector3.left,
            ["forward"] = Vector3.forward,
            ["front"] = Vector3.forward,
            ["back"] = Vector3.back,
            ["backward"] = Vector3.back
        };

        // ── response DTOs ────────────────────────────────────────────────────

        [Serializable]
        private sealed class ToolResponse
        {
            public bool ok;
            public string message = string.Empty;
        }

        [Serializable]
        private sealed class MeshSummary
        {
            public string path = string.Empty;
            public string name = string.Empty;
            public int vertexCount;
            public int faceCount;
            public int edgeCount;
            public int triangleCount;
            public Vector3 worldBoundsCenter;
            public Vector3 worldBoundsSize;
            public string[] materials = Array.Empty<string>();
        }

        [Serializable]
        private sealed class MeshResponse
        {
            public bool ok;
            public string message = string.Empty;
            public bool dryRun;
            // Indices of the faces the operation acted on, valid against the mesh after the operation.
            public int[] faces = Array.Empty<int>();
            public MeshSummary mesh = new();
        }

        [Serializable]
        private sealed class StringList
        {
            public string[] items = Array.Empty<string>();
        }

        private static string Error(string message)
        {
            return JsonUtility.ToJson(new ToolResponse { ok = false, message = message });
        }

        private static string MeshResult(ProBuilderMesh mesh, string message, IEnumerable<int>? faceIndices = null)
        {
            return JsonUtility.ToJson(new MeshResponse
            {
                ok = true,
                message = message,
                faces = faceIndices?.ToArray() ?? Array.Empty<int>(),
                mesh = Summarize(mesh)
            });
        }

        /// <summary>
        /// Mutating tools never touch the mesh under dry-run. Reverting through Undo would restore the
        /// ProBuilderMesh data but leave the compiled UnityEngine.Mesh stale, so the preview is computed
        /// from the validated inputs instead.
        /// </summary>
        private static string DryRunResult(ProBuilderMesh mesh, string action, IEnumerable<int>? faceIndices = null)
        {
            return JsonUtility.ToJson(new MeshResponse
            {
                ok = true,
                dryRun = true,
                message = $"[dry-run] would {action}",
                faces = faceIndices?.ToArray() ?? Array.Empty<int>(),
                mesh = Summarize(mesh)
            });
        }

        // ── argument parsing ─────────────────────────────────────────────────

        private static bool TryParseVector3(string? raw, bool allowUniform, out Vector3 value, out string error)
        {
            value = Vector3.zero;
            error = string.Empty;
            var text = (raw ?? string.Empty).Trim();
            if (text.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    value = JsonUtility.FromJson<Vector3>(text);
                    return IsFinite(value, out error);
                }
                catch (Exception)
                {
                    error = $"'{text}' is not a valid {{\"x\":..,\"y\":..,\"z\":..}} object";
                    return false;
                }
            }

            text = text.TrimStart('(', '[').TrimEnd(')', ']');
            var parts = text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && allowUniform && TryParseFloat(parts[0], out var uniform))
            {
                value = new Vector3(uniform, uniform, uniform);
                return IsFinite(value, out error);
            }

            if (parts.Length != 3
                || !TryParseFloat(parts[0], out var x)
                || !TryParseFloat(parts[1], out var y)
                || !TryParseFloat(parts[2], out var z))
            {
                error = allowUniform
                    ? $"'{raw}' is not a vector; use 'x,y,z' or a single number"
                    : $"'{raw}' is not a vector; use 'x,y,z'";
                return false;
            }

            value = new Vector3(x, y, z);
            return IsFinite(value, out error);
        }

        private static bool TryParseFloat(string raw, out float value)
        {
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool IsFinite(Vector3 value, out string error)
        {
            if (float.IsNaN(value.x) || float.IsInfinity(value.x)
                || float.IsNaN(value.y) || float.IsInfinity(value.y)
                || float.IsNaN(value.z) || float.IsInfinity(value.z))
            {
                error = "vector components must be finite numbers";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>
        /// Accepts a JSON array of strings or a ';' / newline separated list. Commas are not separators
        /// because GameObject names may contain them.
        /// </summary>
        private static List<string> ParsePathList(string? raw)
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    var parsed = JsonUtility.FromJson<StringList>("{\"items\":" + text + "}");
                    return parsed.items
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Select(item => item.Trim())
                        .ToList();
                }
                catch (Exception)
                {
                    // Not a JSON array after all; fall through to separator parsing.
                }
            }

            return text
                .Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .ToList();
        }

        // ── target resolution ────────────────────────────────────────────────

        /// <summary>
        /// Resolves a hierarchy path within the current unifocl context (open prefab or active scene).
        /// Tries the exact path from the root first, then a unique suffix match, so "Floor" and
        /// "Level/Floor" both resolve "/Environment/Level/Floor" when unambiguous.
        /// </summary>
        private static bool TryResolveGameObject(string? rawPath, string label, out GameObject gameObject, out string error)
        {
            gameObject = null!;
            var path = (rawPath ?? string.Empty).Trim();
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                error = $"{label} is required (hierarchy path such as /Level/Floor)";
                return false;
            }

            if (!DaemonHierarchyService.TryGetCurrentHierarchyRoots(out var rootLabel, out var roots))
            {
                error = $"cannot resolve {label} '{path}': no scene or prefab is open";
                return false;
            }

            if (roots.Length == 0)
            {
                error = $"{label} '{path}' not found: {rootLabel} has no objects";
                return false;
            }

            // Tolerate a leading scene-name segment ("SampleScene/Level/Floor") unless a root is named that way.
            if (segments.Length > 1
                && segments[0].Equals(rootLabel, StringComparison.OrdinalIgnoreCase)
                && roots.All(root => !root.name.Equals(segments[0], StringComparison.Ordinal)))
            {
                segments = segments.Skip(1).ToArray();
            }

            var exact = new List<GameObject>();
            var suffix = new List<GameObject>();
            var leafName = segments[segments.Length - 1];
            foreach (var root in roots)
            {
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    if (!transform.name.Equals(leafName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var chain = GetHierarchySegments(transform);
                    if (chain.Count == segments.Length && EndsWithSegments(chain, segments))
                    {
                        exact.Add(transform.gameObject);
                    }
                    else if (EndsWithSegments(chain, segments))
                    {
                        suffix.Add(transform.gameObject);
                    }
                }
            }

            var candidates = exact.Count > 0 ? exact : suffix;
            if (candidates.Count == 1)
            {
                gameObject = candidates[0];
                error = string.Empty;
                return true;
            }

            if (candidates.Count == 0)
            {
                error = $"{label} '{path}' not found in {rootLabel}";
                return false;
            }

            var listed = string.Join(", ", candidates
                .Take(MaxAmbiguousPathsReported)
                .Select(candidate => GetHierarchyPath(candidate.transform)));
            error = $"{label} '{path}' is ambiguous ({candidates.Count} matches: {listed}); pass a longer path";
            return false;
        }

        private static bool TryResolveProBuilderMesh(string? target, out ProBuilderMesh mesh, out string error)
        {
            mesh = null!;
            if (!TryResolveGameObject(target, "target", out var gameObject, out error))
            {
                return false;
            }

            var found = gameObject.GetComponent<ProBuilderMesh>();
            if (found == null)
            {
                error = gameObject.GetComponent<MeshFilter>() != null
                    ? $"'{GetHierarchyPath(gameObject.transform)}' is a regular mesh, not a ProBuilder mesh; convert it first with probuilder.mesh.probuilderize"
                    : $"'{GetHierarchyPath(gameObject.transform)}' has no ProBuilderMesh component";
                return false;
            }

            mesh = found;
            return true;
        }

        /// <summary>
        /// Resolves where a new object should live: the given parent, the open prefab's root when a prefab
        /// is being edited, or the active scene root (null).
        /// </summary>
        private static bool TryResolveCreationParent(string? parentPath, out Transform? parent, out string error)
        {
            parent = null;
            error = string.Empty;
            var path = (parentPath ?? string.Empty).Trim();
            if (path.Length > 0 && path != "/")
            {
                if (!TryResolveGameObject(path, "parent", out var parentObject, out error))
                {
                    return false;
                }

                parent = parentObject.transform;
                return true;
            }

            if (!DaemonHierarchyService.TryGetCurrentHierarchyRoots(out var rootLabel, out var roots))
            {
                error = "no scene is open to create the shape in";
                return false;
            }

            // Prefab contents live in a preview scene; a root-level object would land outside the prefab.
            if (rootLabel.StartsWith("Prefab:", StringComparison.Ordinal) && roots.Length > 0)
            {
                parent = roots[0].transform;
            }

            return true;
        }

        private static string MakeUniqueSiblingName(Transform? parent, string desiredName)
        {
            var siblingNames = new List<string>();
            if (parent != null)
            {
                for (var i = 0; i < parent.childCount; i++)
                {
                    siblingNames.Add(parent.GetChild(i).name);
                }
            }
            else if (DaemonSceneManager.TryGetActiveScene(out var scene))
            {
                siblingNames.AddRange(scene.GetRootGameObjects().Select(root => root.name));
            }

            return siblingNames.Contains(desiredName, StringComparer.Ordinal)
                ? ObjectNames.GetUniqueName(siblingNames.ToArray(), desiredName)
                : desiredName;
        }

        private static void MoveToActiveScene(GameObject gameObject)
        {
            if (DaemonSceneManager.TryGetActiveScene(out var scene) && gameObject.scene != scene)
            {
                SceneManager.MoveGameObjectToScene(gameObject, scene);
            }
        }

        private static List<string> GetHierarchySegments(Transform transform)
        {
            var segments = new List<string>();
            for (Transform? current = transform; current != null; current = current.parent)
            {
                segments.Add(current.name);
            }

            segments.Reverse();
            return segments;
        }

        private static bool EndsWithSegments(IReadOnlyList<string> chain, IReadOnlyList<string> segments)
        {
            if (segments.Count > chain.Count)
            {
                return false;
            }

            var offset = chain.Count - segments.Count;
            for (var i = 0; i < segments.Count; i++)
            {
                if (!chain[offset + i].Equals(segments[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static string GetHierarchyPath(Transform transform)
        {
            return "/" + string.Join("/", GetHierarchySegments(transform));
        }

        // ── face selection ───────────────────────────────────────────────────

        /// <summary>
        /// Parses a face selector: empty or "all", face indices ("0,3,5"), world directions
        /// (up|down|left|right|forward|back, plus top/bottom/front aliases), or any comma list mixing
        /// them — the result is their union.
        /// </summary>
        private static bool TrySelectFaces(ProBuilderMesh mesh, string? selector, out List<Face> selected, out string error)
        {
            selected = new List<Face>();
            error = string.Empty;
            var faces = mesh.faces;
            var text = (selector ?? string.Empty).Trim();
            if (text.Length == 0 || text.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                selected.AddRange(faces);
                if (selected.Count == 0)
                {
                    error = $"'{mesh.name}' has no faces";
                    return false;
                }

                return true;
            }

            var chosen = new SortedSet<int>();
            foreach (var token in text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    if (index < 0 || index >= faces.Count)
                    {
                        error = $"face index {index} is out of range (mesh has {faces.Count} faces; see probuilder.mesh.info)";
                        return false;
                    }

                    chosen.Add(index);
                    continue;
                }

                if (!FaceDirections.TryGetValue(token, out var direction))
                {
                    error = $"unknown face selector '{token}'; use all, face indices, or up|down|left|right|forward|back";
                    return false;
                }

                for (var i = 0; i < faces.Count; i++)
                {
                    if (Vector3.Dot(GetWorldNormal(mesh, faces[i]), direction) >= DirectionDotThreshold)
                    {
                        chosen.Add(i);
                    }
                }
            }

            if (chosen.Count == 0)
            {
                error = $"face selector '{text}' matched no faces on '{mesh.name}'";
                return false;
            }

            selected.AddRange(chosen.Select(index => faces[index]));
            return true;
        }

        private static Vector3 GetWorldNormal(ProBuilderMesh mesh, Face face)
        {
            return mesh.transform.TransformDirection(UnityEngine.ProBuilder.Math.Normal(mesh, face)).normalized;
        }

        private static Vector3 GetWorldCenter(ProBuilderMesh mesh, Face face)
        {
            var positions = mesh.positions;
            var distinct = face.distinctIndexes;
            if (distinct.Count == 0)
            {
                return mesh.transform.position;
            }

            var sum = Vector3.zero;
            foreach (var index in distinct)
            {
                sum += positions[index];
            }

            return mesh.transform.TransformPoint(sum / distinct.Count);
        }

        private static string DescribeDirection(Vector3 worldNormal)
        {
            foreach (var name in new[] { "up", "down", "right", "left", "forward", "back" })
            {
                if (Vector3.Dot(worldNormal, FaceDirections[name]) >= DirectionDotThreshold)
                {
                    return name;
                }
            }

            return string.Empty;
        }

        private static List<int> IndicesOf(ProBuilderMesh mesh, IEnumerable<Face> subset)
        {
            var faces = mesh.faces;
            var lookup = new Dictionary<Face, int>();
            for (var i = 0; i < faces.Count; i++)
            {
                lookup[faces[i]] = i;
            }

            return subset
                .Where(face => face != null && lookup.ContainsKey(face))
                .Select(face => lookup[face])
                .Distinct()
                .OrderBy(index => index)
                .ToList();
        }

        // ── assets ───────────────────────────────────────────────────────────

        private static bool TryLoadMaterial(string? assetPath, out Material material, out string error)
        {
            material = null!;
            var path = (assetPath ?? string.Empty).Trim().Replace('\\', '/');
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) && !path.StartsWith("Packages/", StringComparison.Ordinal))
            {
                error = $"material must be an asset path under Assets/ or Packages/ (got '{assetPath}')";
                return false;
            }

            var loaded = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (loaded == null)
            {
                error = $"no Material found at '{path}'";
                return false;
            }

            material = loaded;
            error = string.Empty;
            return true;
        }

        private static string DescribeAsset(UnityEngine.Object? asset)
        {
            if (asset == null)
            {
                return string.Empty;
            }

            var path = AssetDatabase.GetAssetPath(asset);
            return string.IsNullOrEmpty(path) ? asset.name : path;
        }

        // ── mesh bookkeeping ─────────────────────────────────────────────────

        /// <summary>
        /// Starts a dedicated undo group for one tool call. The editor only advances the current group on
        /// user input, so without this every daemon request since the last click would share one group:
        /// a single Ctrl+Z would undo all of them, and anything that reverts "the current group" would
        /// take earlier requests with it. <see cref="EndUndoStep"/> seals the group afterwards.
        /// </summary>
        private static void BeginUndoStep(string label)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(label);
        }

        private static void EndUndoStep()
        {
            Undo.IncrementCurrentGroup();
        }

        private static void RecordUndo(ProBuilderMesh mesh, string label)
        {
            BeginUndoStep(label);
            // Complete-object undo: ProBuilder meshes routinely exceed the size where RecordObject diffs
            // become slow, which is the same threshold ProBuilder's own UndoUtility switches at.
            Undo.RegisterCompleteObjectUndo(mesh, label);
        }

        /// <summary>
        /// Rebuilds the compiled mesh from the ProBuilder data and persists the owning scene the same way
        /// built-in unifocl hierarchy mutations do.
        /// </summary>
        private static void CommitMeshChange(ProBuilderMesh mesh, string source)
        {
            mesh.ToMesh();
            mesh.Refresh();
            UnityEditor.ProBuilder.EditorMeshUtility.Optimize(mesh);
            EditorUtility.SetDirty(mesh);
            DaemonScenePersistenceService.RecordPrefabInstanceMutation(mesh);
            DaemonScenePersistenceService.PersistMutationScenes(source, mesh.gameObject.scene);
            EndUndoStep();
        }

        private static Bounds ComputeLocalBounds(ProBuilderMesh mesh)
        {
            var positions = mesh.positions;
            if (positions.Count == 0)
            {
                return new Bounds(Vector3.zero, Vector3.zero);
            }

            var bounds = new Bounds(positions[0], Vector3.zero);
            for (var i = 1; i < positions.Count; i++)
            {
                bounds.Encapsulate(positions[i]);
            }

            return bounds;
        }

        private static Bounds ComputeWorldBounds(ProBuilderMesh mesh)
        {
            var positions = mesh.positions;
            var transform = mesh.transform;
            if (positions.Count == 0)
            {
                return new Bounds(transform.position, Vector3.zero);
            }

            var bounds = new Bounds(transform.TransformPoint(positions[0]), Vector3.zero);
            for (var i = 1; i < positions.Count; i++)
            {
                bounds.Encapsulate(transform.TransformPoint(positions[i]));
            }

            return bounds;
        }

        private static MeshSummary Summarize(ProBuilderMesh mesh)
        {
            var renderer = mesh.GetComponent<MeshRenderer>();
            var worldBounds = ComputeWorldBounds(mesh);
            return new MeshSummary
            {
                path = GetHierarchyPath(mesh.transform),
                name = mesh.name,
                vertexCount = mesh.vertexCount,
                faceCount = mesh.faceCount,
                edgeCount = mesh.edgeCount,
                triangleCount = mesh.triangleCount,
                worldBoundsCenter = worldBounds.center,
                worldBoundsSize = worldBounds.size,
                materials = renderer != null
                    ? renderer.sharedMaterials.Select(DescribeAsset).ToArray()
                    : Array.Empty<string>()
            };
        }

        /// <summary>
        /// ProBuilderShape (internal to ProBuilder) and PolyShape regenerate the mesh from their own
        /// parameters, which would discard merged geometry the next time they rebuild. ProBuilder's own
        /// merge action strips them for the same reason.
        /// </summary>
        private static void RemoveParametricShapeComponents(GameObject gameObject)
        {
            foreach (var behaviour in gameObject.GetComponents<MonoBehaviour>())
            {
                if (behaviour == null)
                {
                    continue;
                }

                if (behaviour is PolyShape || behaviour.GetType().FullName == ProBuilderShapeTypeName)
                {
                    Undo.DestroyObjectImmediate(behaviour);
                }
            }
        }

        private static string Format(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Format(Vector3 value)
        {
            return $"{Format(value.x)},{Format(value.y)},{Format(value.z)}";
        }
    }
}
#endif
