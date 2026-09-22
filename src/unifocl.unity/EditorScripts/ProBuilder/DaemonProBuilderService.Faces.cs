// UNIFOCL_PROBUILDER mirrors the accompanying asmdef's define constraint. Unity supplies it through
// the asmdef's versionDefines when com.unity.probuilder 5.0+ is installed; the guard keeps the file
// compiling cleanly in harnesses that build these sources directly (e.g. compatcheck) without the
// ProBuilder assemblies on the reference path.
#if UNITY_EDITOR && UNIFOCL_PROBUILDER
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;

namespace UniFocl.EditorBridge.ProBuilder
{
    internal static partial class DaemonProBuilderService
    {
        // Every probuilder.face.* / edge.* tool takes the same selector; documented once here and repeated
        // in each description because agents see descriptions per tool.
        private const string FacesSelectorHelp =
            "faces: 'all', face indices from probuilder.mesh.info ('0,3,5'), or world directions " +
            "up|down|left|right|forward|back (faces whose normal is less than 45 degrees off; 45-degree chamfers match neither side); a comma list is a union.";

        // ── probuilder.face.extrude ──────────────────────────────────────────

        [UnifoclCommand(
            "probuilder.face.extrude",
            "Extrude faces of a ProBuilder mesh (ProBuilder 'Extrude Faces'). target (required): hierarchy path. " +
            "faces (required): " + FacesSelectorHelp + " " +
            "distance: meters, negative pushes inward (default 0.5). " +
            "method: face_normal (default, grouped along each face normal) | vertex_normal (grouped, averaged normals) | individual (each face separately). " +
            "Returns the indices of the extruded cap faces. SafeWrite.",
            Category)]
        public static string ExtrudeFaces(string target, string faces, float distance = 0.5f, string method = "face_normal")
        {
            try
            {
                if (!TryResolveProBuilderMesh(target, out var mesh, out var error)
                    || !TrySelectFaces(mesh, faces, out var selected, out error))
                {
                    return Error(error);
                }

                if (!IsFinite(distance) || Mathf.Abs(distance) < 1e-5f || Mathf.Abs(distance) > 10000f)
                {
                    return Error("distance must be a non-zero number of meters (at most 10000)");
                }

                if (!TryParseExtrudeMethod(method, out var extrudeMethod, out error))
                {
                    return Error(error);
                }

                var action = $"extrude {selected.Count} face(s) of {GetHierarchyPath(mesh.transform)} by {Format(distance)}";
                if (DaemonDryRunContext.IsActive)
                {
                    return DryRunResult(mesh, action, IndicesOf(mesh, selected));
                }

                RecordUndo(mesh, "unifocl probuilder.face.extrude");
                var created = mesh.Extrude(selected, extrudeMethod, distance);
                if (created == null)
                {
                    return Error("ProBuilder could not extrude the selected faces");
                }

                CommitMeshChange(mesh, "probuilder.face.extrude");
                return MeshResult(
                    mesh,
                    $"extruded {selected.Count} face(s) of {GetHierarchyPath(mesh.transform)} by {Format(distance)} ({created.Length} side face(s) added)",
                    IndicesOf(mesh, selected));
            }
            catch (Exception ex)
            {
                return Error($"probuilder.face.extrude failed: {ex.Message}");
            }
        }

        private static bool TryParseExtrudeMethod(string? method, out ExtrudeMethod extrudeMethod, out string error)
        {
            switch ((method ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "_"))
            {
                case "":
                case "face_normal":
                    extrudeMethod = ExtrudeMethod.FaceNormal;
                    break;
                case "vertex_normal":
                    extrudeMethod = ExtrudeMethod.VertexNormal;
                    break;
                case "individual":
                case "individual_faces":
                    extrudeMethod = ExtrudeMethod.IndividualFaces;
                    break;
                default:
                    extrudeMethod = ExtrudeMethod.FaceNormal;
                    error = $"unknown method '{method}'; use face_normal, vertex_normal, or individual";
                    return false;
            }

            error = string.Empty;
            return true;
        }

        // ── probuilder.face.move ─────────────────────────────────────────────

