#if UNITY_EDITOR
#nullable enable
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static partial class DaemonProjectService
    {
        // ──────────────────────────────────────────────────────────────
        //  DTOs — export-thumbnail
        // ──────────────────────────────────────────────────────────────

        [Serializable]
        private sealed class ExportThumbnailResponsePayload
        {
            public string exportedPath = string.Empty;
            public string assetType = string.Empty;
            public string assetGuid = string.Empty;
            public long fileSizeBytes;
            public int width;
            public int height;
        }

        // ──────────────────────────────────────────────────────────────
        //  Handler
        // ──────────────────────────────────────────────────────────────

        private static string ExecuteExportThumbnail(ProjectCommandRequest request)
        {
            if (!IsValidAssetPath(request.assetPath))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "export-thumbnail requires a valid assetPath"
                });
            }

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(request.assetPath);
            if (asset == null)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"asset not found: {request.assetPath}"
                });
            }

            var guid = AssetDatabase.AssetPathToGUID(request.assetPath);
            if (string.IsNullOrEmpty(guid))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"could not resolve GUID for: {request.assetPath}"
                });
            }

            // Attempt to get a full-size preview, polling for async generation.
            Texture2D? preview = AssetPreview.GetAssetPreview(asset);
            if (preview == null)
            {
                // Unity generates previews asynchronously — poll briefly.
                // GetInstanceID/IsLoadingAssetPreview(int) deprecated in Unity 6; EntityId replacements
                // unavailable in Unity 2021–2022 LTS — suppress until minimum version is raised.
#pragma warning disable CS0618
                var instanceId = asset.GetInstanceID();
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (AssetPreview.IsLoadingAssetPreview(instanceId) && DateTime.UtcNow < deadline)
#pragma warning restore CS0618
                {
                    System.Threading.Thread.Sleep(50);
                    preview = AssetPreview.GetAssetPreview(asset);
                    if (preview != null) break;
                }
            }

            // Fallback to mini-thumbnail icon.
            if (preview == null)
            {
                preview = AssetPreview.GetMiniThumbnail(asset);
            }

            if (preview == null)
            {
                // No visual preview available — return metadata only.
                var metaOnly = new ExportThumbnailResponsePayload
                {
                    assetType = asset.GetType().Name,
                    assetGuid = guid,
                    fileSizeBytes = GetAssetFileSize(request.assetPath)
                };
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = "no visual preview available for this asset type; metadata returned",
                    kind = "export-thumbnail",
                    content = JsonUtility.ToJson(metaOnly)
                });
            }

            // Ensure the preview texture is readable — blit if necessary.
            Texture2D readable = EnsureReadableTexture(preview);

            byte[] pngBytes;
            try
            {
                pngBytes = readable.EncodeToPNG();
            }
            finally
            {
                if (readable != preview)
                {
                    UnityEngine.Object.DestroyImmediate(readable);
                }
            }

            if (pngBytes == null || pngBytes.Length == 0)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "failed to encode preview to PNG"
                });
            }

            // Write to .unifocl-runtime/thumbnails/{guid}.png
            var projectRoot = Path.GetDirectoryName(Application.dataPath)!;
            var thumbDir = Path.Combine(projectRoot, ".unifocl-runtime", "thumbnails");
            Directory.CreateDirectory(thumbDir);
            var thumbPath = Path.Combine(thumbDir, $"{guid}.png");
            File.WriteAllBytes(thumbPath, pngBytes);

            var payload = new ExportThumbnailResponsePayload
            {
                exportedPath = thumbPath,
                assetType = asset.GetType().Name,
                assetGuid = guid,
                fileSizeBytes = GetAssetFileSize(request.assetPath),
                width = preview.width,
                height = preview.height
            };

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = "thumbnail exported",
                kind = "export-thumbnail",
                content = JsonUtility.ToJson(payload)
            });
        }

        /// <summary>
        /// Copies a potentially non-readable texture into a readable Texture2D via RenderTexture blit.
        /// Returns the original texture unchanged if it is already readable.
        /// </summary>
        private static Texture2D EnsureReadableTexture(Texture2D source)
        {
            try
            {
                // Try accessing pixels — if the texture is readable this succeeds.
                source.GetPixel(0, 0);
                return source;
            }
            catch (UnityException)
            {
                // Texture is not readable — blit through a RenderTexture.
            }

            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, rt);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;

            var readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            return readable;
        }

        private static long GetAssetFileSize(string assetPath)
        {
            try
            {
                var projectRoot = Path.GetDirectoryName(Application.dataPath)!;
                var fullPath = Path.Combine(projectRoot, assetPath);
                return File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
#endif
