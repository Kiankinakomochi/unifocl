#if UNITY_EDITOR
using System;
using System.IO;

namespace UniFocl.EditorBridge.Profiling
{
    /// <summary>
    /// Capture path normalization and validation for profiler file I/O.
    /// Handles the .data / .raw / .snap format split explicitly.
    /// </summary>
    internal static class ProfilerPathUtils
    {
        public const string EditorCaptureExtension = ".data";
        public const string RawBinaryLogExtension  = ".raw";
        public const string SnapshotExtension       = ".snap";

        /// <summary>
        /// Normalize a user-supplied capture path for editor save/load.
        /// If no extension is given, appends <see cref="EditorCaptureExtension"/>.
        /// Does NOT silently rewrite unknown extensions — they pass through.
        /// </summary>
        public static string NormalizeEditorCapturePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Capture path must not be empty.");

            path = path.Trim();
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
                path += EditorCaptureExtension;

            return Path.GetFullPath(path);
        }

        /// <summary>
        /// Normalize a user-supplied path for binary log output.
        /// If no extension is given, appends <see cref="RawBinaryLogExtension"/>.
        /// </summary>
        public static string NormalizeBinaryLogPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Binary log path must not be empty.");

            path = path.Trim();
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
                path += RawBinaryLogExtension;

            return Path.GetFullPath(path);
        }

        /// <summary>
        /// Normalize a user-supplied path for memory snapshot output.
        /// If no extension is given, appends <see cref="SnapshotExtension"/>.
        /// </summary>
        public static string NormalizeSnapshotPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Snapshot path must not be empty.");

            path = path.Trim();
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
                path += SnapshotExtension;

            return Path.GetFullPath(path);
        }

        /// <summary>
        /// Generate a temp file path in the same directory as the target, for atomic rename.
        /// </summary>
        public static string GetTempPathForAtomic(string targetPath)
        {
            var dir  = Path.GetDirectoryName(targetPath) ?? ".";
            var name = Path.GetFileNameWithoutExtension(targetPath);
            return Path.Combine(dir, $".{name}.tmp.{Guid.NewGuid():N}");
        }

        /// <summary>
        /// Ensure the parent directory exists for a target path.
        /// </summary>
        public static void EnsureParentDirectory(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        /// <summary>
        /// Validate that a path is readable (file exists).
        /// </summary>
        public static bool FileExists(string path) => File.Exists(path);

        /// <summary>
        /// Get file size in bytes, or -1 if not found.
        /// </summary>
        public static long GetFileSize(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return -1; }
        }
    }
}
#endif
