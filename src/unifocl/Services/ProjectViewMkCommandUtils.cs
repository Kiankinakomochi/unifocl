internal static class ProjectViewMkCommandUtils
{
    public static bool TryResolveMkParentPath(
        CliSessionState session,
        string? parentSelector,
        out string parentPath,
        out string error)
    {
        parentPath = "Assets";
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(parentSelector))
        {
            return true;
        }

        var selector = parentSelector.Trim();
        if (selector.Equals("Assets", StringComparison.OrdinalIgnoreCase)
            || selector.Equals("/", StringComparison.OrdinalIgnoreCase)
            || selector.Equals("root", StringComparison.OrdinalIgnoreCase))
        {
            parentPath = "Assets";
            return true;
        }

        var state = session.ProjectView;
        var entry = ProjectViewServiceUtils.FindEntryBySelector(state, selector);
        if (entry is not null)
        {
            if (!entry.IsDirectory)
            {
                error = $"--parent target is not a folder: {selector}";
                return false;
            }

            parentPath = entry.RelativePath;
            return true;
        }

        var normalized = selector.Replace('\\', '/').Trim('/');
        var relative = normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"Assets/{normalized}";
        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            error = $"parent folder not found: {selector}";
            return false;
        }

        var absolute = ProjectViewServiceUtils.ResolveAbsolutePath(session.CurrentProjectPath!, relative);
        if (!Directory.Exists(absolute))
        {
            error = $"parent folder not found: {selector}";
            return false;
        }

        parentPath = relative;
        return true;
    }

    public static bool TryParseProjectMkArguments(
        IReadOnlyList<string> tokens,
        out string mkType,
        out int count,
        out string? name,
        out string? parent,
        out string error)
    {
        mkType = string.Empty;
        count = 1;
        name = null;
        parent = null;
        error = "usage: make --type <type> [--count <count>] [--name <name>|-n <name>] [--parent <idx|name>] | mk <type> [count] [--name <name>|-n <name>] [--parent <idx|name>]  (quote --parent paths with spaces: --parent \"Assets/My Folder\")";
        if (tokens.Count == 0)
        {
            return false;
        }

        var isMake = tokens[0].Equals("make", StringComparison.OrdinalIgnoreCase)
                     || (tokens[0].Equals("mk", StringComparison.OrdinalIgnoreCase)
                         && tokens.Count >= 2
                         && (tokens[1].StartsWith("--type", StringComparison.OrdinalIgnoreCase)
                             || tokens[1].StartsWith("-t", StringComparison.OrdinalIgnoreCase)));
        if (isMake)
        {
            if (tokens.Count < 3)
            {
                return false;
            }

            for (var i = 1; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Equals("--type", StringComparison.OrdinalIgnoreCase) || token.Equals("-t", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= tokens.Count)
                    {
                        error = "usage: make --type <type> [--count <count>] [--name <name>|-n <name>] [--parent <idx|name>]";
                        return false;
                    }

                    mkType = tokens[++i];
                    continue;
                }

                if (token.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))
                {
                    mkType = token["--type=".Length..];
                    continue;
                }

                if (token.Equals("--count", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= tokens.Count || !int.TryParse(tokens[++i], out count) || count <= 0)
                    {
                        error = "count must be a positive integer";
                        return false;
                    }

                    continue;
                }

                if (token.StartsWith("--count=", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = token["--count=".Length..];
                    if (!int.TryParse(raw, out count) || count <= 0)
                    {
                        error = "count must be a positive integer";
                        return false;
                    }

                    continue;
                }

                if (token.StartsWith("--name=", StringComparison.OrdinalIgnoreCase))
                {
                    name = token["--name=".Length..].Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        error = "name must not be empty";
                        return false;
                    }

                    continue;
                }

                if (token.StartsWith("-n=", StringComparison.OrdinalIgnoreCase))
                {
                    name = token["-n=".Length..].Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        error = "name must not be empty";
                        return false;
                    }

                    continue;
                }

                if (token.Equals("--name", StringComparison.OrdinalIgnoreCase) || token.Equals("-n", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= tokens.Count)
                    {
                        error = "usage: make --type <type> [--count <count>] [--name <name>|-n <name>] [--parent <idx|name>]";
                        return false;
                    }

                    name = tokens[++i].Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        error = "name must not be empty";
                        return false;
                    }

                    continue;
                }

                if (token.StartsWith("--parent=", StringComparison.OrdinalIgnoreCase))
                {
                    parent = token["--parent=".Length..].Trim();
                    if (string.IsNullOrWhiteSpace(parent))
                    {
                        error = "parent must not be empty";
                        return false;
                    }

                    continue;
                }

                if (token.Equals("--parent", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= tokens.Count)
                    {
                        error = "usage: make --type <type> [--count <count>] [--name <name>|-n <name>] [--parent <idx|name>]";
                        return false;
                    }

                    parent = tokens[++i].Trim();
                    if (string.IsNullOrWhiteSpace(parent))
                    {
                        error = "parent must not be empty";
                        return false;
                    }

                    continue;
                }

                error = $"unsupported option: {token}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(mkType))
            {
                error = "missing --type <type>";
                return false;
            }

            return true;
        }

        if (tokens.Count < 2)
        {
            error = "usage: mk <type> [count] [--name <name>|-n <name>] [--parent <idx|name>]";
            return false;
        }

        mkType = tokens[1];
        var countSpecified = false;
        for (var i = 2; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.StartsWith("--count=", StringComparison.OrdinalIgnoreCase))
            {
                var raw = token["--count=".Length..];
                if (!int.TryParse(raw, out count) || count <= 0)
                {
                    error = "count must be a positive integer";
                    return false;
                }

                countSpecified = true;
                continue;
            }

            if (token.Equals("--count", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= tokens.Count || !int.TryParse(tokens[++i], out count) || count <= 0)
                {
                    error = "count must be a positive integer";
                    return false;
                }

                countSpecified = true;
                continue;
            }

            if (token.StartsWith("--name=", StringComparison.OrdinalIgnoreCase))
            {
                name = token["--name=".Length..].Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    error = "name must not be empty";
                    return false;
                }

                continue;
            }

            if (token.StartsWith("-n=", StringComparison.OrdinalIgnoreCase))
            {
                name = token["-n=".Length..].Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    error = "name must not be empty";
                    return false;
                }

                continue;
            }

            if (token.Equals("--name", StringComparison.OrdinalIgnoreCase) || token.Equals("-n", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= tokens.Count)
                {
                    error = "usage: mk <type> [count] [--name <name>|-n <name>] [--parent <idx|name>]";
                    return false;
                }

                name = tokens[++i].Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    error = "name must not be empty";
                    return false;
                }

                continue;
            }

            if (token.StartsWith("--parent=", StringComparison.OrdinalIgnoreCase))
            {
                parent = token["--parent=".Length..].Trim();
                if (string.IsNullOrWhiteSpace(parent))
                {
                    error = "parent must not be empty";
                    return false;
                }

                continue;
            }

            if (token.Equals("--parent", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= tokens.Count)
                {
                    error = "usage: mk <type> [count] [--name <name>|-n <name>] [--parent <idx|name>]";
                    return false;
                }

                parent = tokens[++i].Trim();
                if (string.IsNullOrWhiteSpace(parent))
                {
                    error = "parent must not be empty";
                    return false;
                }

                continue;
            }

            if (!countSpecified && int.TryParse(token, out var parsedCount) && parsedCount > 0)
            {
                count = parsedCount;
                countSpecified = true;
                continue;
            }

            error = $"unsupported mk argument: {token}";
            return false;
        }

        return true;
    }
}
