internal sealed partial class HierarchyTui
{
    private static bool TryParseMakeArguments(
        IReadOnlyList<string> tokens,
        out string type,
        out int count,
        out string error)
    {
        type = string.Empty;
        count = 1;
        error = "usage: make --type <type> [--count <count>]";
        if (tokens.Count < 3)
        {
            return false;
        }

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Equals("--type", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= tokens.Count)
                {
                    error = "usage: make --type <type> [--count <count>]";
                    return false;
                }

                type = tokens[++i];
                continue;
            }

            if (token.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))
            {
                type = token["--type=".Length..];
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

            error = $"unsupported option: {token}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            error = "missing --type <type>";
            return false;
        }

        return true;
    }

    private static bool TryParseMkArguments(
        IReadOnlyList<string> tokens,
        out string type,
        out int count,
        out string? name,
        out string? parentSelector,
        out string error)
    {
        type = string.Empty;
        count = 1;
        name = null;
        parentSelector = null;
        error = "usage: mk <type> [count] [--name <name>|-n <name>] [--parent <path|id>|-p <path|id>]";
        if (tokens.Count < 2)
        {
            return false;
        }

        type = tokens[1];
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
                    error = "usage: mk <type> [count] [--name <name>|-n <name>] [--parent <path|id>|-p <path|id>]";
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
                parentSelector = token["--parent=".Length..].Trim();
                if (string.IsNullOrWhiteSpace(parentSelector))
                {
                    error = "parent target must not be empty";
                    return false;
                }

                continue;
            }

            if (token.StartsWith("-p=", StringComparison.OrdinalIgnoreCase))
            {
                parentSelector = token["-p=".Length..].Trim();
                if (string.IsNullOrWhiteSpace(parentSelector))
                {
                    error = "parent target must not be empty";
                    return false;
                }

                continue;
            }

            if (token.Equals("--parent", StringComparison.OrdinalIgnoreCase) || token.Equals("-p", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= tokens.Count)
                {
                    error = "usage: mk <type> [count] [--name <name>|-n <name>] [--parent <path|id>|-p <path|id>]";
                    return false;
                }

                parentSelector = tokens[++i].Trim();
                if (string.IsNullOrWhiteSpace(parentSelector))
                {
                    error = "parent target must not be empty";
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

    private static Dictionary<string, string> BuildMkTypeLookup()
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string canonical, params string[] aliases)
        {
            lookup[NormalizeMkTypeKey(canonical)] = canonical;
            foreach (var alias in aliases)
            {
                lookup[NormalizeMkTypeKey(alias)] = canonical;
            }
        }

        // UI
        Add("Canvas");
        Add("Panel");
        Add("Text");
        Add("Tmp", "TMP");
        Add("Image");
        Add("Button");
        Add("Toggle");
        Add("Slider");
        Add("Scrollbar");
        Add("ScrollView");
        Add("EventSystem");

        // 3D Primitives
        Add("Cube");
        Add("Sphere");
        Add("Capsule");
        Add("Cylinder");
        Add("Plane");
        Add("Quad");

        // Lights
        Add("DirLight");
        Add("DirectionalLight");
        Add("PointLight");
        Add("SpotLight");
        Add("AreaLight");
        Add("ReflectionProbe");

        // 2D
        Add("Sprite");
        Add("SpriteMask");

        // Misc
        Add("Camera");
        Add("AudioSource");
        Add("Empty");
        Add("EmptyParent");
        Add("EmptyChild");

        return lookup;
    }

    private static bool TryNormalizeMkType(string raw, out string canonical, out bool catalogResolved, out string error)
    {
        canonical = string.Empty;
        catalogResolved = false;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "mk type is required";
            return false;
        }

        var key = NormalizeMkTypeKey(raw);
        if (MkTypeLookup.TryGetValue(key, out var resolved))
        {
            canonical = resolved;
            catalogResolved = true;
            return true;
        }

        // Pass through to daemon for TypeCache resolution
        canonical = raw.Trim();
        return true;
    }

    private static string NormalizeMkTypeKey(string raw)
    {
        return raw.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }
}
