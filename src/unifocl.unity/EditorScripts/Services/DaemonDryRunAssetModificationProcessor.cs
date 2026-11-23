#if UNITY_EDITOR
using UnityEditor;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Blocks AssetDatabase-level file operations (save, move, delete) while a unifocl
    /// dry-run or a sandbox session is in progress. Activated by <see cref="DaemonDryRunContext.IsActive"/>;
    /// in a sandbox session each refused operation is noted for its report.
    ///
    /// Coverage:
    ///   ✅ AssetDatabase.SaveAssets()           — OnWillSaveAssets returns empty array
    ///   ✅ AssetDatabase.MoveAsset()             — OnWillMoveAsset returns FailedMove
    ///   ✅ AssetDatabase.DeleteAsset()           — OnWillDeleteAsset returns FailedDelete
    ///   ⚠️ AssetDatabase.CreateAsset()          — OnWillCreateAsset is informational only;
    ///                                              use Undo.RegisterCreatedObjectUndo in
    ///                                              your tool so the Undo group revert handles it.
    ///   ❌ System.IO.File.WriteAllText() et al. — not interactable from this hook;
    ///                                              flagged at compile time by the
    ///                                              unifocl Roslyn analyzer (UNIFOCL001).
    /// </summary>
    internal sealed class DaemonDryRunAssetModificationProcessor : AssetModificationProcessor
    {
        static string[] OnWillSaveAssets(string[] paths)
        {
            if (!DaemonDryRunContext.IsActive)
            {
                return paths;
            }

            foreach (var path in paths)
            {
                DaemonSandboxSession.RecordBlocked("save", path);
            }

            // Return an empty array — Unity will not persist any of the requested assets.
            return System.Array.Empty<string>();
        }

        static AssetMoveResult OnWillMoveAsset(string sourcePath, string destinationPath)
        {
            if (!DaemonDryRunContext.IsActive)
            {
                return AssetMoveResult.DidNotMove;
            }

            DaemonSandboxSession.RecordBlocked("move", $"{sourcePath} -> {destinationPath}");
            return AssetMoveResult.FailedMove;
        }

        static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions options)
        {
            if (!DaemonDryRunContext.IsActive)
            {
                return AssetDeleteResult.DidNotDelete;
            }

            DaemonSandboxSession.RecordBlocked("delete", assetPath);
            return AssetDeleteResult.FailedDelete;
        }
    }
}
#endif
