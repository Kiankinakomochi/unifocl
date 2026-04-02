#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static partial class DaemonProjectService
    {
        private static bool SupportsFileDryRunPreview(string action)
        {
            return action.Equals("rename-asset", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("remove-asset", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("mk-script", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("mk-asset", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("addressables-cli", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildMkAssetDryRunResponse(
            ProjectCommandRequest request,
            string parentPath,
            string canonicalType,
            int count,
            string requestedName)
        {
            var plannedPaths = BuildMkAssetDryRunPaths(parentPath, canonicalType, requestedName, count);
            var changes = plannedPaths
                .Select(path => new MutationPathChange
                {
                    action = "create",
                    path = path,
                    nextPath = path,
                    metaPath = path + ".meta"
                })
                .ToArray();

            var payload = BuildFileDiffPayloadWithVcs(
                request,
                $"mk-asset preview ({canonicalType})",
                changes);
            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = "dry-run preview",
                kind = "dry-run",
                content = payload
            });
        }

        private static List<string> BuildMkAssetDryRunPaths(
            string parentPath,
            string canonicalType,
            string requestedName,
            int count)
        {
            var planned = new List<string>(count);
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var extension = InferMkAssetExtension(canonicalType);
            var asFolder = canonicalType.Equals("folder", StringComparison.OrdinalIgnoreCase);

            for (var i = 0; i < count; i++)
            {
                var baseName = ResolveMkAssetName(canonicalType, requestedName, i, count);
                var candidate = asFolder
                    ? GenerateUniqueFolderPathWithReservations(parentPath, baseName, reserved)
                    : GenerateUniqueAssetPathWithReservations(parentPath, baseName, extension, reserved);
                reserved.Add(candidate);
                planned.Add(candidate);
            }

            return planned;
        }

        private static string InferMkAssetExtension(string canonicalType)
        {
            return canonicalType switch
            {
                "scene" => ".unity",
                "assemblydefinition" or "testingassemblydefinition" => ".asmdef",
                "assemblydefinitionreference" or "testingassemblydefinitionreference" => ".asmref",
                "shader" => ".shader",
                "computeshader" => ".compute",
                "shaderincludefile" => ".hlsl",
                "material" => ".mat",
                "animatorcontroller" => ".controller",
                "animationclip" => ".anim",
                "timeline" => ".playable",
                "audiomixer" => ".mixer",
                "physicsmaterial" => ".physicMaterial",
                "physicsmaterial2d" => ".physicsMaterial2D",
                "spriteatlas" => ".spriteatlas",
                "inputactions" => ".inputactions",
                "uidocument" or "uxmldocument" => ".uxml",
                "ussstylesheet" => ".uss",
                "lightingsettings" => ".lighting",
                "terrainlayer" => ".terrainlayer",
                "searchindex" => ".index",
                _ => ".asset"
            };
        }

        private static string BuildFileDryRunPayload(
            ProjectCommandRequest request,
            string mutationName,
            IEnumerable<string> preMutationTargets,
            IEnumerable<string>? rollbackCleanupTargets)
        {
            var sourceTargets = preMutationTargets
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var destinationTargets = rollbackCleanupTargets?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            var changes = new List<MutationPathChange>();

            if (mutationName.Equals("rename-asset", StringComparison.OrdinalIgnoreCase))
            {
                var source = sourceTargets.FirstOrDefault() ?? string.Empty;
                var destination = destinationTargets.FirstOrDefault() ?? string.Empty;
                changes.Add(new MutationPathChange
                {
                    action = "rename",
                    path = source,
                    nextPath = destination,
                    metaPath = source + ".meta"
                });
                changes.Add(new MutationPathChange
                {
                    action = "rename-meta",
                    path = source + ".meta",
                    nextPath = destination + ".meta",
                    metaPath = source + ".meta"
                });
            }
            else if (mutationName.Equals("remove-asset", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var source in sourceTargets)
                {
                    changes.Add(new MutationPathChange
                    {
                        action = "remove",
                        path = source,
                        nextPath = "(trash)",
                        metaPath = source + ".meta"
                    });
                    changes.Add(new MutationPathChange
                    {
                        action = "remove-meta",
                        path = source + ".meta",
                        nextPath = "(trash)",
                        metaPath = source + ".meta"
                    });
                }
            }
            else
            {
                foreach (var source in sourceTargets)
                {
                    changes.Add(new MutationPathChange
                    {
                        action = mutationName,
                        path = source,
                        nextPath = source,
                        metaPath = source + ".meta"
                    });
                }
            }

            return BuildFileDiffPayloadWithVcs(request, $"{mutationName} preview", changes);
        }

        private static string BuildFileDiffPayloadWithVcs(
            ProjectCommandRequest request,
            string summary,
            IEnumerable<MutationPathChange> changes)
        {
            var list = changes.ToList();
            ApplyVcsMetadataToChanges(request, list);
            return DaemonDryRunDiffService.BuildFileDiffPayload(summary, list);
        }
    }
}
#nullable restore
#endif
