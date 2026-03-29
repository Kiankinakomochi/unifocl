using Spectre.Console;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed partial class ProjectLifecycleService
{
    private static HttpClient CreateGitHubReleasesHttpClient()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("unifocl-cli-updater");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return httpClient;
    }

    private static bool TryResolveCurrentPlatformUpdateSpec(out PlatformUpdateSpec spec, out string error)
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && architecture == Architecture.Arm64)
        {
            spec = new PlatformUpdateSpec(
                "macOS arm64",
                "-macos-arm64.tar.gz",
                "unifocl",
                ReleaseArchiveType.TarGz);
            error = string.Empty;
            return true;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && architecture == Architecture.X64)
        {
            spec = new PlatformUpdateSpec(
                "Windows x64",
                "-win-x64.zip",
                "unifocl.exe",
                ReleaseArchiveType.Zip);
            error = string.Empty;
            return true;
        }

        spec = new PlatformUpdateSpec(string.Empty, string.Empty, string.Empty, ReleaseArchiveType.Zip);
        error = $"unsupported update target: os={RuntimeInformation.OSDescription}, arch={architecture}";
        return false;
    }

    private static async Task<ReleaseFetchResult> TryFetchLatestGitHubReleaseAsync()
    {
        try
        {
            var endpoint = $"https://api.github.com/repos/{GitHubReleaseOwner}/{GitHubReleaseRepository}/releases/latest";
            using var response = await GitHubReleasesHttpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return ReleaseFetchResult.Fail($"{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var payload = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ReleaseFetchResult.Fail("invalid release payload");
            }

            if (!root.TryGetProperty("tag_name", out var tagNameElement)
                || tagNameElement.ValueKind != JsonValueKind.String)
            {
                return ReleaseFetchResult.Fail("release payload missing tag_name");
            }

            var tagName = tagNameElement.GetString();
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return ReleaseFetchResult.Fail("release tag_name is empty");
            }

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var assetsElement)
                && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var assetElement in assetsElement.EnumerateArray())
                {
                    if (assetElement.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!assetElement.TryGetProperty("name", out var nameElement)
                        || nameElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    if (!assetElement.TryGetProperty("browser_download_url", out var urlElement)
                        || urlElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var name = nameElement.GetString();
                    var url = urlElement.GetString();
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                    {
                        assets.Add(new ReleaseAsset(name, url));
                    }
                }
            }

            return ReleaseFetchResult.Success(new ReleaseInfo(tagName!, assets));
        }
        catch (Exception ex)
        {
            return ReleaseFetchResult.Fail(ex.Message);
        }
    }

    private static bool TryParseComparableSemVer(string value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var match = Regex.Match(normalized, @"^(?<core>\d+\.\d+\.\d+)", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        if (!Version.TryParse(match.Groups["core"].Value, out var parsed) || parsed is null)
        {
            return false;
        }

        version = parsed;
        return true;
    }

    private static async Task<OperationResult> DownloadReleaseAssetAsync(string downloadUrl, string destinationPath)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(CliUpdateDownloadTimeout);
            using var response = await GitHubReleasesHttpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);
            response.EnsureSuccessStatusCode();
            await using var sourceStream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            await using var destinationStream = File.Create(destinationPath);
            await sourceStream.CopyToAsync(destinationStream, timeoutCts.Token);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    private static async Task<OperationResult> ExtractReleaseArchiveAsync(
        string archivePath,
        string extractDirectory,
        ReleaseArchiveType archiveType)
    {
        try
        {
            if (archiveType == ReleaseArchiveType.Zip)
            {
                ZipFile.ExtractToDirectory(archivePath, extractDirectory, overwriteFiles: true);
                return OperationResult.Success();
            }

            await ExtractTarGzArchiveAsync(archivePath, extractDirectory);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    private static async Task ExtractTarGzArchiveAsync(string archivePath, string extractDirectory)
    {
        var extractRoot = Path.GetFullPath(extractDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        await using var archiveStream = File.OpenRead(archivePath);
        await using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);

        TarEntry? entry;
        while ((entry = tarReader.GetNextEntry()) is not null)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.DataStream is null)
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(extractDirectory, entry.Name));
            if (!destinationPath.StartsWith(extractRoot, StringComparison.Ordinal))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var output = File.Create(destinationPath);
            await entry.DataStream.CopyToAsync(output);
        }
    }

    private static string? FindExtractedExecutablePath(string extractDirectory, string executableName)
    {
        if (!Directory.Exists(extractDirectory))
        {
            return null;
        }

        return Directory.EnumerateFiles(extractDirectory, executableName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string StageDownloadedExecutableForManualInstall(
        string sourcePath,
        string releaseVersion,
        string executableName,
        string targetDirectory)
    {
        var extension = Path.GetExtension(executableName);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(executableName);
        var stagedFileName = $"{fileNameWithoutExtension}-{releaseVersion}{extension}";
        var stagedPath = Path.Combine(targetDirectory, stagedFileName);
        File.Copy(sourcePath, stagedPath, overwrite: true);
        TryApplyUnixExecutableMode(stagedPath);
        return stagedPath;
    }

    /// <summary>
    /// If winget is available, print the managed upgrade command and return true (skip manual download).
    /// </summary>
    private static async Task<bool> TrySpawnWingetUpgradeAsync(Action<string> log)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var psi = new ProcessStartInfo("winget", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var probe = Process.Start(psi);
            if (probe is null)
            {
                return false;
            }

            await probe.WaitForExitAsync(cts.Token);
            if (probe.ExitCode != 0)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        log($"[green]update[/]: winget detected — run the following to upgrade");
        log($"[white]  winget upgrade {Markup.Escape(WingetPackageId)}[/]");
        log("[grey]update[/]: winget will handle the binary lock and installs the latest release cleanly");
        return true;
    }

    /// <summary>
    /// Spawns a hidden PowerShell script that waits for this process to exit, then swaps
    /// <paramref name="stagedPath"/> into <paramref name="processPath"/> automatically.
    /// Returns true if the script was launched successfully.
    /// </summary>
    private static bool TrySpawnDeferredWindowsSwap(string stagedPath, string processPath, Action<string> log)
    {
        try
        {
            var currentPid = Environment.ProcessId;
            var escapedSource = stagedPath.Replace("'", "''");
            var escapedTarget = processPath.Replace("'", "''");

            var script = $$"""
$source = '{{escapedSource}}'
$target = '{{escapedTarget}}'
try { Wait-Process -Id {{currentPid}} -ErrorAction SilentlyContinue } catch {}
Start-Sleep -Seconds 1
try {
    Copy-Item -Path $source -Destination $target -Force
} catch {
    exit 1
}
""";
            var scriptPath = Path.Combine(Path.GetTempPath(), $"unifocl-swap-{currentPid}.ps1");
            File.WriteAllText(scriptPath, script, System.Text.Encoding.UTF8);

            var psi = new ProcessStartInfo("powershell.exe",
                $"-ExecutionPolicy Bypass -WindowStyle Hidden -NonInteractive -File \"{scriptPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var _ = Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            log($"[yellow]update[/]: could not spawn deferred swap ({Markup.Escape(ex.Message)})");
            return false;
        }
    }

    private static void TryApplyUnixExecutableMode(string executablePath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            var mode =
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(executablePath, mode);
        }
        catch
        {
            // best effort only
        }
    }
}
