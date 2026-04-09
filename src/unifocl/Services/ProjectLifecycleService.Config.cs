using Spectre.Console;
using System.Text.Json;

internal sealed partial class ProjectLifecycleService
{
    // ── Config command handlers (extracted from HandleConfigAsync) ────────────

    private Task<bool> HandleConfigAsync(
        string input,
        CommandSpec matched,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (args.Count == 0)
        {
            LogConfigUsage(log);
            return Task.FromResult(true);
        }

        var action = args[0].Trim().ToLowerInvariant();
        return action switch
        {
            "list" => HandleConfigList(log),
            "get" => HandleConfigGet(args.Skip(1).ToList(), log),
            "set" => HandleConfigSet(args.Skip(1).ToList(), log),
            "reset" => HandleConfigReset(args.Skip(1).ToList(), log),
            _ => Task.FromResult(LogConfigUsage(log))
        };
    }

    private static Task<bool> HandleConfigList(Action<string> log)
    {
        var loadResult = TryLoadCliConfig(out var config, out var error);
        if (!loadResult)
        {
            log($"[red]error[/]: {Markup.Escape(error ?? "failed to read config")}");
            return Task.FromResult(true);
        }

        var source = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNIFOCL_THEME"))
            ? "file/default"
            : "env (UNIFOCL_THEME)";
        var theme = ResolveEffectiveTheme(config);
        var staleDays = ResolveRecentPruneStaleDays(config);
        var gitignore = ResolveGitignoreDisplayValue(config);
        log("[grey]config[/]: available settings");
        log($"[grey]config[/]: [white]theme[/] = [white]{theme}[/] [dim](dark|light, source: {source})[/]");
        log($"[grey]config[/]: [white]recent.staleDays[/] = [white]{staleDays}[/] [dim](days, default: {DefaultRecentPruneStaleDays})[/]");
        log($"[grey]config[/]: [white]setup.gitignore[/] = [white]{gitignore}[/] [dim](auto|off|prompt, default: prompt)[/]");
        return Task.FromResult(true);
    }

    private static Task<bool> HandleConfigGet(IReadOnlyList<string> args, Action<string> log)
    {
        if (args.Count != 1)
        {
            log("[red]error[/]: usage /config get <theme|recent.staleDays|setup.gitignore>");
            return Task.FromResult(true);
        }

        var isThemeKey = IsThemeKey(args[0]);
        var isRecentStaleDaysKey = IsRecentStaleDaysKey(args[0]);
        var isGitignoreKey = IsGitignoreKey(args[0]);
        if (!isThemeKey && !isRecentStaleDaysKey && !isGitignoreKey)
        {
            log("[red]error[/]: supported keys are 'theme', 'recent.staleDays', and 'setup.gitignore'");
            return Task.FromResult(true);
        }

        var loadResult = TryLoadCliConfig(out var config, out var error);
        if (!loadResult)
        {
            log($"[red]error[/]: {Markup.Escape(error ?? "failed to read config")}");
            return Task.FromResult(true);
        }

        if (isThemeKey)
        {
            var theme = ResolveEffectiveTheme(config);
            log($"[grey]config[/]: [white]theme[/] = [white]{theme}[/]");
            return Task.FromResult(true);
        }

        if (isGitignoreKey)
        {
            var gitignore = ResolveGitignoreDisplayValue(config);
            log($"[grey]config[/]: [white]setup.gitignore[/] = [white]{gitignore}[/]");
            return Task.FromResult(true);
        }

        var staleDays = ResolveRecentPruneStaleDays(config);
        log($"[grey]config[/]: [white]recent.staleDays[/] = [white]{staleDays}[/]");
        return Task.FromResult(true);
    }

