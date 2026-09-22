// UNIFOCL_PROBUILDER mirrors the accompanying asmdef's define constraint. Unity supplies it through
// the asmdef's versionDefines when com.unity.probuilder 5.0+ is installed; the guard keeps the file
// compiling cleanly in harnesses that build these sources directly (e.g. compatcheck) without the
// ProBuilder assemblies on the reference path.
#if UNITY_EDITOR && UNIFOCL_PROBUILDER
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;

namespace UniFocl.EditorBridge.ProBuilder
{
    /// <summary>
    /// The optional "probuilder" tool category: level-design geometry through ProBuilder's scripting API.
    ///
    /// This lives in its own assembly (UniFocl.EditorBridge.ProBuilder) because the main bridge cannot
    /// reference Unity.ProBuilder without breaking every project that does not have the package. The
    /// assembly carries a define constraint fed by a versionDefine on com.unity.probuilder, so with the
    /// package absent Unity skips it entirely: the tools never reach the manifest and the category does
    /// not appear in get_categories.
    ///
    /// Tools take flat primitive parameters so the generated manifest schema describes them and the
    /// custom-tool dispatcher can bind them by name. Vectors are "x,y,z" strings.
    /// </summary>
    internal static partial class DaemonProBuilderService
    {
        private const string Category = "probuilder";

        private enum ShapeKind
        {
            Cube,
            Stair,
            CurvedStair,
            Prism,
            Cylinder,
            Plane,
            Door,
            Pipe,
            Cone,
            Arch,
            Sphere,
            Torus
        }

        private static readonly Dictionary<string, ShapeKind> ShapeAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cube"] = ShapeKind.Cube,
            ["box"] = ShapeKind.Cube,
            ["stair"] = ShapeKind.Stair,
            ["stairs"] = ShapeKind.Stair,
            ["curvedstair"] = ShapeKind.CurvedStair,
            ["curvedstairs"] = ShapeKind.CurvedStair,
            ["prism"] = ShapeKind.Prism,
            ["cylinder"] = ShapeKind.Cylinder,
            ["plane"] = ShapeKind.Plane,
            ["door"] = ShapeKind.Door,
            ["pipe"] = ShapeKind.Pipe,
            ["cone"] = ShapeKind.Cone,
            ["arch"] = ShapeKind.Arch,
            ["sphere"] = ShapeKind.Sphere,
            ["icosphere"] = ShapeKind.Sphere,
            ["torus"] = ShapeKind.Torus
        };

        [Serializable]
        private sealed class FaceInfo
        {
            public int index;
            public Vector3 normal;
            public Vector3 center;
            public string direction = string.Empty;
            public int submeshIndex;
            public string material = string.Empty;
            public int smoothingGroup;
            public int vertexCount;
        }

        [Serializable]
        private sealed class MeshInfoResponse
        {
            public bool ok;
            public string message = string.Empty;
            public MeshSummary mesh = new();
            public FaceInfo[] faces = Array.Empty<FaceInfo>();
            public bool facesTruncated;
        }

        [Serializable]
        private sealed class MergeResponse
        {
            public bool ok;
            public string message = string.Empty;
            public bool dryRun;
            public MeshSummary mesh = new();
            public string[] removed = Array.Empty<string>();
            public string[] created = Array.Empty<string>();
        }

        [Serializable]
        private sealed class ExportResponse
        {
            public bool ok;
            public string message = string.Empty;
            public bool dryRun;
            public string assetPath = string.Empty;
            public int vertexCount;
            public int triangleCount;
        }

        // ── probuilder.shape.create ──────────────────────────────────────────

