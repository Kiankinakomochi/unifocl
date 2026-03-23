#if UNITY_EDITOR
using System;
using System.Diagnostics;
using UnityEditor.Compilation;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Global utility for programmatically triggering a Unity script recompile.
    ///
    /// Combines two mechanisms:
    ///   1. OS-level editor window activation — ensures Unity's file-watcher ticks
    ///      immediately so newly copied .cs files are detected without manual focus.
    ///   2. <see cref="CompilationPipeline.RequestScriptCompilation"/> — queues a
    ///      script-only recompile; much cheaper than AssetDatabase.Refresh() on
    ///      large projects because it skips asset reimporting entirely.
    ///
    /// Window-grab behaviour is controlled by <see cref="UnifoclEditorConfig.allowWindowGrab"/>.
    /// Set it to <c>false</c> for headless CI runners where window activation is
    /// meaningless or would fail.
    /// </summary>
    internal static class UnifoclCompilationService
    {
        /// <summary>
        /// Optionally activates the Unity editor window (so the file-watcher ticks),
        /// then requests a script-only recompile via the Compilation Pipeline.
        /// </summary>
        public static void RequestRecompile()
        {
            var config = UnifoclEditorConfig.Load();
            if (config.allowWindowGrab)
                TryGrabEditorWindow();
            CompilationPipeline.RequestScriptCompilation();
        }

        // ── Platform-specific window activation ───────────────────────────────

        private static void TryGrabEditorWindow()
        {
            try
            {
#if UNITY_EDITOR_OSX
                // AppleScript: bring Unity to the foreground
                SpawnSilent("osascript", "-e 'tell application \"Unity\" to activate'");

#elif UNITY_EDITOR_WIN
                // Shell.Application COM activation — no P/Invoke required
                SpawnSilent(
                    "powershell",
                    "-NoProfile -NonInteractive -Command " +
                    "\"(New-Object -ComObject Shell.Application).AppActivate('Unity')\"");

#elif UNITY_EDITOR_LINUX
                // xdotool preferred; wmctrl as fallback
                SpawnSilent(
                    "bash",
                    "-c \"xdotool search --name 'Unity' windowactivate 2>/dev/null" +
                    " || wmctrl -a Unity 2>/dev/null\"");
#endif
            }
            catch (Exception ex)
            {
                // Non-fatal: log and continue — recompile will still be requested
                UnityEngine.Debug.LogWarning($"[unifocl] window grab failed (non-fatal): {ex.Message}");
            }
        }

        private static void SpawnSilent(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute  = false,
                CreateNoWindow   = true,
                RedirectStandardOutput = false,
                RedirectStandardError  = false,
            };
            Process.Start(psi)?.Dispose();
        }
    }
}
#endif
