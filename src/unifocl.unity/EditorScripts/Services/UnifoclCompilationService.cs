#if UNITY_EDITOR
using System;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Triggers a Unity script recompile and reports whether the compile pipeline
    /// actually started.
    ///
    /// The flow combines four mechanisms in deterministic order:
    ///
    ///   1. <see cref="AssetDatabase.Refresh()"/> — forces Unity to rescan the
    ///      asset folder so newly copied .cs files become visible to the
    ///      compiler. Without this step new scripts copied while the editor is
    ///      not focused are routinely missed.
    ///   2. <see cref="CompilationPipeline.RequestScriptCompilation"/> — queues
    ///      a script-only recompile. When <c>forceRecompile</c> is set the
    ///      <see cref="RequestScriptCompilationOptions.CleanBuildCache"/> flag
    ///      is passed so the incremental cache is busted.
    ///   3. Optional OS window-grab (cosmetic) — only runs if the editor config
    ///      enables it; left as a fallback for users who want the editor brought
    ///      to the foreground but never relied on for correctness.
    ///   4. Compile-did-not-start watchdog — a short EditorApplication.update
    ///      poll that surfaces a diagnostic if <c>EditorApplication.isCompiling</c>
    ///      never flips true. Catches the common silent-failure modes: auto
    ///      refresh disabled, no script changes, duplicate asmdef names,
    ///      domain-reload locks held.
    /// </summary>
    internal static class UnifoclCompilationService
    {
        // Watchdog: how long to wait for EditorApplication.isCompiling to become
        // true after RequestScriptCompilation before declaring "did not start".
        // Sized to absorb a preceding AssetDatabase.Refresh(), which can take
        // around a second on larger projects before the compiler actually
        // begins.
        private const double CompileStartWatchdogSeconds = 4.0;

        private static double _watchdogDeadlineSeconds;
        private static Action<string>? _watchdogReporter;
        private static bool _watchdogActive;

        public readonly struct RecompileOutcome
        {
            public RecompileOutcome(bool ok, string message)
            {
                Ok = ok;
                Message = message;
            }

            public bool Ok { get; }
            public string Message { get; }
        }

        public static RecompileOutcome RequestRecompile(bool forceRecompile = false, Action<string>? watchdogReporter = null)
        {
            try
            {
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                return new RecompileOutcome(false, $"AssetDatabase.Refresh failed: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                if (forceRecompile)
                {
                    CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
                }
                else
                {
                    CompilationPipeline.RequestScriptCompilation();
                }
            }
            catch (Exception ex)
            {
                return new RecompileOutcome(false, $"RequestScriptCompilation failed: {ex.GetType().Name}: {ex.Message}");
            }

            ArmCompileStartWatchdog(watchdogReporter);

            try
            {
                var config = UnifoclEditorConfig.Load();
                if (config.allowWindowGrab)
                {
                    TryGrabEditorWindow();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] window grab raised non-fatal exception: {ex.Message}");
            }

            return new RecompileOutcome(true, forceRecompile
                ? "force recompile requested"
                : "compile request submitted");
        }

        private static void ArmCompileStartWatchdog(Action<string>? reporter)
        {
            _watchdogReporter = reporter;
            _watchdogDeadlineSeconds = EditorApplication.timeSinceStartup + CompileStartWatchdogSeconds;

            if (_watchdogActive)
            {
                return;
            }

            _watchdogActive = true;
            EditorApplication.update += OnEditorUpdateForWatchdog;
        }

        private static void OnEditorUpdateForWatchdog()
        {
            if (EditorApplication.isCompiling)
            {
                ClearWatchdog();
                return;
            }

            if (EditorApplication.timeSinceStartup < _watchdogDeadlineSeconds)
            {
                return;
            }

            var reporter = _watchdogReporter;
            ClearWatchdog();

            string diagnostic =
                "compile request did not start within " +
                $"{CompileStartWatchdogSeconds:F1}s. Likely causes: " +
                "(a) Auto Refresh is disabled, (b) no script changes since last compile, " +
                "(c) duplicate asmdef names blocking compile, " +
                "(d) editor update or domain reload lock held.";

            try
            {
                reporter?.Invoke(diagnostic);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] compile watchdog reporter raised: {ex.Message}");
            }
        }

        private static void ClearWatchdog()
        {
            if (!_watchdogActive)
            {
                return;
            }

            EditorApplication.update -= OnEditorUpdateForWatchdog;
            _watchdogActive = false;
            _watchdogReporter = null;
        }

        // ── Platform-specific window activation (cosmetic, opt-in) ────────────

        private static void TryGrabEditorWindow()
        {
#if UNITY_EDITOR_OSX
            SpawnSilent("osascript", "-e 'tell application \"Unity\" to activate'");
#elif UNITY_EDITOR_WIN
            SpawnSilent(
                "powershell",
                "-NoProfile -NonInteractive -Command " +
                "\"(New-Object -ComObject Shell.Application).AppActivate('Unity')\"");
#elif UNITY_EDITOR_LINUX
            SpawnSilent(
                "bash",
                "-c \"xdotool search --name 'Unity' windowactivate 2>/dev/null" +
                " || wmctrl -a Unity 2>/dev/null\"");
#endif
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