        [UnifoclCommand(
            "probuilder.shape.create",
            "Create a ProBuilder primitive in the open scene (or the open prefab). " +
            "shape (required): cube|stair|curved_stair|prism|cylinder|plane|door|pipe|cone|arch|sphere|torus. " +
            "name: object name (default: shape name; made unique among siblings). " +
            "parent: hierarchy path of the parent (default: scene root). " +
            "position / rotation: local 'x,y,z' (rotation in Euler degrees) relative to the parent. " +
            "size: bounding box 'x,y,z' in meters, or one number for a uniform size (default: ProBuilder's default size; plane ignores y). " +
            "pivot: center (default) | bottom (bottom-center, handy for placing on a floor). " +
            "material: Material asset path (default: ProBuilder default material). " +
            "segments: divisions for round shapes / plane grid cuts (cylinder 4-64, cone|pipe|arch|torus 3-64, plane 1-64, sphere subdivisions 1-4; 0 = default). " +
            "steps: stair step count (0 = default). collider: add a MeshCollider (default true). " +
            "Returns the created object's hierarchy path and mesh stats. SafeWrite.",
            Category)]
        public static string CreateShape(
            string shape,
            string name = "",
            string parent = "",
            string position = "",
            string rotation = "",
            string size = "",
            string pivot = "center",
            string material = "",
            int segments = 0,
            int steps = 0,
            bool collider = true)
        {
            try
            {
                if (!TryParseShapeKind(shape, out var kind, out var error)
                    || !TryValidateTopology(kind, segments, steps, out error))
                {
                    return Error(error);
                }

                Vector3? targetSize = null;
                if (!string.IsNullOrWhiteSpace(size))
                {
                    if (!TryParseVector3(size, allowUniform: true, out var parsedSize, out error))
                    {
                        return Error($"size: {error}");
                    }

                    if (!TryValidateSize(kind, parsedSize, out error))
                    {
                        return Error(error);
                    }

                    targetSize = parsedSize;
                }

                var localPosition = Vector3.zero;
                if (!string.IsNullOrWhiteSpace(position) && !TryParseVector3(position, allowUniform: false, out localPosition, out error))
                {
                    return Error($"position: {error}");
                }

                var localEuler = Vector3.zero;
                if (!string.IsNullOrWhiteSpace(rotation) && !TryParseVector3(rotation, allowUniform: false, out localEuler, out error))
                {
                    return Error($"rotation: {error}");
                }

                if (!TryParsePivot(pivot, out var pivotAtBottom, out error))
                {
                    return Error(error);
                }

                Material? shapeMaterial = null;
                if (!string.IsNullOrWhiteSpace(material))
                {
                    if (!TryLoadMaterial(material, out var loaded, out error))
                    {
                        return Error(error);
                    }

                    shapeMaterial = loaded;
                }

                if (!TryResolveCreationParent(parent, out var parentTransform, out error))
                {
                    return Error(error);
                }

                var objectName = MakeUniqueSiblingName(
                    parentTransform,
                    string.IsNullOrWhiteSpace(name) ? kind.ToString() : name.Trim());
                var parentPath = parentTransform != null ? GetHierarchyPath(parentTransform) : string.Empty;

                if (DaemonDryRunContext.IsActive)
                {
                    return JsonUtility.ToJson(new MeshResponse
                    {
                        ok = true,
                        dryRun = true,
                        message = $"[dry-run] would create {DescribeShape(kind)} '{objectName}' at {parentPath}/{objectName}",
                        mesh = new MeshSummary { path = $"{parentPath}/{objectName}", name = objectName }
                    });
                }

                // Everything that can be rejected was validated above: from here on a GameObject exists in
                // the scene, and any failure must remove it again.
                BeginUndoStep("unifocl probuilder.shape.create");
                var mesh = GenerateShape(kind, segments, steps);
                var gameObject = mesh.gameObject;
                try
                {
                    gameObject.name = objectName;
                    if (targetSize.HasValue)
                    {
                        UnityEngine.ProBuilder.MeshUtility.FitToSize(mesh, ComputeLocalBounds(mesh), targetSize.Value);
                    }

                    if (pivotAtBottom)
                    {
                        MovePivotToBottomCenter(mesh);
                    }

                    // Parent first so position and rotation are interpreted as local coordinates.
                    if (parentTransform != null)
                    {
                        gameObject.transform.SetParent(parentTransform, false);
                    }
                    else
                    {
                        MoveToActiveScene(gameObject);
                    }

                    gameObject.transform.localPosition = localPosition;
                    gameObject.transform.localEulerAngles = localEuler;
                    mesh.SetMaterial(mesh.faces, shapeMaterial != null ? shapeMaterial : BuiltinMaterials.defaultMaterial);
                    if (collider && gameObject.GetComponent<Collider>() == null)
                    {
                        gameObject.AddComponent<MeshCollider>();
                    }

                    Undo.RegisterCreatedObjectUndo(gameObject, "unifocl probuilder.shape.create");
                    CommitMeshChange(mesh, "probuilder.shape.create");
                }
                catch (Exception)
                {
                    UnityEngine.Object.DestroyImmediate(gameObject);
                    throw;
                }

                return MeshResult(mesh, $"created {DescribeShape(kind)} '{gameObject.name}' at {GetHierarchyPath(gameObject.transform)}");
            }
            catch (Exception ex)
            {
                return Error($"probuilder.shape.create failed: {ex.Message}");
            }
        }

