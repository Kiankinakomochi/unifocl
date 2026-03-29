using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

internal sealed partial class ProjectViewService
{
    private static async Task<Dictionary<string, ExternalLatestInfo>> FetchExternalLatestVersionsAsync(
        string projectPath,
        IReadOnlyList<PackagesLockEntry> directEntries)
    {
        var results = new Dictionary<string, ExternalLatestInfo>(StringComparer.OrdinalIgnoreCase);
        if (directEntries.Count == 0)
        {
            return results;
        }

        var scopedRegistries = LoadScopedRegistries(projectPath);
        var manifestDependencies = ReadManifestDependencyMap(projectPath);
        var tasks = directEntries.Select(async entry =>
        {
            if (entry.Source.Equals("Registry", StringComparison.OrdinalIgnoreCase))
            {
                var registryUrl = ResolveRegistryUrlForPackage(entry.PackageId, scopedRegistries);
                var fetched = await TryFetchRegistryLatestVersionAsync(entry.PackageId, registryUrl);
                if (!fetched.Ok || string.IsNullOrWhiteSpace(fetched.LatestVersion))
                {
                    return new ExternalLatestInfo(entry.PackageId, null, false, fetched.Error);
                }

                var cmp = CompareSemVer(entry.Version, fetched.LatestVersion!);
                return new ExternalLatestInfo(entry.PackageId, fetched.LatestVersion, cmp < 0, fetched.Error);
            }

            if (entry.Source.Equals("Git", StringComparison.OrdinalIgnoreCase))
            {
                if (!manifestDependencies.TryGetValue(entry.PackageId, out var manifestTarget)
                    || string.IsNullOrWhiteSpace(manifestTarget))
                {
                    return new ExternalLatestInfo(entry.PackageId, null, false, "manifest git target not found");
                }

                var gitUrl = NormalizeGitTargetForRemote(manifestTarget);
                if (string.IsNullOrWhiteSpace(gitUrl))
                {
                    return new ExternalLatestInfo(entry.PackageId, null, false, "invalid git target");
                }

                var gitLatest = await TryFetchGitLatestTagAsync(gitUrl!);
                if (!gitLatest.Ok || string.IsNullOrWhiteSpace(gitLatest.Tag))
                {
                    return new ExternalLatestInfo(entry.PackageId, null, false, gitLatest.Error);
                }

                var isOutdated = !string.IsNullOrWhiteSpace(entry.Hash)
                                 && !string.IsNullOrWhiteSpace(gitLatest.Hash)
                                 && !entry.Hash.Equals(gitLatest.Hash, StringComparison.OrdinalIgnoreCase);
                return new ExternalLatestInfo(entry.PackageId, gitLatest.Tag, isOutdated, gitLatest.Error);
            }

            return new ExternalLatestInfo(entry.PackageId, null, false, null);
        }).ToArray();

        var resolved = await Task.WhenAll(tasks);
        foreach (var item in resolved)
        {
            results[item.PackageId] = item;
        }

        return results;
    }

    private static Dictionary<string, string> ReadManifestDependencyMap(string projectPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryLoadManifest(projectPath, out _, out _, out var dependencies, out _))
        {
            return map;
        }

