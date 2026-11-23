#if UNITY_EDITOR && UNIFOCL_PIPELINE
#nullable enable
using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge.Pipeline
{
    /// <summary>
    /// Quits a batchmode Editor that the unifocl CLI started once no unifocl command has arrived for a
    /// while, so an Editor left behind by an agent doesn't run forever.
    /// </summary>
    /// <remarks>
    /// The CLI starts such Editors with <c>unity open --batchmode</c> and passes
    /// <c>-unifoclIdleTimeoutSeconds N</c>. Editors started any other way, GUI or batchmode, never see the
    /// argument and are left alone. Every <c>unifocl.bridge</c> command counts as activity
    /// (<see cref="CLIDaemon.MarkActivity"/>).
    /// </remarks>
    [InitializeOnLoad]
    internal static class UnifoclHostSession
    {
        private const string IdleTimeoutArgument = "-unifoclIdleTimeoutSeconds";
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

        private static readonly TimeSpan IdleTimeout;

        // A domain reload resets static state, including the activity clock in CLIDaemon, so the timeout
        // counts from the latest of the reload and the last command.
        private static readonly DateTime LoadedAtUtc = DateTime.UtcNow;
        private static DateTime _nextCheckUtc;

        static UnifoclHostSession()
        {
            if (!Application.isBatchMode || !TryReadIdleTimeout(Environment.GetCommandLineArgs(), out IdleTimeout))
            {
                return;
            }

            EditorApplication.update += CheckIdle;
        }

        private static void CheckIdle()
        {
            var now = DateTime.UtcNow;
            if (now < _nextCheckUtc)
            {
                return;
            }

            _nextCheckUtc = now + CheckInterval;
            var lastActivity = CLIDaemon.LastActivityUtc > LoadedAtUtc ? CLIDaemon.LastActivityUtc : LoadedAtUtc;
            if (now - lastActivity < IdleTimeout || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            EditorApplication.update -= CheckIdle;
            Debug.Log($"[unifocl] no unifocl command for {IdleTimeout.TotalSeconds:0}s; quitting the batchmode Editor the unifocl CLI started.");
            EditorApplication.Exit(0);
        }

        internal static bool TryReadIdleTimeout(string[] args, out TimeSpan timeout)
        {
            timeout = TimeSpan.Zero;
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], IdleTimeoutArgument, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                    && seconds > 0)
                {
                    timeout = TimeSpan.FromSeconds(seconds);
                    return true;
                }
            }

            return false;
        }
    }
}
#endif