        private static bool TryParseShapeKind(string? shape, out ShapeKind kind, out string error)
        {
            var normalized = (shape ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
            if (ShapeAliases.TryGetValue(normalized, out kind))
            {
                error = string.Empty;
                return true;
            }

            error = string.IsNullOrWhiteSpace(shape)
                ? "shape is required: cube|stair|curved_stair|prism|cylinder|plane|door|pipe|cone|arch|sphere|torus"
                : $"unknown shape '{shape}'; use cube|stair|curved_stair|prism|cylinder|plane|door|pipe|cone|arch|sphere|torus";
            return false;
        }

        private static bool TryValidateTopology(ShapeKind kind, int segments, int steps, out string error)
        {
            error = string.Empty;
            if (steps < 0 || steps > 256)
            {
                error = "steps must be between 0 (default) and 256";
                return false;
            }

            var (min, max) = kind switch
            {
                ShapeKind.Cylinder => (4, 64),
                ShapeKind.Cone or ShapeKind.Pipe or ShapeKind.Arch or ShapeKind.Torus => (3, 64),
                ShapeKind.Plane => (1, 64),
                ShapeKind.Sphere => (1, 4),
                _ => (0, int.MaxValue)
            };

            // 0 always means "use the shape's default"; only explicit values are range-checked.
            if (segments != 0 && (segments < min || segments > max))
            {
                error = $"segments for {DescribeShape(kind)} must be between {min} and {max}, or 0 for the default";
                return false;
            }

            if (segments < 0)
            {
                error = "segments must not be negative";
                return false;
            }

            return true;
        }

        private static bool TryValidateSize(ShapeKind kind, Vector3 size, out string error)
        {
            error = string.Empty;
            var flatY = kind == ShapeKind.Plane;
            if (size.x <= 0f || size.z <= 0f || (!flatY && size.y <= 0f) || size.y < 0f)
            {
                error = flatY
                    ? "size x and z must be greater than 0 for a plane"
                    : "size components must all be greater than 0";
                return false;
            }

            if (size.x > 10000f || size.y > 10000f || size.z > 10000f)
            {
                error = "size components must not exceed 10000";
                return false;
            }

            return true;
        }

        private static bool TryParsePivot(string? pivot, out bool atBottom, out string error)
        {
            var normalized = (pivot ?? string.Empty).Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "":
                case "center":
                    atBottom = false;
                    error = string.Empty;
                    return true;
                case "bottom":
                    atBottom = true;
                    error = string.Empty;
                    return true;
                default:
                    atBottom = false;
                    error = $"unknown pivot '{pivot}'; use center or bottom";
                    return false;
            }
        }

