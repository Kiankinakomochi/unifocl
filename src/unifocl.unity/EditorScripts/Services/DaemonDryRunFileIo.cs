#if UNITY_EDITOR
using System.IO;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Dry-run-aware <c>System.IO</c> wrappers for <c>[UnifoclCommand]</c> methods.
    /// Direct <c>System.IO</c> writes inside command methods bypass the Undo-based
    /// dry-run sandbox (see analyzer rule UNIFOCL001); these helpers skip the write
    /// while a dry-run is active so command code keeps a single code path.
    /// Each method returns true when the operation was performed, false when it was
    /// skipped because a dry-run is active.
    /// </summary>
    internal static class DaemonDryRunFileIo
    {
        public static bool CreateDirectory(string path)
        {
            if (DaemonDryRunContext.IsActive)
            {
                return false;
            }

            Directory.CreateDirectory(path);
            return true;
        }

        public static bool WriteAllText(string path, string contents)
        {
            if (DaemonDryRunContext.IsActive)
            {
                return false;
            }

            File.WriteAllText(path, contents);
            return true;
        }

        public static bool DeleteFileIfExists(string path)
        {
            if (DaemonDryRunContext.IsActive)
            {
                return false;
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }

        public static bool MoveFile(string sourcePath, string destinationPath)
        {
            if (DaemonDryRunContext.IsActive)
            {
                return false;
            }

            File.Move(sourcePath, destinationPath);
            return true;
        }
    }
}
#endif