        [UnifoclCommand(
            "probuilder.face.move",
            "Translate faces of a ProBuilder mesh; vertices shared with neighbouring faces move too, so connected " +
            "geometry stretches (e.g. raise the top of a box). target (required): hierarchy path. " +
            "faces (required): " + FacesSelectorHelp + " " +
            "offset (required): 'x,y,z' in meters. space: world (default) | local. SafeWrite.",
            Category)]
        public static string MoveFaces(string target, string faces, string offset, string space = "world")
        {
            try
            {
                if (!TryResolveProBuilderMesh(target, out var mesh, out var error)
                    || !TrySelectFaces(mesh, faces, out var selected, out error))
                {
                    return Error(error);
                }

                if (!TryParseVector3(offset, allowUniform: false, out var delta, out error))
                {
                    return Error($"offset: {error}");
                }

                var normalizedSpace = (space ?? string.Empty).Trim().ToLowerInvariant();
                if (normalizedSpace.Length > 0 && normalizedSpace != "world" && normalizedSpace != "local")
                {
                    return Error($"unknown space '{space}'; use world or local");
                }

                var localDelta = normalizedSpace == "local" ? delta : mesh.transform.InverseTransformVector(delta);
                var action = $"move {selected.Count} face(s) of {GetHierarchyPath(mesh.transform)} by {Format(delta)} ({(normalizedSpace == "local" ? "local" : "world")})";
                if (DaemonDryRunContext.IsActive)
                {
                    return DryRunResult(mesh, action, IndicesOf(mesh, selected));
                }

                RecordUndo(mesh, "unifocl probuilder.face.move");
                mesh.TranslateVertices(selected, localDelta);
                CommitMeshChange(mesh, "probuilder.face.move");
                return MeshResult(
                    mesh,
                    $"moved {selected.Count} face(s) of {GetHierarchyPath(mesh.transform)} by {Format(delta)} ({(normalizedSpace == "local" ? "local" : "world")})",
                    IndicesOf(mesh, selected));
            }
            catch (Exception ex)
            {
                return Error($"probuilder.face.move failed: {ex.Message}");
            }
        }

        // ── probuilder.face.set_material ─────────────────────────────────────

        [UnifoclCommand(
            "probuilder.face.set_material",
            "Assign a material to faces of a ProBuilder mesh (adds a submesh when needed). target (required): hierarchy path. " +
            "material (required): Material asset path. faces: " + FacesSelectorHelp + " Default all. SafeWrite.",
            Category)]
        public static string SetFaceMaterial(string target, string material, string faces = "all")
        {
            try
            {
                if (!TryResolveProBuilderMesh(target, out var mesh, out var error)
                    || !TrySelectFaces(mesh, faces, out var selected, out error)
                    || !TryLoadMaterial(material, out var faceMaterial, out error))
                {
                    return Error(error);
                }

                var action = $"assign '{DescribeAsset(faceMaterial)}' to {selected.Count} face(s) of {GetHierarchyPath(mesh.transform)}";
                if (DaemonDryRunContext.IsActive)
                {
                    return DryRunResult(mesh, action, IndicesOf(mesh, selected));
                }

                RecordUndo(mesh, "unifocl probuilder.face.set_material");
                var renderer = mesh.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    // SetMaterial rewrites the renderer's material array alongside the face submesh indices.
                    UnityEditor.Undo.RecordObject(renderer, "unifocl probuilder.face.set_material");
                }

                mesh.SetMaterial(selected, faceMaterial);
                CommitMeshChange(mesh, "probuilder.face.set_material");
                return MeshResult(
                    mesh,
                    $"assigned '{DescribeAsset(faceMaterial)}' to {selected.Count} face(s) of {GetHierarchyPath(mesh.transform)}",
                    IndicesOf(mesh, selected));
            }
            catch (Exception ex)
            {
                return Error($"probuilder.face.set_material failed: {ex.Message}");
            }
        }

        // ── probuilder.face.delete ───────────────────────────────────────────

        [UnifoclCommand(
            "probuilder.face.delete",
            "Delete faces from a ProBuilder mesh, leaving an opening (e.g. remove the top of a box to make a tray). " +
            "Refuses to delete every face; delete the GameObject instead. target (required): hierarchy path. " +
            "faces (required): " + FacesSelectorHelp + " Face indices shift afterwards; re-read probuilder.mesh.info. DestructiveWrite.",
            Category)]
        public static string DeleteFaces(string target, string faces)
        {
            try
            {
                if (!TryResolveProBuilderMesh(target, out var mesh, out var error)
                    || !TrySelectFaces(mesh, faces, out var selected, out error))
                {
                    return Error(error);
                }

                if (selected.Count >= mesh.faceCount)
                {
                    return Error("the selector matches every face; delete the GameObject instead of emptying its mesh");
                }

                var action = $"delete {selected.Count} face(s) of {GetHierarchyPath(mesh.transform)}";
                if (DaemonDryRunContext.IsActive)
                {
                    return DryRunResult(mesh, action, IndicesOf(mesh, selected));
                }

                RecordUndo(mesh, "unifocl probuilder.face.delete");
                mesh.DeleteFaces(selected);
                CommitMeshChange(mesh, "probuilder.face.delete");
                return MeshResult(mesh, $"deleted {selected.Count} face(s) of {GetHierarchyPath(mesh.transform)}");
            }
            catch (Exception ex)
            {
                return Error($"probuilder.face.delete failed: {ex.Message}");
            }
        }

        // ── probuilder.face.flip_normals ─────────────────────────────────────

