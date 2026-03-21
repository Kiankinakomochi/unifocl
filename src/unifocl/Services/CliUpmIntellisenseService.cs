internal static class CliUpmIntellisenseService
{
    public static bool TryGetUpmComposerCandidates(
        string input,
        CliSessionState session,
        out List<(string Label, string? CommitCommand)> candidates)
    {
        candidates = [];
        var trimmed = input.TrimStart();
        var isSlash = trimmed.StartsWith("/upm", StringComparison.OrdinalIgnoreCase);
        var isProjectCommand = trimmed.StartsWith("upm", StringComparison.OrdinalIgnoreCase);
        if (!isSlash && !isProjectCommand)
        {
            return false;
        }

        var prefix = isSlash ? "/upm" : "upm";
        var suffix = trimmed.Length > prefix.Length
            ? trimmed[prefix.Length..].TrimStart()
            : string.Empty;

        if (string.IsNullOrWhiteSpace(suffix))
        {
            candidates.Add((isSlash
                ? "/upm list [--outdated] [--builtin] [--git]"
                : "upm list [--outdated] [--builtin] [--git]", isSlash ? "/upm list" : "upm list"));
            candidates.Add((isSlash ? "/upm ls" : "upm ls", isSlash ? "/upm ls" : "upm ls"));
            candidates.Add((isSlash ? "/upm install <target>" : "upm install <target>", isSlash ? "/upm install " : "upm install "));
            candidates.Add((isSlash ? "/upm add <target>" : "upm add <target>", isSlash ? "/upm add " : "upm add "));
            candidates.Add((isSlash ? "/upm i <target>" : "upm i <target>", isSlash ? "/upm i " : "upm i "));
            candidates.Add((isSlash ? "/upm remove <id>" : "upm remove <id>", isSlash ? "/upm remove " : "upm remove "));
            candidates.Add((isSlash ? "/upm rm <id>" : "upm rm <id>", isSlash ? "/upm rm " : "upm rm "));
            candidates.Add((isSlash ? "/upm uninstall <id>" : "upm uninstall <id>", isSlash ? "/upm uninstall " : "upm uninstall "));
            candidates.Add((isSlash ? "/upm update <id> [version]" : "upm update <id> [version]", isSlash ? "/upm update " : "upm update "));
            candidates.Add((isSlash ? "/upm u <id> [version]" : "upm u <id> [version]", isSlash ? "/upm u " : "upm u "));
            return true;
        }

        var upmSuggestions = new List<(string Label, string? CommitCommand)>
        {
            (isSlash ? "/upm list [--outdated] [--builtin] [--git]" : "upm list [--outdated] [--builtin] [--git]", isSlash ? "/upm list" : "upm list"),
            (isSlash ? "/upm ls [--outdated] [--builtin] [--git]" : "upm ls [--outdated] [--builtin] [--git]", isSlash ? "/upm ls" : "upm ls"),
            (isSlash ? "/upm install <target>" : "upm install <target>", isSlash ? "/upm install " : "upm install "),
            (isSlash ? "/upm add <target>" : "upm add <target>", isSlash ? "/upm add " : "upm add "),
            (isSlash ? "/upm i <target>" : "upm i <target>", isSlash ? "/upm i " : "upm i "),
            (isSlash ? "/upm remove <id>" : "upm remove <id>", isSlash ? "/upm remove " : "upm remove "),
            (isSlash ? "/upm rm <id>" : "upm rm <id>", isSlash ? "/upm rm " : "upm rm "),
            (isSlash ? "/upm uninstall <id>" : "upm uninstall <id>", isSlash ? "/upm uninstall " : "upm uninstall "),
            (isSlash ? "/upm update <id> [version]" : "upm update <id> [version]", isSlash ? "/upm update " : "upm update "),
            (isSlash ? "/upm u <id> [version]" : "upm u <id> [version]", isSlash ? "/upm u " : "upm u ")
        };

        var suffixLower = suffix.ToLowerInvariant();
        candidates = upmSuggestions
            .Where(candidate => candidate.Label.Contains(suffixLower, StringComparison.OrdinalIgnoreCase)
                                || candidate.CommitCommand?.Contains(suffixLower, StringComparison.OrdinalIgnoreCase) == true)
            .Take(10)
            .ToList();

        if (suffixLower.StartsWith("list", StringComparison.OrdinalIgnoreCase)
            || suffixLower.StartsWith("ls", StringComparison.OrdinalIgnoreCase))
        {
            var commandHead = isSlash
                ? (suffixLower.StartsWith("ls", StringComparison.OrdinalIgnoreCase) ? "/upm ls" : "/upm list")
                : (suffixLower.StartsWith("ls", StringComparison.OrdinalIgnoreCase) ? "upm ls" : "upm list");
            var flags = new[] { "--outdated", "--builtin", "--git" };
            foreach (var flag in flags)
            {
                candidates.Add(($"{commandHead} {flag}", $"{commandHead} {flag}"));
            }
        }
        else if (suffixLower.StartsWith("install", StringComparison.OrdinalIgnoreCase)
                 || suffixLower.StartsWith("add", StringComparison.OrdinalIgnoreCase)
                 || suffixLower.Equals("i", StringComparison.OrdinalIgnoreCase)
                 || suffixLower.StartsWith("i ", StringComparison.OrdinalIgnoreCase))
        {
            var commandHead = isSlash
                ? (suffixLower.StartsWith("add", StringComparison.OrdinalIgnoreCase)
                    ? "/upm add"
                    : (suffixLower.StartsWith("install", StringComparison.OrdinalIgnoreCase)
                        ? "/upm install"
                        : "/upm i"))
                : (suffixLower.StartsWith("add", StringComparison.OrdinalIgnoreCase)
                    ? "upm add"
                    : (suffixLower.StartsWith("install", StringComparison.OrdinalIgnoreCase)
                        ? "upm install"
                        : "upm i"));

            candidates.Add(($"{commandHead} com.unity.addressables", $"{commandHead} com.unity.addressables"));
            candidates.Add(($"{commandHead} https://github.com/user/repo.git?path=/subfolder#v1.0.0", $"{commandHead} https://github.com/user/repo.git?path=/subfolder#v1.0.0"));
            candidates.Add(($"{commandHead} file:../local-pkg", $"{commandHead} file:../local-pkg"));
        }
        else if (suffixLower.StartsWith("remove", StringComparison.OrdinalIgnoreCase)
                 || suffixLower.StartsWith("rm", StringComparison.OrdinalIgnoreCase)
                 || suffixLower.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase))
        {
            var commandHead = isSlash
                ? (suffixLower.StartsWith("rm", StringComparison.OrdinalIgnoreCase)
                    ? "/upm rm"
                    : (suffixLower.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase) ? "/upm uninstall" : "/upm remove"))
                : (suffixLower.StartsWith("rm", StringComparison.OrdinalIgnoreCase)
                    ? "upm rm"
                    : (suffixLower.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase) ? "upm uninstall" : "upm remove"));
            candidates.Add(($"{commandHead} com.unity.addressables", $"{commandHead} com.unity.addressables"));
        }
        else if (suffixLower.StartsWith("update", StringComparison.OrdinalIgnoreCase)
                 || suffixLower.Equals("u", StringComparison.OrdinalIgnoreCase)
                 || suffixLower.StartsWith("u ", StringComparison.OrdinalIgnoreCase))
        {
            var commandHead = isSlash
                ? (suffixLower.StartsWith("update", StringComparison.OrdinalIgnoreCase) ? "/upm update" : "/upm u")
                : (suffixLower.StartsWith("update", StringComparison.OrdinalIgnoreCase) ? "upm update" : "upm u");
            candidates.Add(($"{commandHead}", $"{commandHead}"));
            candidates.Add(($"{commandHead} com.unity.addressables", $"{commandHead} com.unity.addressables"));
        }

        var packageRefs = session.ProjectView.LastUpmPackages.Take(5).ToList();
        if (packageRefs.Count > 0)
        {
            foreach (var package in packageRefs)
            {
                var label = $"[{package.Index}] {package.DisplayName} ({package.PackageId})";
                candidates.Add((label, null));
            }
        }

        candidates = candidates.DistinctBy(x => x.Label).Take(10).ToList();
        return true;
    }
}