    private static Task<bool> HandleConfigSet(IReadOnlyList<string> args, Action<string> log)
    {
        if (args.Count != 2)
        {
            log("[red]error[/]: usage /config set <theme|recent.staleDays|setup.gitignore> <value>");
            return Task.FromResult(true);
        }

        var key = args[0];
        if (!TryLoadCliConfig(out var config, out var loadError))
        {
            log($"[red]error[/]: {Markup.Escape(loadError ?? "failed to read config")}");
            return Task.FromResult(true);
        }

        if (IsThemeKey(key))
        {
            var requestedTheme = args[1].Trim().ToLowerInvariant();
            if (requestedTheme is not ("dark" or "light"))
            {
                log("[red]error[/]: theme must be 'dark' or 'light'");
                return Task.FromResult(true);
            }

            config.Theme = requestedTheme;
            if (!TrySaveCliConfig(config, out var saveThemeError))
            {
                log($"[red]error[/]: {Markup.Escape(saveThemeError ?? "failed to write config")}");
                return Task.FromResult(true);
            }

            CliTheme.TrySetTheme(requestedTheme);
            log($"[green]config[/]: theme set to [white]{requestedTheme}[/]");
            return Task.FromResult(true);
        }

        if (IsRecentStaleDaysKey(key))
        {
            if (!TryParseRecentPruneStaleDays(args[1], out var staleDays))
            {
                log("[red]error[/]: recent.staleDays must be a positive integer");
                return Task.FromResult(true);
            }

            config.RecentPruneStaleDays = staleDays;
            if (!TrySaveCliConfig(config, out var saveStaleDaysError))
            {
                log($"[red]error[/]: {Markup.Escape(saveStaleDaysError ?? "failed to write config")}");
                return Task.FromResult(true);
            }

            log($"[green]config[/]: recent.staleDays set to [white]{staleDays}[/] days");
            return Task.FromResult(true);
        }

        if (IsGitignoreKey(key))
        {
            if (!TryParseGitignoreValue(args[1], out var gitignoreValue))
            {
                log("[red]error[/]: setup.gitignore must be 'auto' or 'off'");
                return Task.FromResult(true);
            }

            config.GitignoreAutoIgnore = gitignoreValue;
            if (!TrySaveCliConfig(config, out var saveGitignoreError))
            {
                log($"[red]error[/]: {Markup.Escape(saveGitignoreError ?? "failed to write config")}");
                return Task.FromResult(true);
            }

            var displayValue = gitignoreValue ? "auto" : "off";
            log($"[green]config[/]: setup.gitignore set to [white]{displayValue}[/]");
            return Task.FromResult(true);
        }

        log("[red]error[/]: supported keys are 'theme', 'recent.staleDays', and 'setup.gitignore'");
        return Task.FromResult(true);
    }

    private static Task<bool> HandleConfigReset(IReadOnlyList<string> args, Action<string> log)
    {
        if (args.Count > 1)
        {
            log("[red]error[/]: usage /config reset <theme|recent.staleDays|setup.gitignore?>");
            return Task.FromResult(true);
        }

        var resetTheme = args.Count == 0 || IsThemeKey(args[0]);
        var resetRecentStaleDays = args.Count == 0 || IsRecentStaleDaysKey(args[0]);
        var resetGitignore = args.Count == 0 || IsGitignoreKey(args[0]);
        if (!resetTheme && !resetRecentStaleDays && !resetGitignore)
        {
            log("[red]error[/]: supported keys are 'theme', 'recent.staleDays', and 'setup.gitignore'");
            return Task.FromResult(true);
        }

        if (!TryLoadCliConfig(out var config, out var loadError))
        {
            log($"[red]error[/]: {Markup.Escape(loadError ?? "failed to read config")}");
            return Task.FromResult(true);
        }

        if (resetTheme)
        {
            config.Theme = null;
        }

        if (resetRecentStaleDays)
        {
            config.RecentPruneStaleDays = null;
        }

        if (resetGitignore)
        {
            config.GitignoreAutoIgnore = null;
        }

        if (!TrySaveCliConfig(config, out var saveError))
        {
            log($"[red]error[/]: {Markup.Escape(saveError ?? "failed to write config")}");
            return Task.FromResult(true);
        }

        var effective = ResolveEffectiveTheme(config);
        CliTheme.TrySetTheme(effective);

        var resetParts = new List<string>();
        if (resetTheme) resetParts.Add($"theme={effective}");
        if (resetRecentStaleDays) resetParts.Add($"recent.staleDays={DefaultRecentPruneStaleDays}");
        if (resetGitignore) resetParts.Add("setup.gitignore=prompt");
        log($"[green]config[/]: reset to defaults ({string.Join(", ", resetParts)})");
        return Task.FromResult(true);
    }

    private static bool LogConfigUsage(Action<string> log)
    {
        log("[red]error[/]: usage /config <get|set|list|reset> <theme|recent.staleDays|setup.gitignore?> <value?>");
        return true;
    }

    // ── Config persistence helpers ───────────────────────────────────────────

    private static bool TryLoadCliConfig(out CliConfig config, out string? error)
    {
        config = new CliConfig();
        error = null;

        try
        {
            var path = GetCliConfigPath();
            if (!File.Exists(path))
            {
                return true;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "invalid config format";
                return false;
            }

            if (document.RootElement.TryGetProperty("theme", out var themeProperty)
                && themeProperty.ValueKind == JsonValueKind.String)
            {
                config.Theme = themeProperty.GetString();
            }

            if (document.RootElement.TryGetProperty("unityProjectPath", out var unityProjectPathProperty)
                && unityProjectPathProperty.ValueKind == JsonValueKind.String)
            {
                var configuredPath = unityProjectPathProperty.GetString();
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    config.UnityProjectPath = Path.GetFullPath(configuredPath);
                }
            }

            if (document.RootElement.TryGetProperty("recentStaleDays", out var recentStaleDaysProperty))
            {
                if (recentStaleDaysProperty.ValueKind == JsonValueKind.Number
                    && recentStaleDaysProperty.TryGetInt32(out var staleDaysFromNumber))
                {
                    config.RecentPruneStaleDays = staleDaysFromNumber;
                }
                else if (recentStaleDaysProperty.ValueKind == JsonValueKind.String
                         && TryParseRecentPruneStaleDays(recentStaleDaysProperty.GetString() ?? string.Empty, out var staleDaysFromString))
                {
                    config.RecentPruneStaleDays = staleDaysFromString;
                }
            }

            if (document.RootElement.TryGetProperty("gitignoreAutoIgnore", out var gitignoreProp)
                && gitignoreProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                config.GitignoreAutoIgnore = gitignoreProp.GetBoolean();
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to read config ({ex.Message})";
            return false;
        }
    }

