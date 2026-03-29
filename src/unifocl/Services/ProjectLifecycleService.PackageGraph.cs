using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed partial class ProjectLifecycleService
{
    private static async Task<OperationResult> EnsureRequiredUnityPackageReferencesAsync(string projectPath)
    {
        try
        {
            var manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return OperationResult.Fail("Packages/manifest.json is missing");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return OperationResult.Fail("manifest.json has invalid format");
            }

            var root = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                root[property.Name] = JsonSerializer.Deserialize<object>(property.Value.GetRawText());
            }

            var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("dependencies", out var depsElement)
                && depsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var dep in depsElement.EnumerateObject())
                {
                    if (dep.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var version = dep.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        dependencies[dep.Name] = version;
                    }
                }
            }

            var requiredPackages = InferRequiredUnityPackages(projectPath);

            var scopedRegistries = LoadScopedRegistries(projectPath);
            var resolvedRequiredPackages = await ResolveRequiredPackageGraphAsync(requiredPackages, scopedRegistries);

            var changed = false;

            foreach (var required in resolvedRequiredPackages)
            {
                if (dependencies.ContainsKey(required.Key))
                {
                    continue;
                }

                dependencies[required.Key] = required.Value;
                changed = true;
            }

            if (!changed)
            {
                return OperationResult.Success();
            }

            root["dependencies"] = dependencies
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.Ordinal);
            var updated = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(manifestPath, updated + Environment.NewLine);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to update required Unity package references ({ex.Message})");
        }
    }

    private static async Task<(bool Ok, Dictionary<string, string> Dependencies, string Error)> ProbeRequiredMcpTransitiveDependenciesAsync(
        IReadOnlyList<ScopedRegistryConfig> scopedRegistries)
    {
        var dependencies = await TryFetchPackageDependenciesAsync(
            RequiredMcpPackageId,
            RequiredMcpPackageTarget,
            scopedRegistries);
        if (dependencies.Count > 0)
        {
            return (true, dependencies, string.Empty);
        }

        var diagnostics = await DiagnoseMcpDependencyResolutionFailureAsync(scopedRegistries);
        if (!string.IsNullOrWhiteSpace(diagnostics))
        {
            return (false, new Dictionary<string, string>(StringComparer.Ordinal), diagnostics);
        }

        return (false, new Dictionary<string, string>(StringComparer.Ordinal),
            "failed to resolve transitive dependencies for required package com.coplaydev.unity-mcp; package metadata returned no dependencies");
    }

    private static async Task<string?> DiagnoseMcpDependencyResolutionFailureAsync(IReadOnlyList<ScopedRegistryConfig> scopedRegistries)
    {
        try
        {
            if (TryBuildGitHubRawPackageJsonUrl(RequiredMcpPackageTarget, out var packageJsonUrl))
            {
                using var gitResponse = await UnityRegistryHttpClient.GetAsync(packageJsonUrl);
                if (!gitResponse.IsSuccessStatusCode)
                {
                    return $"failed to resolve transitive dependencies for required package {RequiredMcpPackageId} (git package metadata lookup returned {(int)gitResponse.StatusCode})";
                }
            }
        }
        catch (Exception ex) when (LooksLikePermissionOrNetworkRestriction(ex.Message))
        {
            return $"failed to resolve transitive dependencies for required package {RequiredMcpPackageId} ({ex.Message}); rerun with elevated permissions";
        }
        catch (Exception ex)
        {
            return $"failed to resolve transitive dependencies for required package {RequiredMcpPackageId} ({ex.Message})";
        }

        try
        {
            var registryUrl = ResolveRegistryUrlForPackage(RequiredMcpPackageId, scopedRegistries);
            var endpoint = $"{registryUrl.TrimEnd('/')}/{Uri.EscapeDataString(RequiredMcpPackageId)}";
            using var registryResponse = await UnityRegistryHttpClient.GetAsync(endpoint);
            if (!registryResponse.IsSuccessStatusCode)
            {
                return $"failed to resolve transitive dependencies for required package {RequiredMcpPackageId} (registry lookup returned {(int)registryResponse.StatusCode})";
            }
        }
        catch (Exception ex) when (LooksLikePermissionOrNetworkRestriction(ex.Message))
        {
            return $"failed to resolve transitive dependencies for required package {RequiredMcpPackageId} ({ex.Message}); rerun with elevated permissions";
        }
        catch (Exception ex)
        {
            return $"failed to resolve transitive dependencies for required package {RequiredMcpPackageId} ({ex.Message})";
        }

        return null;
    }

    private static bool LooksLikePermissionOrNetworkRestriction(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.ToLowerInvariant();
        return normalized.Contains("access to the path")
               || normalized.Contains("operation not permitted")
               || normalized.Contains("permission denied")
               || normalized.Contains("could not resolve host")
               || normalized.Contains("nodename nor servname provided")
               || normalized.Contains("name or service not known")
               || normalized.Contains("temporary failure in name resolution")
               || normalized.Contains("network is unreachable")
               || normalized.Contains("sandbox");
    }

    private static OperationResult SyncInstalledMcpPackageJsonDependencies(
        string projectPath,
        IReadOnlyDictionary<string, string> transitiveDependencies)
    {
        if (transitiveDependencies.Count == 0)
        {
            return OperationResult.Success();
        }

        try
        {
            var packageJsonPaths = new List<string>();
            var directPackageJson = Path.Combine(projectPath, "Packages", RequiredMcpPackageId, "package.json");
            if (File.Exists(directPackageJson))
            {
                packageJsonPaths.Add(directPackageJson);
            }

            var packageCacheRoot = Path.Combine(projectPath, "Library", "PackageCache");
            if (Directory.Exists(packageCacheRoot))
            {
                foreach (var cacheDir in Directory.EnumerateDirectories(packageCacheRoot, $"{RequiredMcpPackageId}@*"))
                {
                    var packageJsonPath = Path.Combine(cacheDir, "package.json");
                    if (File.Exists(packageJsonPath))
                    {
                        packageJsonPaths.Add(packageJsonPath);
                    }
                }
            }

            foreach (var packageJsonPath in packageJsonPaths.Distinct(StringComparer.Ordinal))
            {
                var syncResult = MergeDependenciesIntoPackageJson(packageJsonPath, transitiveDependencies);
                if (!syncResult.Ok)
                {
                    return syncResult;
                }
            }

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to sync local MCP package.json dependencies ({ex.Message})");
        }
    }

    private static OperationResult MergeDependenciesIntoPackageJson(
        string packageJsonPath,
        IReadOnlyDictionary<string, string> dependenciesToMerge)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return OperationResult.Fail($"failed to sync local MCP package.json dependencies (invalid JSON root: {packageJsonPath})");
            }

            var root = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                root[property.Name] = JsonSerializer.Deserialize<object>(property.Value.GetRawText());
            }

            var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("dependencies", out var depsElement)
                && depsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var dep in depsElement.EnumerateObject())
                {
                    if (dep.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var value = dep.Value.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        dependencies[dep.Name] = value!;
                    }
                }
            }

            var changed = false;
            foreach (var dependency in dependenciesToMerge)
            {
                if (dependencies.ContainsKey(dependency.Key))
                {
                    continue;
                }

                dependencies[dependency.Key] = dependency.Value;
                changed = true;
            }

            if (!changed)
            {
                return OperationResult.Success();
            }

            root["dependencies"] = dependencies
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => (object?)item.Value, StringComparer.Ordinal);
            var updated = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(packageJsonPath, updated + Environment.NewLine);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to sync local MCP package.json dependencies ({ex.Message})");
        }
    }

    private static async Task<Dictionary<string, string>> ResolveRequiredPackageGraphAsync(
        Dictionary<string, string> seedPackages,
        IReadOnlyList<ScopedRegistryConfig> scopedRegistries)
    {
        var resolved = new Dictionary<string, string>(seedPackages, StringComparer.Ordinal);
        var queue = new Queue<KeyValuePair<string, string>>(seedPackages);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current.Key))
            {
                continue;
            }

            var dependencies = await TryFetchPackageDependenciesAsync(current.Key, current.Value, scopedRegistries);
            foreach (var dependency in dependencies)
            {
                if (!IsRegistryPackageId(dependency.Key))
                {
                    continue;
                }

                var value = dependency.Value?.Trim() ?? string.Empty;
                if (!IsLikelyRegistryVersionSpec(value))
                {
                    continue;
                }

                if (!resolved.TryGetValue(dependency.Key, out var existing))
                {
                    resolved[dependency.Key] = value;
                    queue.Enqueue(new KeyValuePair<string, string>(dependency.Key, value));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(existing) && !string.IsNullOrWhiteSpace(value))
                {
                    resolved[dependency.Key] = value;
                }
            }
        }

        return resolved;
    }

    private static async Task<Dictionary<string, string>> TryFetchPackageDependenciesAsync(
        string packageId,
        string packageTarget,
        IReadOnlyList<ScopedRegistryConfig> scopedRegistries)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(packageTarget))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (packageTarget.Contains(".git", StringComparison.OrdinalIgnoreCase)
            || packageTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || packageTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var gitDependencies = await TryFetchGitPackageDependenciesAsync(packageTarget);
            var registryDependencies = await TryFetchRegistryPackageDependenciesAsync(
                packageId,
                versionSpec: "latest",
                scopedRegistries);
            if (gitDependencies.Count == 0)
            {
                return registryDependencies;
            }

            foreach (var dependency in registryDependencies)
            {
                if (!gitDependencies.ContainsKey(dependency.Key))
                {
                    gitDependencies[dependency.Key] = dependency.Value;
                }
            }

            return gitDependencies;
        }

        return await TryFetchRegistryPackageDependenciesAsync(packageId, packageTarget, scopedRegistries);
    }

    private static async Task<Dictionary<string, string>> TryFetchRegistryPackageDependenciesAsync(
        string packageId,
        string versionSpec,
        IReadOnlyList<ScopedRegistryConfig> scopedRegistries)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var registryUrl = ResolveRegistryUrlForPackage(packageId, scopedRegistries);
            var endpoint = $"{registryUrl.TrimEnd('/')}/{Uri.EscapeDataString(packageId)}";
            using var response = await UnityRegistryHttpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return map;
            }

            var raw = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("versions", out var versionsElement)
                || versionsElement.ValueKind != JsonValueKind.Object)
            {
                return map;
            }

            var selectedVersion = versionSpec.Trim();
            if (!versionsElement.TryGetProperty(selectedVersion, out var packageNode)
                || packageNode.ValueKind != JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("dist-tags", out var tagsElement)
                    && tagsElement.ValueKind == JsonValueKind.Object
                    && tagsElement.TryGetProperty("latest", out var latestElement)
                    && latestElement.ValueKind == JsonValueKind.String)
                {
                    var latest = latestElement.GetString();
                    if (!string.IsNullOrWhiteSpace(latest)
                        && versionsElement.TryGetProperty(latest, out var latestNode)
                        && latestNode.ValueKind == JsonValueKind.Object)
                    {
                        packageNode = latestNode;
                    }
                }
            }

            if (packageNode.ValueKind != JsonValueKind.Object
                || !packageNode.TryGetProperty("dependencies", out var dependenciesElement)
                || dependenciesElement.ValueKind != JsonValueKind.Object)
            {
                return map;
            }

            foreach (var dependency in dependenciesElement.EnumerateObject())
            {
                if (dependency.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = dependency.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    map[dependency.Name] = value!;
                }
            }

            return map;
        }
        catch
        {
            return map;
        }
    }

    private static async Task<Dictionary<string, string>> TryFetchGitPackageDependenciesAsync(string gitTarget)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryBuildGitHubRawPackageJsonUrl(gitTarget, out var packageJsonUrl))
        {
            return map;
        }

        try
        {
            using var response = await UnityRegistryHttpClient.GetAsync(packageJsonUrl);
            if (!response.IsSuccessStatusCode)
            {
                return map;
            }

            var raw = await response.Content.ReadAsStringAsync();
            return TryReadDependenciesFromPackageJson(raw);
        }
        catch
        {
            return map;
        }
    }

    private static bool TryBuildGitHubRawPackageJsonUrl(string gitTarget, out string packageJsonUrl)
    {
        packageJsonUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(gitTarget))
        {
            return false;
        }

        var normalized = gitTarget.Trim();
        if (normalized.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["git+".Length..];
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || !uri.Host.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fragment = uri.Fragment.TrimStart('#');
        var reference = string.IsNullOrWhiteSpace(fragment) ? "main" : fragment;

        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        var owner = segments[0];
        var repository = segments[1];

        var packagePath = "package.json";
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !kv[0].Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var decodedPath = Uri.UnescapeDataString(kv[1]).Trim('/');
            packagePath = string.IsNullOrWhiteSpace(decodedPath)
                ? "package.json"
                : $"{decodedPath}/package.json";
            break;
        }

        packageJsonUrl = $"https://raw.githubusercontent.com/{owner}/{repository}/{reference}/{packagePath}";
        return true;
    }

    private static Dictionary<string, string> TryReadDependenciesFromPackageJson(string rawPackageJson)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(rawPackageJson))
        {
            return map;
        }

        try
        {
            using var document = JsonDocument.Parse(rawPackageJson);
            if (!document.RootElement.TryGetProperty("dependencies", out var dependenciesElement)
                || dependenciesElement.ValueKind != JsonValueKind.Object)
            {
                return map;
            }

            foreach (var dependency in dependenciesElement.EnumerateObject())
            {
                if (dependency.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = dependency.Value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    map[dependency.Name] = value!;
                }
            }

            return map;
        }
        catch
        {
            return map;
        }
    }

    private static bool IsLikelyRegistryVersionSpec(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return !value.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
               && !value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               && !value.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRegistryPackageId(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        var segments = packageId.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        return segments.All(segment => segment.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'));
    }

    private static Dictionary<string, string> InferRequiredUnityPackages(string projectPath)
    {
        var namespaceToPackage = new Dictionary<string, (string PackageId, string Version)>(StringComparer.Ordinal)
        {
            ["UnityEngine.UI"] = ("com.unity.ugui", "1.0.0"),
            ["UnityEngine.EventSystems"] = ("com.unity.ugui", "1.0.0"),
            ["TMPro"] = ("com.unity.textmeshpro", "3.0.6")
        };
        var required = new Dictionary<string, string>(StringComparer.Ordinal);
        var usingPattern = new Regex(@"^\s*using\s+([A-Za-z0-9_.]+)\s*;", RegexOptions.Compiled);
        var assetsPath = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(assetsPath))
        {
            return required;
        }

        foreach (var scriptPath in Directory.EnumerateFiles(assetsPath, "*.cs", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadLines(scriptPath))
            {
                var match = usingPattern.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var ns = match.Groups[1].Value;
                if (!namespaceToPackage.TryGetValue(ns, out var package))
                {
                    continue;
                }

                required[package.PackageId] = package.Version;
            }
        }

        return required;
    }

    private static bool ManifestContainsPackage(string projectPath, string packageId)
    {
        try
        {
            var manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            var manifest = File.ReadAllText(manifestPath);
            return manifest.Contains($"\"{packageId}\"", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static List<ScopedRegistryConfig> LoadScopedRegistries(string projectPath)
    {
        var manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("scopedRegistries", out var scopedRegistriesElement)
                || scopedRegistriesElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var resolved = new List<ScopedRegistryConfig>();
            foreach (var registryElement in scopedRegistriesElement.EnumerateArray())
            {
                if (registryElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!registryElement.TryGetProperty("url", out var urlElement)
                    || urlElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var url = urlElement.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var scopes = new List<string>();
                if (registryElement.TryGetProperty("scopes", out var scopesElement)
                    && scopesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var scopeElement in scopesElement.EnumerateArray())
                    {
                        if (scopeElement.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var scope = scopeElement.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(scope))
                        {
                            scopes.Add(scope);
                        }
                    }
                }

                resolved.Add(new ScopedRegistryConfig(url, scopes));
            }

            return resolved;
        }
        catch
        {
            return [];
        }
    }

    private static string ResolveRegistryUrlForPackage(string packageId, IReadOnlyList<ScopedRegistryConfig> scopedRegistries)
    {
        var resolvedUrl = "https://packages.unity.com";
        var bestScopeLength = -1;
        foreach (var scopedRegistry in scopedRegistries)
        {
            foreach (var scope in scopedRegistry.Scopes)
            {
                if (!packageId.StartsWith($"{scope}.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (scope.Length <= bestScopeLength)
                {
                    continue;
                }

                bestScopeLength = scope.Length;
                resolvedUrl = scopedRegistry.Url;
            }
        }

        return resolvedUrl;
    }
}