        private static ProBuilderMesh GenerateShape(ShapeKind kind, int segments, int steps)
        {
            const PivotLocation pivot = PivotLocation.Center;
            int Pick(int requested, int fallback) => requested > 0 ? requested : fallback;

            // Dimensions below are ProBuilder's own defaults (ShapeGenerator.CreateShape); callers resize
            // afterwards with FitToSize so every shape shares the same bounding-box size semantics.
            return kind switch
            {
                ShapeKind.Cube => ShapeGenerator.GenerateCube(pivot, Vector3.one),
                ShapeKind.Prism => ShapeGenerator.GeneratePrism(pivot, Vector3.one),
                ShapeKind.Stair => ShapeGenerator.GenerateStair(pivot, new Vector3(2f, 2.5f, 4f), Pick(steps, 6), true),
                ShapeKind.CurvedStair => ShapeGenerator.GenerateCurvedStair(pivot, 2f, 2.5f, 2f, 180f, Pick(steps, 8), true),
                ShapeKind.Cylinder => ShapeGenerator.GenerateCylinder(pivot, Pick(segments, 8), 1f, 2f, 0),
                ShapeKind.Plane => ShapeGenerator.GeneratePlane(pivot, 5f, 5f, segments, segments, Axis.Up),
                ShapeKind.Door => ShapeGenerator.GenerateDoor(pivot, 3f, 2.5f, 0.5f, 0.75f, 1f),
                ShapeKind.Pipe => ShapeGenerator.GeneratePipe(pivot, 1f, 2f, 0.25f, Pick(segments, 8), 0),
                ShapeKind.Cone => ShapeGenerator.GenerateCone(pivot, 0.5f, 1f, Pick(segments, 8)),
                ShapeKind.Arch => ShapeGenerator.GenerateArch(pivot, 180f, 2f, 1f, 1f, Pick(segments, 9), true, true, true, true, true),
                ShapeKind.Sphere => ShapeGenerator.GenerateIcosahedron(pivot, 0.5f, Pick(segments, 2), true, false),
                ShapeKind.Torus => ShapeGenerator.GenerateTorus(
                    pivot, Pick(segments, 12), segments > 0 ? Mathf.Max(3, segments * 4 / 3) : 16, 1f, 0.3f, true, 360f, 360f, false),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unsupported shape")
            };
        }

        private static void MovePivotToBottomCenter(ProBuilderMesh mesh)
        {
            var bounds = ComputeLocalBounds(mesh);
            var offset = new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
            if (offset.sqrMagnitude < 1e-10f)
            {
                return;
            }

            var positions = mesh.positions.ToArray();
            for (var i = 0; i < positions.Length; i++)
            {
                positions[i] += offset;
            }

            mesh.positions = positions;
            mesh.ToMesh();
            mesh.Refresh();
        }

        private static string DescribeShape(ShapeKind kind)
        {
            return kind switch
            {
                ShapeKind.CurvedStair => "curved_stair",
                _ => kind.ToString().ToLowerInvariant()
            };
        }

        // ── probuilder.mesh.info ─────────────────────────────────────────────