    private static bool TrySaveCliConfig(CliConfig config, out string? error)
    {
        error = null;
        try
        {
            var path = GetCliConfigPath();
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                error = "failed to resolve config directory";
                return false;
            }

            Directory.CreateDirectory(directory);
            var payload = JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["theme"] = NormalizeTheme(config.Theme),
                    ["unityProjectPath"] = NormalizeProjectPath(config.UnityProjectPath),
                    ["recentStaleDays"] = config.RecentPruneStaleDays,
                    ["gitignoreAutoIgnore"] = config.GitignoreAutoIgnore
                },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, payload + Environment.NewLine);
            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to write config ({ex.Message})";
            return false;
        }
    }

    private static string ResolveEffectiveTheme(CliConfig config)
    {
        var fromEnv = Environment.GetEnvironmentVariable("UNIFOCL_THEME");
        if (NormalizeTheme(fromEnv) is string envTheme)
        {
            return envTheme;
        }

        return NormalizeTheme(config.Theme) ?? "dark";
    }

    private static int ResolveRecentPruneStaleDays(CliConfig config)
    {
        if (config.RecentPruneStaleDays is int configured && configured > 0)
        {
            return configured;
        }

        return DefaultRecentPruneStaleDays;
    }

    private static bool IsThemeKey(string key)
    {
        return key.Equals("theme", StringComparison.OrdinalIgnoreCase)
               || key.Equals("ui.theme", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecentStaleDaysKey(string key)
    {
        return key.Equals("recent.staledays", StringComparison.OrdinalIgnoreCase)
               || key.Equals("recent.stale-days", StringComparison.OrdinalIgnoreCase)
               || key.Equals("recent.prunedays", StringComparison.OrdinalIgnoreCase)
               || key.Equals("recent.prune-days", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGitignoreKey(string key)
    {
        return key.Equals("setup.gitignore", StringComparison.OrdinalIgnoreCase)
               || key.Equals("gitignore", StringComparison.OrdinalIgnoreCase)
               || key.Equals("gitignore.auto", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses user input for <c>setup.gitignore</c>: "auto"/"true" → <c>true</c>, "off"/"false" → <c>false</c>.
    /// Returns <c>false</c> if the value is not recognised.
    /// </summary>
    private static bool TryParseGitignoreValue(string raw, out bool result)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "auto" or "true":
                result = true;
                return true;
            case "off" or "false":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static string ResolveGitignoreDisplayValue(CliConfig config)
    {
        return config.GitignoreAutoIgnore switch
        {
            true => "auto",
            false => "off",
            null => "prompt"
        };
    }

    private static bool TryParseRecentPruneStaleDays(string raw, out int staleDays)
    {
        staleDays = 0;
        if (!int.TryParse(raw.Trim(), out var parsed))
        {
            return false;
        }

        if (parsed <= 0)
        {
            return false;
        }

        staleDays = parsed;
        return true;
    }

    private static string? NormalizeTheme(string? theme)
    {
        if (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase))
        {
            return "dark";
        }

        if (string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase))
        {
            return "light";
        }

        return null;
    }

    private static string? NormalizeProjectPath(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return null;
        }

        return Path.GetFullPath(projectPath);
    }

    private static bool TrySaveLastUnityProjectPath(string projectPath, out string? error)
    {
        error = null;
        if (!TryLoadCliConfig(out var config, out var loadError))
        {
            error = loadError ?? "failed to read config";
            return false;
        }

        config.UnityProjectPath = NormalizeProjectPath(projectPath);
        if (!TrySaveCliConfig(config, out var saveError))
        {
            error = saveError ?? "failed to write config";
            return false;
        }

        return true;
    }

    private static string GetCliConfigPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("UNIFOCL_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var configRoot = Environment.GetEnvironmentVariable("UNIFOCL_CONFIG_ROOT");
        if (!string.IsNullOrWhiteSpace(configRoot))
        {
            return Path.Combine(Path.GetFullPath(configRoot), "config.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".unifocl", "config.json");
    }
}