        foreach (var dependency in dependencies)
        {
            if (dependency.Value is not System.Text.Json.Nodes.JsonValue valueNode)
            {
                continue;
            }

            if (!valueNode.TryGetValue<string>(out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            map[dependency.Key] = rawValue;
        }

        return map;
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
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("scopedRegistries", out var scopedRegistries)
                || scopedRegistries.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var resolved = new List<ScopedRegistryConfig>();
            foreach (var item in scopedRegistries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!item.TryGetProperty("url", out var urlElement)
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
                if (item.TryGetProperty("scopes", out var scopesElement) && scopesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var scope in scopesElement.EnumerateArray())
                    {
                        if (scope.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var value = scope.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            scopes.Add(value!);
                        }
                    }
                }

                if (scopes.Count > 0)
                {
                    resolved.Add(new ScopedRegistryConfig(url!, scopes));
                }
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
        var bestScopeLength = -1;
        var resolvedUrl = "https://packages.unity.com";
        foreach (var scopedRegistry in scopedRegistries)
        {
            foreach (var scope in scopedRegistry.Scopes)
            {
                var matches = packageId.Equals(scope, StringComparison.OrdinalIgnoreCase)
                              || packageId.StartsWith(scope + ".", StringComparison.OrdinalIgnoreCase);
                if (!matches || scope.Length <= bestScopeLength)
                {
                    continue;
                }

                bestScopeLength = scope.Length;
                resolvedUrl = scopedRegistry.Url;
            }
        }

        return resolvedUrl;
    }

    private static async Task<RegistryLatestResult> TryFetchRegistryLatestVersionAsync(string packageId, string registryUrl)
    {
        try
        {
            var endpoint = $"{registryUrl.TrimEnd('/')}/{Uri.EscapeDataString(packageId)}";
            using var response = await UpmRegistryHttpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return RegistryLatestResult.Fail($"registry lookup failed ({response.StatusCode}) for {packageId} at {registryUrl}");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("dist-tags", out var distTags)
                || distTags.ValueKind != JsonValueKind.Object
                || !distTags.TryGetProperty("latest", out var latest)
                || latest.ValueKind != JsonValueKind.String)
            {
                return RegistryLatestResult.Fail($"registry metadata is missing dist-tags.latest for {packageId}");
            }

            var latestVersion = latest.GetString();
            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                return RegistryLatestResult.Fail($"registry latest version is empty for {packageId}");
            }

            return RegistryLatestResult.Success(latestVersion!);
        }
        catch (Exception ex)
        {
            return RegistryLatestResult.Fail($"registry lookup exception for {packageId}: {ex.Message}");
        }
    }

    private static async Task<string?> TryFetchRegistryDisplayNameAsync(string packageId, string registryUrl)
    {
        try
        {
            var endpoint = $"{registryUrl.TrimEnd('/')}/{Uri.EscapeDataString(packageId)}";
            using var response = await UpmRegistryHttpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("displayName", out var rootDisplayName)
                && rootDisplayName.ValueKind == JsonValueKind.String)
            {
                var value = rootDisplayName.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            if (doc.RootElement.TryGetProperty("dist-tags", out var distTags)
                && distTags.ValueKind == JsonValueKind.Object
                && distTags.TryGetProperty("latest", out var latestTag)
                && latestTag.ValueKind == JsonValueKind.String
                && doc.RootElement.TryGetProperty("versions", out var versions)
                && versions.ValueKind == JsonValueKind.Object)
            {
                var latestVersion = latestTag.GetString();
                if (!string.IsNullOrWhiteSpace(latestVersion)
                    && versions.TryGetProperty(latestVersion, out var latestVersionNode)
                    && latestVersionNode.ValueKind == JsonValueKind.Object
                    && latestVersionNode.TryGetProperty("displayName", out var versionDisplayName)
                    && versionDisplayName.ValueKind == JsonValueKind.String)
                {
                    var value = versionDisplayName.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TrySearchRegistryPackageIdByFriendlyNameAsync(
        string registryUrl,
        string rawFriendlyName,
        string normalizedFriendly)
    {
        try
        {
            var endpoint = $"{registryUrl.TrimEnd('/')}/-/v1/search?text={Uri.EscapeDataString(rawFriendlyName)}&size=64";
            using var response = await UpmRegistryHttpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("objects", out var objects)
                || objects.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? partialMatch = null;
            foreach (var item in objects.EnumerateArray())
            {
                if (!item.TryGetProperty("package", out var package)
                    || package.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var packageName = TryGetString(package, "name");
                if (string.IsNullOrWhiteSpace(packageName))
                {
                    continue;
                }

                var displayName = TryGetString(package, "displayName");
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    var normalizedDisplayName = NormalizeFriendlyToken(displayName);
                    if (normalizedDisplayName.Equals(normalizedFriendly, StringComparison.Ordinal))
                    {
                        return packageName;
                    }

                    if (partialMatch is null && normalizedDisplayName.Contains(normalizedFriendly, StringComparison.Ordinal))
                    {
                        partialMatch = packageName;
                    }
                }

                var normalizedPackageName = NormalizeFriendlyToken(packageName);
                if (normalizedPackageName.Equals(normalizedFriendly, StringComparison.Ordinal))
                {
                    return packageName;
                }

                if (partialMatch is null && normalizedPackageName.Contains(normalizedFriendly, StringComparison.Ordinal))
                {
                    partialMatch = packageName;
                }
            }

            return partialMatch;
        }
        catch
        {
            return null;
        }
    }

    private static (string FriendlyName, string Version) SplitFriendlyNameAndVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (string.Empty, string.Empty);
        }

        var at = value.LastIndexOf('@');
        if (at <= 0 || at >= value.Length - 1)
        {
            return (value.Trim(), string.Empty);
        }

        var name = value[..at].Trim();
        var version = value[(at + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
        {
            return (value.Trim(), string.Empty);
        }

        return (name, version);
    }

    private static string NormalizeFriendlyToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .Trim()
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant);
        return new string(chars.ToArray());
    }

    private static async Task<RegistryDependencyResult> TryFetchRegistryDependenciesForVersionAsync(
        string packageId,
        string version,
        string registryUrl)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
        {
            return RegistryDependencyResult.Fail("package id/version is missing");
        }

        try
        {
            var endpoint = $"{registryUrl.TrimEnd('/')}/{Uri.EscapeDataString(packageId)}";
            using var response = await UpmRegistryHttpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return RegistryDependencyResult.Fail(
                    $"registry dependency lookup failed ({response.StatusCode}) for {packageId}@{version}");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("versions", out var versions)
                || versions.ValueKind != JsonValueKind.Object
                || !versions.TryGetProperty(version, out var selectedVersion)
                || selectedVersion.ValueKind != JsonValueKind.Object)
            {
                return RegistryDependencyResult.Success([]);
            }

            if (!selectedVersion.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object)
            {
                return RegistryDependencyResult.Success([]);
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in dependencies.EnumerateObject())
            {
                if (dependency.Value.ValueKind == JsonValueKind.String)
                {
                    map[dependency.Name] = dependency.Value.GetString() ?? string.Empty;
                }
            }

            return RegistryDependencyResult.Success(map);
        }
        catch (Exception ex)
        {
            return RegistryDependencyResult.Fail(
                $"registry dependency lookup exception for {packageId}@{version}: {ex.Message}");
        }
    }

    private static async Task<RegistryVersionExistsResult> TryFetchRegistryVersionExistsAsync(
        string packageId,
        string version,
        string registryUrl)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
        {
            return RegistryVersionExistsResult.Fail("package id/version is missing");
        }

        try
        {
            var endpoint = $"{registryUrl.TrimEnd('/')}/{Uri.EscapeDataString(packageId)}";
            using var response = await UpmRegistryHttpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return RegistryVersionExistsResult.Fail(
                    $"registry lookup failed ({response.StatusCode}) for {packageId} at {registryUrl}");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("versions", out var versions)
                || versions.ValueKind != JsonValueKind.Object)
            {
            return RegistryVersionExistsResult.Success(exists: false);
            }

            return RegistryVersionExistsResult.Success(versions.TryGetProperty(version, out _));
        }
        catch (Exception ex)
        {
            return RegistryVersionExistsResult.Fail(
                $"registry version lookup exception for {packageId}@{version}: {ex.Message}");
        }
    }

    private static bool TryReadLocalPackageDependencies(
        string projectPath,
        string manifestTarget,
        out Dictionary<string, string> dependencies,
        out string error)
    {
        dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(manifestTarget)
            || !manifestTarget.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            error = "manifest target is not a file package";
            return false;
        }

        var relativePath = manifestTarget["file:".Length..].Trim();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "file package path is empty";
            return false;
        }

        var absolutePath = Path.GetFullPath(
            Path.Combine(projectPath, "Packages", relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var packageJsonPath = Path.Combine(absolutePath, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            error = $"package.json not found at {absolutePath}";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (!document.RootElement.TryGetProperty("dependencies", out var depsElement)
                || depsElement.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            foreach (var dependency in depsElement.EnumerateObject())
            {
                if (dependency.Value.ValueKind == JsonValueKind.String)
                {
                    dependencies[dependency.Name] = dependency.Value.GetString() ?? string.Empty;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to parse local package.json: {ex.Message}";
            return false;
        }
    }

    private static string? NormalizeGitTargetForRemote(string manifestTarget)
    {
        if (string.IsNullOrWhiteSpace(manifestTarget))
        {
            return null;
        }

        var normalized = manifestTarget.Trim();
        if (normalized.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["git+".Length..];
        }

        var fragmentIndex = normalized.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            normalized = normalized[..fragmentIndex];
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static async Task<GitLatestResult> TryFetchGitLatestTagAsync(string gitUrl)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("ls-remote");
            process.StartInfo.ArgumentList.Add("--tags");
            process.StartInfo.ArgumentList.Add("--refs");
            process.StartInfo.ArgumentList.Add(gitUrl);

            if (!process.Start())
            {
                return GitLatestResult.Fail($"failed to start git for {gitUrl}");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await process.WaitForExitAsync(timeout.Token);
            var stdOut = await process.StandardOutput.ReadToEndAsync();
            var stdErr = await process.StandardError.ReadToEndAsync();
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stdErr) ? $"exit={process.ExitCode}" : stdErr.Trim();
                return GitLatestResult.Fail($"git ls-remote failed for {gitUrl}: {detail}");
            }

            var tags = stdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line =>
                {
                    var columns = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (columns.Length != 2 || !columns[1].StartsWith("refs/tags/", StringComparison.Ordinal))
                    {
                        return null;
                    }

                    var hash = columns[0];
                    var tag = columns[1]["refs/tags/".Length..];
                    return string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(hash)
                        ? null
                        : new GitTagRef(tag, hash);
                })
                .Where(tag => tag is not null)
                .Select(tag => tag!)
                .ToList();
            if (tags.Count == 0)
            {
                return GitLatestResult.Fail($"no tags found in {gitUrl}");
            }

            var latest = tags
                .OrderByDescending(tag => tag.Tag, SemVerStringComparer.Instance)
                .First();
            return GitLatestResult.Success(latest.Tag, latest.Hash);
        }
        catch (OperationCanceledException)
        {
            return GitLatestResult.Fail($"git ls-remote timed out for {gitUrl}");
        }
        catch (Exception ex)
        {
            return GitLatestResult.Fail($"git ls-remote exception for {gitUrl}: {ex.Message}");
        }
    }

    private static int CompareSemVer(string? left, string? right)
        => SemVerStringComparer.Instance.Compare(left ?? string.Empty, right ?? string.Empty);

    private sealed record GitTagRef(string Tag, string Hash);

    private sealed class SemVerStringComparer : IComparer<string>
    {
        public static readonly SemVerStringComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var left = Parse(x);
            var right = Parse(y);
            return left.CompareTo(right);
        }

        private static ParsedSemVer Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new ParsedSemVer([0, 0, 0], []);
            }

            var trimmed = raw.Trim();
            if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            {
                trimmed = trimmed[1..];
            }

            var plus = trimmed.IndexOf('+');
            if (plus >= 0)
            {
                trimmed = trimmed[..plus];
            }

            var dash = trimmed.IndexOf('-');
            var core = dash >= 0 ? trimmed[..dash] : trimmed;
            var pre = dash >= 0 && dash + 1 < trimmed.Length ? trimmed[(dash + 1)..] : string.Empty;

            var numbers = core
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Take(3)
                .Select(segment => int.TryParse(segment, out var value) ? value : 0)
                .ToList();
            while (numbers.Count < 3)
            {
                numbers.Add(0);
            }

            var prerelease = string.IsNullOrWhiteSpace(pre)
                ? []
                : pre.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            return new ParsedSemVer(numbers, prerelease);
        }

        private sealed record ParsedSemVer(List<int> Core, List<string> Prerelease) : IComparable<ParsedSemVer>
        {
            public int CompareTo(ParsedSemVer? other)
            {
                if (other is null)
                {
                    return 1;
                }

                for (var i = 0; i < 3; i++)
                {
                    var cmp = Core[i].CompareTo(other.Core[i]);
                    if (cmp != 0)
                    {
                        return cmp;
                    }
                }

                var leftHasPre = Prerelease.Count > 0;
                var rightHasPre = other.Prerelease.Count > 0;
                if (!leftHasPre && !rightHasPre)
                {
                    return 0;
                }

                if (!leftHasPre)
                {
                    return 1;
                }

                if (!rightHasPre)
                {
                    return -1;
                }

                var max = Math.Max(Prerelease.Count, other.Prerelease.Count);
                for (var i = 0; i < max; i++)
                {
                    if (i >= Prerelease.Count)
                    {
                        return -1;
                    }

                    if (i >= other.Prerelease.Count)
                    {
                        return 1;
                    }

                    var left = Prerelease[i];
                    var right = other.Prerelease[i];
                    var leftIsInt = int.TryParse(left, out var leftInt);
                    var rightIsInt = int.TryParse(right, out var rightInt);
                    if (leftIsInt && rightIsInt)
                    {
                        var cmp = leftInt.CompareTo(rightInt);
                        if (cmp != 0)
                        {
                            return cmp;
                        }

                        continue;
                    }

                    if (leftIsInt && !rightIsInt)
                    {
                        return -1;
                    }

                    if (!leftIsInt && rightIsInt)
                    {
                        return 1;
                    }

                    var lexical = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
                    if (lexical != 0)
                    {
                        return lexical;
                    }
                }

                return 0;
            }
        }
    }
}