        [UnifoclCommand(
            "probuilder.mesh.info",
            "Describe a ProBuilder mesh: vertex/face/edge/triangle counts, world bounds, materials, and per-face " +
            "index, world normal, world center, facing direction (up|down|left|right|forward|back), material and " +
            "smoothing group. Use the face indices or directions as the 'faces' selector of the probuilder.face.* tools. " +
            "target (required): hierarchy path. includeFaces: list faces (default true). maxFaces: cap on listed faces (default 64, max 1024). SafeRead.",
            Category)]
        public static string GetMeshInfo(string target, bool includeFaces = true, int maxFaces = 64)
        {
            try
            {
                if (!TryResolveProBuilderMesh(target, out var mesh, out var error))
                {
                    return Error(error);
                }

                var limit = Mathf.Clamp(maxFaces, 0, 1024);
                var faces = mesh.faces;
                var faceInfos = new List<FaceInfo>();
                if (includeFaces)
                {
                    var renderer = mesh.GetComponent<MeshRenderer>();
                    var materials = renderer != null ? renderer.sharedMaterials : Array.Empty<Material>();
                    for (var i = 0; i < faces.Count && i < limit; i++)
                    {
                        var face = faces[i];
                        var normal = GetWorldNormal(mesh, face);
                        faceInfos.Add(new FaceInfo
                        {
                            index = i,
                            normal = normal,
                            center = GetWorldCenter(mesh, face),
                            direction = DescribeDirection(normal),
                            submeshIndex = face.submeshIndex,
                            material = face.submeshIndex >= 0 && face.submeshIndex < materials.Length
                                ? DescribeAsset(materials[face.submeshIndex])
                                : string.Empty,
                            smoothingGroup = face.smoothingGroup,
                            vertexCount = face.distinctIndexes.Count
                        });
                    }
                }

                var summary = Summarize(mesh);
                return JsonUtility.ToJson(new MeshInfoResponse
                {
                    ok = true,
                    message = $"{summary.path}: {summary.vertexCount} vertices, {summary.faceCount} faces, {summary.triangleCount} triangles",
                    mesh = summary,
                    faces = faceInfos.ToArray(),
                    facesTruncated = includeFaces && faces.Count > faceInfos.Count
                });
            }
            catch (Exception ex)
            {
                return Error($"probuilder.mesh.info failed: {ex.Message}");
            }
        }

        // ── probuilder.mesh.merge ────────────────────────────────────────────

        [UnifoclCommand(
            "probuilder.mesh.merge",
            "Merge two or more ProBuilder meshes into the first one (ProBuilder 'Merge Objects'). The other source " +
            "objects are deleted; materials are kept per face. " +
            "targets (required): hierarchy paths separated by ';' (or a JSON array of paths); the first path is the merge target. " +
            "name: optional new name for the merged object. DestructiveWrite (deletes the source objects).",
            Category)]
        public static string MergeMeshes(string targets, string name = "")
        {
            try
            {
                var paths = ParsePathList(targets);
                if (paths.Count < 2)
                {
                    return Error("targets must name at least two ProBuilder meshes separated by ';'");
                }

                var meshes = new List<ProBuilderMesh>();
                foreach (var path in paths)
                {
                    if (!TryResolveProBuilderMesh(path, out var resolved, out var error))
                    {
                        return Error(error);
                    }

                    if (meshes.Contains(resolved))
                    {
                        return Error($"'{GetHierarchyPath(resolved.transform)}' is listed more than once");
                    }

                    meshes.Add(resolved);
                }

                var target = meshes[0];
                var donors = meshes.Skip(1).ToList();
                var donorPaths = donors.Select(donor => GetHierarchyPath(donor.transform)).ToArray();
                var newName = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();

                if (DaemonDryRunContext.IsActive)
                {
                    return JsonUtility.ToJson(new MergeResponse
                    {
                        ok = true,
                        dryRun = true,
                        message = $"[dry-run] would merge {donors.Count} mesh(es) into {GetHierarchyPath(target.transform)} and delete them",
                        mesh = Summarize(target),
                        removed = donorPaths
                    });
                }

                var scenes = meshes.Select(mesh => mesh.gameObject.scene).Distinct().ToArray();
                RecordUndo(target, "unifocl probuilder.mesh.merge");
                var result = CombineMeshes.Combine(meshes, target);
                if (result == null || result.Count == 0)
                {
                    return Error("ProBuilder could not merge the meshes");
                }

                var created = new List<string>();
                foreach (var merged in result)
                {
                    RemoveParametricShapeComponents(merged.gameObject);
                    if (merged != target)
                    {
                        // Combine spills into extra meshes only past ProBuilder's per-mesh vertex limit.
                        merged.gameObject.name = MakeUniqueSiblingName(merged.transform.parent, target.name + "-Merged");
                        Undo.RegisterCreatedObjectUndo(merged.gameObject, "unifocl probuilder.mesh.merge");
                        created.Add(GetHierarchyPath(merged.transform));
                    }

                    merged.ToMesh();
                    merged.Refresh();
                    UnityEditor.ProBuilder.EditorMeshUtility.Optimize(merged);
                    EditorUtility.SetDirty(merged);
                }

                var removed = new List<string>();
                for (var i = 0; i < donors.Count; i++)
                {
                    if (donors[i] != null && !result.Contains(donors[i]))
                    {
                        removed.Add(donorPaths[i]);
                        Undo.DestroyObjectImmediate(donors[i].gameObject);
                    }
                }

                if (newName.Length > 0 && newName != target.name)
                {
                    Undo.RecordObject(target.gameObject, "unifocl probuilder.mesh.merge");
                    target.gameObject.name = MakeUniqueSiblingName(target.transform.parent, newName);
                }

                DaemonScenePersistenceService.RecordPrefabInstanceMutation(target);
                DaemonScenePersistenceService.PersistMutationScenes("probuilder.mesh.merge", scenes);
                EndUndoStep();

                return JsonUtility.ToJson(new MergeResponse
                {
                    ok = true,
                    message = $"merged {removed.Count} mesh(es) into {GetHierarchyPath(target.transform)}",
                    mesh = Summarize(target),
                    removed = removed.ToArray(),
                    created = created.ToArray()
                });
            }
            catch (Exception ex)
            {
                return Error($"probuilder.mesh.merge failed: {ex.Message}");
            }
        }