        [UnifoclCommand(
            "probuilder.face.flip_normals",
            "Reverse the winding (normal direction) of faces of a ProBuilder mesh, e.g. to see a box from the inside " +
            "as a room. target (required): hierarchy path. faces: " + FacesSelectorHelp + " Default all. SafeWrite.",
            Category)]
        public static string FlipFaceNormals(string target, string faces = "all")
        {
            try
            {
                if (!TryResolveProBuilderMesh(target, out var mesh, out var error)
                    || !TrySelectFaces(mesh, faces, out var selected, out error))
                {
                    return Error(error);
                }

                var action = $"flip the normals of {selected.Count} face(s) of {GetHierarchyPath(mesh.transform)}";
                if (DaemonDryRunContext.IsActive)
                {
                    return DryRunResult(mesh, action, IndicesOf(mesh, selected));
                }

                RecordUndo(mesh, "unifocl probuilder.face.flip_normals");
                foreach (var face in selected)
                {
                    face.Reverse();
                }

                CommitMeshChange(mesh, "probuilder.face.flip_normals");
                return MeshResult(
                    mesh,
                    $"flipped the normals of {selected.Count} face(s) of {GetHierarchyPath(mesh.transform)}",
                    IndicesOf(mesh, selected));
            }
            catch (Exception ex)
            {
                return Error($"probuilder.face.flip_normals failed: {ex.Message}");
            }
        }

        // ── probuilder.face.subdivide ────────────────────────────────────────

        [UnifoclCommand(
            "probuilder.face.subdivide",
            "Subdivide faces of a ProBuilder mesh: each face is split by connecting its edge midpoints through its center. " +
            "target (required): hierarchy path. faces: " + FacesSelectorHelp + " Default all. " +
            "Returns the indices of the new faces. SafeWrite.",
            Category)]
        public static string SubdivideFaces(string target, string faces = "all")
        {
            try
            {
                if (!TryResolveProBuilderMesh(target, out var mesh, out var error)
                    || !TrySelectFaces(mesh, faces, out var selected, out error))
                {
                    return Error(error);
                }

                var action = $"subdivide {selected.Count} face(s) of {GetHierarchyPath(mesh.transform)}";
                if (DaemonDryRunContext.IsActive)
                {
                    return DryRunResult(mesh, action, IndicesOf(mesh, selected));
                }

                RecordUndo(mesh, "unifocl probuilder.face.subdivide");
                var created = ConnectElements.Connect(mesh, selected);
                if (created == null)
                {
                    return Error("ProBuilder could not subdivide the selected faces");
                }

                CommitMeshChange(mesh, "probuilder.face.subdivide");
                return MeshResult(
                    mesh,
                    $"subdivided {selected.Count} face(s) of {GetHierarchyPath(mesh.transform)} into {created.Length} face(s)",
                    IndicesOf(mesh, created));
            }
            catch (Exception ex)
            {
                return Error($"probuilder.face.subdivide failed: {ex.Message}");
            }
        }

        // ── probuilder.edge.bevel ────────────────────────────────────────────

        [UnifoclCommand(
            "probuilder.edge.bevel",
            "Bevel (chamfer) the edges of the selected faces of a ProBuilder mesh; with faces=all every edge is bevelled. " +
            "target (required): hierarchy path. faces: " + FacesSelectorHelp + " Default all. " +
            "amount: bevel size as a fraction of the adjacent faces, greater than 0 and at most 1 (default 0.1). " +
            "Returns the indices of the new bevel faces. SafeWrite.",
            Category)]
        public static string BevelEdges(string target, string faces = "all", float amount = 0.1f)
        {
            try
            {
                if (!TryResolveProBuilderMesh(target, out var mesh, out var error)
                    || !TrySelectFaces(mesh, faces, out var selected, out error))
                {
                    return Error(error);
                }

                if (!IsFinite(amount) || amount <= 0f || amount > 1f)
                {
                    return Error("amount must be greater than 0 and at most 1");
                }

                var edges = selected.SelectMany(face => face.edges).Distinct().ToList();
                var action = $"bevel {edges.Count} edge(s) of {selected.Count} face(s) of {GetHierarchyPath(mesh.transform)} by {Format(amount)}";
                if (DaemonDryRunContext.IsActive)
                {
                    return DryRunResult(mesh, action, IndicesOf(mesh, selected));
                }

                RecordUndo(mesh, "unifocl probuilder.edge.bevel");
                var created = Bevel.BevelEdges(mesh, edges, amount);
                if (created == null)
                {
                    return Error("ProBuilder could not bevel the selected edges");
                }

                CommitMeshChange(mesh, "probuilder.edge.bevel");
                return MeshResult(
                    mesh,
                    $"bevelled {edges.Count} edge(s) of {GetHierarchyPath(mesh.transform)} by {Format(amount)} ({created.Count} face(s) added)",
                    IndicesOf(mesh, created));
            }
            catch (Exception ex)
            {
                return Error($"probuilder.edge.bevel failed: {ex.Message}");
            }
        }
    }
}
#endif
