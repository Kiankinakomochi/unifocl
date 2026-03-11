using System.Text;
using System.Text.Json;

internal static class ProjectViewServiceUtils
{
    public static bool TryParseRemoveIndexRange(
        string selector,
        out int startIndex,
        out int endIndex,
        out string? error)
    {
        startIndex = 0;
        endIndex = 0;
        error = null;
        var trimmed = selector.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator < 0)
        {
            return false;
        }

        var startRaw = trimmed[..separator];
        var endRaw = separator + 1 < trimmed.Length ? trimmed[(separator + 1)..] : string.Empty;
        if (!int.TryParse(startRaw, out startIndex) || !int.TryParse(endRaw, out endIndex))
        {
            error = "range must be numeric: <start:end>";
            return true;
        }

        if (startIndex < 0 || endIndex < 0)
        {
            error = "range indices must be non-negative";
            return true;
        }

        if (startIndex > endIndex)
        {
            error = "range start must be <= end";
            return true;
        }

        return true;
    }

    public static bool ShouldRecoverUpmTimeout(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("daemon project command timed out", StringComparison.OrdinalIgnoreCase)
               && message.Contains("daemonPing=ok", StringComparison.OrdinalIgnoreCase)
               || DidDaemonRuntimeRestart(message);
    }

    public static bool DidDaemonRuntimeRestart(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("daemon runtime restarted during project command", StringComparison.OrdinalIgnoreCase);
    }

    public static bool DidResponseChannelInterruptAfterCompletion(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("completed successfully but response channel was interrupted", StringComparison.OrdinalIgnoreCase);
    }

    public static ProjectTreeEntry? FindEntryBySelector(ProjectViewState state, string selector)
    {
        var normalizedSelector = NormalizeLoadSelector(selector);
        if (int.TryParse(normalizedSelector, out var index))
        {
            var visible = state.VisibleEntries.FirstOrDefault(entry => entry.Index == index);
            if (visible is not null)
            {
                return visible;
            }

            var fuzzy = state.LastFuzzyMatches.FirstOrDefault(entry => entry.Index == index);
            if (fuzzy is not null)
            {
                var name = Path.GetFileName(fuzzy.Path);
                return new ProjectTreeEntry(fuzzy.Index, 0, name, fuzzy.Path, false);
            }

            return null;
        }

        var normalized = normalizedSelector.Replace('\\', '/');
        return state.VisibleEntries.FirstOrDefault(entry =>
            entry.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || entry.RelativePath.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || entry.RelativePath.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeLoadSelector(string selector)
    {
        var normalized = selector.Trim();
        if (normalized.Length >= 2
            && ((normalized.StartsWith('<') && normalized.EndsWith('>'))
                || (normalized.StartsWith('"') && normalized.EndsWith('"'))
                || (normalized.StartsWith('\'') && normalized.EndsWith('\''))))
        {
            normalized = normalized[1..^1].Trim();
        }

        return normalized;
    }

    public static string ComputeRenameDestinationPath(string sourceRelativePath, bool isDirectory, string newName)
    {
        var parentRelative = Path.GetDirectoryName(sourceRelativePath)?.Replace('\\', '/') ?? string.Empty;
        var sourceName = Path.GetFileName(sourceRelativePath);
        var finalName = isDirectory
            ? newName
            : (Path.HasExtension(newName) ? newName : $"{newName}{Path.GetExtension(sourceName)}");
        return string.IsNullOrEmpty(parentRelative) ? finalName : $"{parentRelative}/{finalName}";
    }

    public static bool IsAssetNotFoundFailure(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
               && message.Contains("asset not found", StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolvePackageDisplayName(string? displayName, string packageId)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName.Trim();
        }

        var tail = packageId.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? packageId;
        return string.Join(' ', tail
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Length == 0
                ? token
                : char.ToUpperInvariant(token[0]) + token[1..]));
    }

    public static string ResolveUpmStatusColor(UpmPackageEntry package)
    {
        if (package.IsDeprecated)
        {
            return CliTheme.Error;
        }

        if (package.IsOutdated)
        {
            return CliTheme.Warning;
        }

        if (package.IsPreview)
        {
            return CliTheme.Info;
        }

        return CliTheme.Success;
    }

    public static string ResolveUpmStatusLabel(UpmPackageEntry package)
    {
        if (package.IsDeprecated)
        {
            return "deprecated";
        }

        if (package.IsOutdated)
        {
            return "update available";
        }

        if (package.IsPreview)
        {
            return "preview";
        }

        return "stable";
    }

    public static bool TryNormalizeUpmInstallTarget(
        string rawTarget,
        out string normalizedTarget,
        out string targetType,
        out string error)
    {
        normalizedTarget = NormalizeLoadSelector(rawTarget);
        targetType = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            error = "missing target";
            return false;
        }

        if (IsRegistryPackageId(normalizedTarget))
        {
            targetType = "registry";
            return true;
        }

        if (IsGitPackageUrl(normalizedTarget))
        {
            targetType = "git";
            return true;
        }

        if (IsLocalFilePackagePath(normalizedTarget))
        {
            targetType = "file";
            return true;
        }

        error = "target must be registry ID, Git URL, or file: path";
        return false;
    }

    public static bool IsRegistryPackageId(string value)
    {
        var (packageId, version) = SplitRegistryTarget(value);
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        if (version is not null && string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var segments = packageId.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return false;
            }

            if (!char.IsLetterOrDigit(segment[0]))
            {
                return false;
            }

            foreach (var ch in segment)
            {
                if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static (string PackageId, string? Version) SplitRegistryTarget(string value)
    {
        var at = value.IndexOf('@');
        if (at < 0)
        {
            return (value, null);
        }

        var packageId = value[..at];
        var version = at + 1 < value.Length ? value[(at + 1)..] : string.Empty;
        return (packageId, version);
    }

    public static string ComposeRegistryInstallTarget(string packageId, string? latestCompatibleVersion)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return packageId;
        }

        if (string.IsNullOrWhiteSpace(latestCompatibleVersion))
        {
            return packageId;
        }

        return $"{packageId}@{latestCompatibleVersion}";
    }

    public static bool IsGitPackageUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var isHttp = uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                     || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!isHttp)
        {
            return false;
        }

        var withoutQuery = value.Split('?', '#')[0];
        return withoutQuery.EndsWith(".git", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLocalFilePackagePath(string value)
    {
        if (!value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pathPart = value["file:".Length..].Trim();
        return !string.IsNullOrWhiteSpace(pathPart);
    }

    public static UpmListPackagePayload? TryFindUpmPackageById(string? rawContent, string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        var packages = TryParseUpmPackages(rawContent);
        return packages?.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.PackageId)
            && p.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
    }

    public static List<UpmListPackagePayload>? TryParseUpmPackages(string? rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<UpmListResponsePayload>(
                rawContent,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed?.Packages;
        }
        catch
        {
            return null;
        }
    }

    public static string ResolveUpmUpdatedVersion(string? installResponseContent, string? fallbackLatestCompatibleVersion)
    {
        if (!string.IsNullOrWhiteSpace(installResponseContent))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<UpmInstallResponsePayload>(
                    installResponseContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (!string.IsNullOrWhiteSpace(parsed?.Version))
                {
                    return parsed.Version!;
                }
            }
            catch
            {
            }
        }

        return string.IsNullOrWhiteSpace(fallbackLatestCompatibleVersion)
            ? "unknown"
            : fallbackLatestCompatibleVersion!;
    }

    public static bool IsHierarchyAssetExtension(string extension)
    {
        return extension.Equals(".unity", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveLoadAssetKind(string extension)
    {
        if (extension.Equals(".unity", StringComparison.OrdinalIgnoreCase))
        {
            return "scene";
        }

        if (extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            return "prefab";
        }

        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return "script";
        }

        return "asset";
    }

    public static (string TemplateName, string TemplateSource, string Content) ResolveTemplate(string projectPath)
    {
        var templatesJsonPath = Path.Combine(projectPath, "templates.json");
        if (File.Exists(templatesJsonPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(templatesJsonPath));
                if (document.RootElement.TryGetProperty("templates", out var templates)
                    && templates.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "CustomScript", "script" })
                    {
                        if (templates.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                        {
                            var templateRelative = value.GetString();
                            if (string.IsNullOrWhiteSpace(templateRelative))
                            {
                                continue;
                            }

                            var templatePath = ResolveAbsolutePath(projectPath, templateRelative);
                            if (File.Exists(templatePath))
                            {
                                return (key, "templates.json", File.ReadAllText(templatePath));
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        var defaultTemplate =
$"using UnityEngine;{Environment.NewLine}{Environment.NewLine}public class #NAME# : MonoBehaviour{Environment.NewLine}{{{Environment.NewLine}    private void Start(){{ }}{Environment.NewLine}{Environment.NewLine}    private void Update(){{ }}{Environment.NewLine}}}{Environment.NewLine}";
        return ("ProjectDefault", "default template", defaultTemplate);
    }

    public static string ResolveAbsolutePath(string projectPath, string relativeOrAbsolute)
    {
        if (Path.IsPathRooted(relativeOrAbsolute))
        {
            return relativeOrAbsolute;
        }

        return Path.GetFullPath(Path.Combine(projectPath, relativeOrAbsolute));
    }

    public static string CombineRelative(string parent, string child)
    {
        if (string.IsNullOrWhiteSpace(parent))
        {
            return child.Replace('\\', '/');
        }

        return $"{parent.TrimEnd('/', '\\')}/{child}".Replace('\\', '/');
    }

    public static string SanitizeTypeName(string raw)
    {
        var builder = new StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                builder.Append(ch);
            }
        }

        var value = builder.ToString();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (!char.IsLetter(value[0]) && value[0] != '_')
        {
            value = "_" + value;
        }

        return value;
    }

    public static string ResolveScriptCreateName(string canonicalType, string? baseName, int index, int count)
    {
        var defaultBase = canonicalType.Equals("ScriptableObjectScript", StringComparison.OrdinalIgnoreCase)
            ? "NewScriptableObject"
            : "NewScript";
        var resolvedBase = string.IsNullOrWhiteSpace(baseName) ? defaultBase : baseName.Trim();
        return count > 1 ? $"{resolvedBase}_{index + 1}" : resolvedBase;
    }

    public static string ResolveUniqueScriptTypeName(string projectPath, string parentPath, string baseTypeName)
    {
        var candidate = baseTypeName;
        var suffix = 1;
        while (true)
        {
            var candidateRelative = CombineRelative(parentPath, $"{candidate}.cs");
            var candidateAbsolute = ResolveAbsolutePath(projectPath, candidateRelative);
            if (!File.Exists(candidateAbsolute))
            {
                return candidate;
            }

            candidate = $"{baseTypeName}_{suffix++}";
        }
    }

    public static string BuildScriptableObjectTemplate(string typeName)
    {
        return
$"using UnityEngine;{Environment.NewLine}{Environment.NewLine}[CreateAssetMenu(fileName = \"{typeName}\", menuName = \"ScriptableObjects/{typeName}\")]{Environment.NewLine}public class {typeName} : ScriptableObject{Environment.NewLine}{{{Environment.NewLine}}}{Environment.NewLine}";
    }

    public static List<string> ParseMkAssetCreatedPaths(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<MkAssetResponsePayload>(
                content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed?.CreatedPaths is null || parsed.CreatedPaths.Count == 0)
            {
                return [];
            }

            return parsed.CreatedPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static (string? TypeFilter, string Query) ParseProjectQuery(string query)
        => ProjectMkCatalog.ParseFuzzyQuery(query);

    public static bool PassesTypeFilter(string path, string? typeFilter)
        => ProjectMkCatalog.PassesFuzzyTypeFilter(path, typeFilter);

    public static string FormatProjectCommandFailure(string action, string? message)
    {
        var (category, hint) = ClassifyProjectBridgeFailure(message);
        var details = string.IsNullOrWhiteSpace(message) ? "unknown error" : message;
        return string.IsNullOrWhiteSpace(hint)
            ? $"[x] {action} failed ({category}): {details}"
            : $"[x] {action} failed ({category}): {details} [grey]{hint}[/]";
    }

    public static (string Category, string Hint) ClassifyProjectBridgeFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return ("bridge runtime error", "Command is implemented; inspect bridge logs for details.");
        }

        if (message.StartsWith(ProjectDaemonBridge.StubbedBridgePrefix, StringComparison.Ordinal))
        {
            return ("stubbed bridge", "This daemon path is not implemented; run with Bridge mode attached.");
        }

        if (message.Contains("daemon did not return", StringComparison.OrdinalIgnoreCase)
            || message.Contains("daemon returned", StringComparison.OrdinalIgnoreCase)
            || message.Contains("daemon is not attached", StringComparison.OrdinalIgnoreCase))
        {
            return ("bridge transport error", "Bridge connection failed; ensure daemon/editor bridge is running and attached.");
        }

        return ("bridge runtime error", "Command is implemented; bridge returned an operational error.");
    }

    public static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
