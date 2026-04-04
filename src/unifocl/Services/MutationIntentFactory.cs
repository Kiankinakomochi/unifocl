using System;

internal static class MutationIntentFactory
{
    public static HierarchyCommandRequestDto EnsureHierarchyIntent(HierarchyCommandRequestDto request)
    {
        if (!DaemonMutationActionCatalog.IsHierarchyMutation(request.Action) || request.Intent is not null)
        {
            return request;
        }

        var target = request.TargetId is int targetId && targetId != 0
            ? $"node:{targetId}"
            : (request.ParentId is int parentId && parentId != 0 ? $"parent:{parentId}" : "scene-root");
        var nextValue = request.Name
            ?? request.Type
            ?? (request.Count is int count && count > 0 ? count.ToString() : null);
        return request with
        {
            Intent = CreateIntent(target, request.Action, oldValue: null, nextValue)
        };
    }

    public static ProjectCommandRequestDto EnsureProjectIntent(ProjectCommandRequestDto request)
    {
        if (!DaemonMutationActionCatalog.IsProjectMutation(request.Action) || request.Intent is not null)
        {
            return request;
        }

        var target = !string.IsNullOrWhiteSpace(request.AssetPath)
            ? request.AssetPath!
            : request.Action;
        return request with
        {
            Intent = CreateIntent(target, request.Action, request.AssetPath, request.NewAssetPath)
        };
    }

    public static MutationIntentDto CreateInspectorIntent(
        string action,
        string? targetPath,
        int? componentIndex,
        string? componentName,
        string? fieldName,
        string? value)
    {
        var target = string.IsNullOrWhiteSpace(targetPath)
            ? "inspector"
            : targetPath!;
        var property = !string.IsNullOrWhiteSpace(fieldName)
            ? fieldName!
            : (componentIndex is int index && index >= 0
                ? $"component[{index}]"
                : (!string.IsNullOrWhiteSpace(componentName) ? componentName! : action));
        return CreateIntent(target, property, oldValue: null, value);
    }

    private static MutationIntentDto CreateIntent(string target, string property, string? oldValue, string? nextValue)
    {
        return new MutationIntentDto(
            Guid.NewGuid().ToString("N"),
            target,
            property,
            oldValue,
            nextValue,
            new MutationIntentFlagsDto(DryRun: CliDryRunScope.IsEnabled, RequireRollback: true));
    }
}

internal static class DaemonMutationActionCatalog
{
    public static bool IsHierarchyMutation(string action)
    {
        return action.Equals("mk", StringComparison.OrdinalIgnoreCase)
               || action.Equals("toggle", StringComparison.OrdinalIgnoreCase)
               || action.Equals("rm", StringComparison.OrdinalIgnoreCase)
               || action.Equals("rename", StringComparison.OrdinalIgnoreCase)
               || action.Equals("mv", StringComparison.OrdinalIgnoreCase)
               || action.Equals("duplicate", StringComparison.OrdinalIgnoreCase)
               || action.Equals("dup", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInspectorMutation(string action)
    {
        return action.Equals("add-component", StringComparison.OrdinalIgnoreCase)
               || action.Equals("remove-component", StringComparison.OrdinalIgnoreCase)
               || action.Equals("duplicate-component", StringComparison.OrdinalIgnoreCase)
               || action.Equals("toggle-component", StringComparison.OrdinalIgnoreCase)
               || action.Equals("toggle-field", StringComparison.OrdinalIgnoreCase)
               || action.Equals("set-field", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsProjectMutation(string action)
    {
        return action.Equals("mk-script", StringComparison.OrdinalIgnoreCase)
               || action.Equals("mk-asset", StringComparison.OrdinalIgnoreCase)
               || action.Equals("rename-asset", StringComparison.OrdinalIgnoreCase)
               || action.Equals("duplicate-asset", StringComparison.OrdinalIgnoreCase)
               || action.Equals("remove-asset", StringComparison.OrdinalIgnoreCase)
               || action.Equals("upm-install", StringComparison.OrdinalIgnoreCase)
               || action.Equals("upm-remove", StringComparison.OrdinalIgnoreCase)
               || action.Equals("build-scenes-set", StringComparison.OrdinalIgnoreCase)
               || action.Equals("scene-load", StringComparison.OrdinalIgnoreCase)
               || action.Equals("scene-add", StringComparison.OrdinalIgnoreCase)
               || action.Equals("scene-unload", StringComparison.OrdinalIgnoreCase)
               || action.Equals("scene-remove", StringComparison.OrdinalIgnoreCase)
               || action.Equals("console-clear", StringComparison.OrdinalIgnoreCase)
               || action.Equals("hierarchy-duplicate", StringComparison.OrdinalIgnoreCase)
               || action.Equals("prefab-create", StringComparison.OrdinalIgnoreCase)
               || action.Equals("prefab-apply", StringComparison.OrdinalIgnoreCase)
               || action.Equals("prefab-revert", StringComparison.OrdinalIgnoreCase)
               || action.Equals("prefab-unpack", StringComparison.OrdinalIgnoreCase)
               || action.Equals("prefab-variant", StringComparison.OrdinalIgnoreCase)
               || action.Equals("addressables-cli", StringComparison.OrdinalIgnoreCase)
               || action.Equals("eval-code", StringComparison.OrdinalIgnoreCase)
               || action.Equals("playmode-start", StringComparison.OrdinalIgnoreCase)
               || action.Equals("playmode-stop", StringComparison.OrdinalIgnoreCase)
               || action.Equals("playmode-pause", StringComparison.OrdinalIgnoreCase)
               || action.Equals("playmode-resume", StringComparison.OrdinalIgnoreCase)
               || action.Equals("playmode-step", StringComparison.OrdinalIgnoreCase)
               || action.Equals("time-scale", StringComparison.OrdinalIgnoreCase);
    }
}