        // ── probuilder.mesh.probuilderize ────────────────────────────────────

        [UnifoclCommand(
            "probuilder.mesh.probuilderize",
            "Convert a regular MeshFilter/MeshRenderer object into an editable ProBuilder mesh (ProBuilder 'ProBuilderize'). " +
            "The source mesh asset is left untouched; the object gets its own ProBuilder-owned mesh. " +
            "target (required): hierarchy path. quads: rebuild quads from triangle pairs (default true). " +
            "smoothing: import smoothing groups (default true). smoothingAngle: max angle in degrees between faces of one smoothing group (default 1). SafeWrite.",
            Category)]
        public static string ProBuilderize(string target, bool quads = true, bool smoothing = true, float smoothingAngle = 1f)
        {
            try
            {
                if (!TryResolveGameObject(target, "target", out var gameObject, out var error))
                {
                    return Error(error);
                }

                var path = GetHierarchyPath(gameObject.transform);
                if (gameObject.GetComponent<ProBuilderMesh>() != null)
                {
                    return Error($"'{path}' is already a ProBuilder mesh");
                }

                var meshFilter = gameObject.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null)
                {
                    return Error($"'{path}' has no MeshFilter with a mesh to convert");
                }

                var renderer = gameObject.GetComponent<MeshRenderer>();
                if (renderer != null && renderer.isPartOfStaticBatch)
                {
                    return Error($"'{path}' is part of a static batch, which ProBuilderize does not support");
                }

                if (!IsFinite(smoothingAngle) || smoothingAngle < 0f || smoothingAngle > 180f)
                {
                    return Error("smoothingAngle must be between 0 and 180 degrees");
                }

                var sourceMesh = meshFilter.sharedMesh;
                if (DaemonDryRunContext.IsActive)
                {
                    return JsonUtility.ToJson(new ToolResponse
                    {
                        ok = true,
                        message = $"[dry-run] would convert '{path}' ({sourceMesh.vertexCount} vertices from '{DescribeAsset(sourceMesh)}') to a ProBuilder mesh"
                    });
                }

                BeginUndoStep("unifocl probuilder.mesh.probuilderize");
                var undoGroup = Undo.GetCurrentGroup();
                try
                {
                    var mesh = Undo.AddComponent<ProBuilderMesh>(gameObject);
                    var importer = new MeshImporter(sourceMesh, renderer != null ? renderer.sharedMaterials : null, mesh);
                    importer.Import(new MeshImportSettings
                    {
                        quads = quads,
                        smoothing = smoothing,
                        smoothingAngle = smoothingAngle
                    });

                    CommitMeshChange(mesh, "probuilder.mesh.probuilderize");
                    return MeshResult(mesh, $"converted '{path}' to a ProBuilder mesh");
                }
                catch (Exception)
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                    throw;
                }
            }
            catch (Exception ex)
            {
                return Error($"probuilder.mesh.probuilderize failed: {ex.Message}");
            }
        }

        // ── probuilder.mesh.export ───────────────────────────────────────────

        [UnifoclCommand(
            "probuilder.mesh.export",
            "Save a copy of a ProBuilder object's compiled mesh as a Mesh asset (.asset), e.g. to reuse it on " +
            "objects without ProBuilder. The scene object is not changed. " +
            "target (required): hierarchy path. assetPath (required): 'Assets/.../Name.asset' in an existing folder. " +
            "overwrite: replace an existing asset at that path (default false). SafeWrite.",
            Category)]
        public static string ExportMesh(string target, string assetPath, bool overwrite = false)
        {
            try
            {
                if (!TryResolveProBuilderMesh(target, out var mesh, out var error))
                {
                    return Error(error);
                }

                var path = (assetPath ?? string.Empty).Trim().Replace('\\', '/');
                if (!path.StartsWith("Assets/", StringComparison.Ordinal)
                    || !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("/../")
                    || path.Contains("/./"))
                {
                    return Error("assetPath must look like 'Assets/Folder/Name.asset'");
                }

                var folder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;
                if (!AssetDatabase.IsValidFolder(folder))
                {
                    return Error($"folder '{folder}' does not exist");
                }

                var exists = AssetDatabase.LoadMainAssetAtPath(path) != null;
                if (exists && !overwrite)
                {
                    return Error($"an asset already exists at '{path}'; pass overwrite=true to replace it");
                }

                var meshFilter = mesh.GetComponent<MeshFilter>();
                var compiled = meshFilter != null ? meshFilter.sharedMesh : null;
                if (compiled == null)
                {
                    return Error($"'{GetHierarchyPath(mesh.transform)}' has no compiled mesh to export");
                }

                if (DaemonDryRunContext.IsActive)
                {
                    return JsonUtility.ToJson(new ExportResponse
                    {
                        ok = true,
                        dryRun = true,
                        message = $"[dry-run] would {(exists ? "overwrite" : "create")} mesh asset '{path}'",
                        assetPath = path,
                        vertexCount = compiled.vertexCount,
                        triangleCount = CountTriangles(compiled)
                    });
                }

                var copy = UnityEngine.Object.Instantiate(compiled);
                copy.name = Path.GetFileNameWithoutExtension(path);
                AssetDatabase.CreateAsset(copy, path);
                AssetDatabase.SaveAssets();

                return JsonUtility.ToJson(new ExportResponse
                {
                    ok = true,
                    message = $"{(exists ? "overwrote" : "created")} mesh asset '{path}' from {GetHierarchyPath(mesh.transform)}",
                    assetPath = path,
                    vertexCount = copy.vertexCount,
                    triangleCount = CountTriangles(copy)
                });
            }
            catch (Exception ex)
            {
                return Error($"probuilder.mesh.export failed: {ex.Message}");
            }
        }

        private static int CountTriangles(Mesh mesh)
        {
            long indices = 0;
            for (var i = 0; i < mesh.subMeshCount; i++)
            {
                indices += mesh.GetIndexCount(i);
            }

            return (int)(indices / 3);
        }
    }
}
#endif
