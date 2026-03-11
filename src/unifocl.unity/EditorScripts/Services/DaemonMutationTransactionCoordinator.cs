#if UNITY_EDITOR
using System;

namespace UniFocl.EditorBridge
{
    internal static class DaemonMutationTransactionCoordinator
    {
        public static bool IsHierarchyMutation(string action)
        {
            return action.Equals("mk", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("toggle", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("rm", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("rename", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("mv", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsInspectorMutation(string action)
        {
            return action.Equals("add-component", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("remove-component", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("toggle-component", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("toggle-field", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("set-field", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsProjectMutation(string action)
        {
            return action.Equals("mk-script", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("mk-asset", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("rename-asset", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("remove-asset", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("upm-install", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("upm-remove", StringComparison.OrdinalIgnoreCase)
                   || action.Equals("build-scenes-set", StringComparison.OrdinalIgnoreCase);
        }

        public static MutationTransactionDecision ValidateHierarchyIntent(string action, MutationIntentEnvelope intent)
        {
            return ValidateIntent("hierarchy", action, intent);
        }

        public static MutationTransactionDecision ValidateInspectorIntent(string action, MutationIntentEnvelope intent)
        {
            return ValidateIntent("inspector", action, intent);
        }

        public static MutationTransactionDecision ValidateProjectIntent(string action, MutationIntentEnvelope intent)
        {
            return ValidateIntent("project", action, intent);
        }

        private static MutationTransactionDecision ValidateIntent(string mode, string action, MutationIntentEnvelope intent)
        {
            if (intent is null)
            {
                return MutationTransactionDecision.Error($"mutation intent envelope is required for {mode}:{action}");
            }

            if (string.IsNullOrWhiteSpace(intent.transactionId))
            {
                return MutationTransactionDecision.Error("mutation intent transactionId is required");
            }

            if (string.IsNullOrWhiteSpace(intent.target))
            {
                return MutationTransactionDecision.Error("mutation intent target is required");
            }

            if (string.IsNullOrWhiteSpace(intent.property))
            {
                return MutationTransactionDecision.Error("mutation intent property is required");
            }

            if (intent.flags is null)
            {
                return MutationTransactionDecision.Error("mutation intent flags are required");
            }

            if (!intent.flags.requireRollback)
            {
                return MutationTransactionDecision.Error("mutation intent must require rollback semantics");
            }

            if (intent.flags.dryRun)
            {
                return MutationTransactionDecision.DryRun($"dry-run accepted ({mode}:{action})");
            }

            var handler = ResolveHandler(mode);
            return MutationTransactionDecision.Success($"handler={handler}");
        }

        private static string ResolveHandler(string mode)
        {
            return mode.Equals("project", StringComparison.OrdinalIgnoreCase)
                ? "filesystem"
                : "memory";
        }
    }

    internal readonly struct MutationTransactionDecision
    {
        private MutationTransactionDecision(bool accepted, bool shouldExecute, string status, string message)
        {
            Accepted = accepted;
            ShouldExecute = shouldExecute;
            Status = status;
            Message = message;
        }

        public bool Accepted { get; }
        public bool ShouldExecute { get; }
        public string Status { get; }
        public string Message { get; }

        public static MutationTransactionDecision Success(string message)
        {
            return new MutationTransactionDecision(true, true, "success", message);
        }

        public static MutationTransactionDecision DryRun(string message)
        {
            return new MutationTransactionDecision(true, false, "success", message);
        }

        public static MutationTransactionDecision Error(string message)
        {
            return new MutationTransactionDecision(false, false, "error", message);
        }
    }
}
#endif
