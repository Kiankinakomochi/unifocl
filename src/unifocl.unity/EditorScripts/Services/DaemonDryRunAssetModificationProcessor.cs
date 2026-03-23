#if UNITY_EDITOR
using UnityEditor;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Blocks AssetDatabase-level file operations (save, move, delete) while a unifocl
    /// dry-run is in progress. Activated by <see cref="DaemonDryRunContext.IsActive"/>.
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

            // Return an empty array — Unity will not persist any of the requested assets.
            return System.Array.Empty<string>();
        }

        static AssetMoveResult OnWillMoveAsset(string sourcePath, string destinationPath)
        {
            if (!DaemonDryRunContext.IsActive)
            {
                return AssetMoveResult.DidNotMove;
            }

            return AssetMoveResult.FailedMove;
        }

        static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions options)
        {
            if (!DaemonDryRunContext.IsActive)
            {
                return AssetDeleteResult.DidNotDelete;
            }

            return AssetDeleteResult.FailedDelete;
        }
    }
}
#endif
