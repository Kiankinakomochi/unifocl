using Spectre.Console;
using System.Text.Json;

internal static class CliTheme
{
    private const string BrandColor = "#ffb300";
    private const string CursorBackgroundColor = "#ffb300";
    private const string CursorForegroundColor = "#000000";

    private static string _currentTheme = ResolveInitialTheme();

    public static string Brand => BrandColor;
    public static string CursorBackground => CursorBackgroundColor;
    public static string CursorForeground => CursorForegroundColor;

    public static string BaseBackground => IsLightMode ? "#fafafa" : "#18181b";
    public static string SurfaceBackground => IsLightMode ? "#f4f4f5" : "#27272a";
    public static string TextPrimary => IsLightMode ? "#18181b" : "#e4e4e7";
    public static string TextSecondary => IsLightMode ? "#52525b" : "#a1a1aa";
    public static string TextMuted => IsLightMode ? "#a1a1aa" : "#52525b";
    public static string Success => IsLightMode ? "#16a34a" : "#4ade80";
    public static string Info => IsLightMode ? "#2563eb" : "#60a5fa";
    public static string Warning => IsLightMode ? "#ea580c" : "#fb923c";
    public static string Error => IsLightMode ? "#dc2626" : "#f87171";
    public static string CurrentTheme => _currentTheme;

    public static string ApplyMarkupPalette(string markup)
    {
        if (string.IsNullOrEmpty(markup))
        {
            return markup;
        }

        return markup
            .Replace("[bold deepskyblue1]", $"[bold {Brand}]")
            .Replace("[deepskyblue1]", $"[{Brand}]")
            .Replace("[bold white]", $"[bold {TextPrimary}]")
            .Replace("[white]", $"[{TextPrimary}]")
            .Replace("[bold green]", $"[bold {Success}]")
            .Replace("[green]", $"[{Success}]")
            .Replace("[yellow]", $"[{Warning}]")
            .Replace("[red]", $"[{Error}]")
            .Replace("[grey]", $"[{TextSecondary}]")
            .Replace("[dim]", $"[{TextMuted}]");
    }

    public static void MarkupLine(string markupLine)
    {
        var themed = ApplyMarkupPalette(markupLine);
        try
        {
            AnsiConsole.MarkupLine(themed);
        }
        catch (InvalidOperationException)
        {
            // Fallback to plain output if invalid Spectre markup slips through.
            AnsiConsole.MarkupLine(Spectre.Console.Markup.Escape(themed));
        }
    }

    public static void Markup(string markup)
    {
        var themed = ApplyMarkupPalette(markup);
        try
        {
            AnsiConsole.Markup(themed);
        }
        catch (InvalidOperationException)
        {
            AnsiConsole.Markup(Spectre.Console.Markup.Escape(themed));
        }
    }

    public static string CursorWrapEscaped(string escapedContent)
    {
        return $"[{CursorForeground} on {CursorBackground}]{escapedContent}[/]";
    }

    public static bool TrySetTheme(string theme)
    {
        if (!IsSupportedTheme(theme))
        {
            return false;
        }

        _currentTheme = theme.ToLowerInvariant();
        return true;
    }

    private static bool IsLightMode => string.Equals(_currentTheme, "light", StringComparison.OrdinalIgnoreCase);

    private static string ResolveInitialTheme()
    {
        var theme = Environment.GetEnvironmentVariable("UNIFOCL_THEME");
        if (IsSupportedTheme(theme))
        {
            return theme!.ToLowerInvariant();
        }

        var configTheme = TryReadThemeFromConfig();
        if (IsSupportedTheme(configTheme))
        {
            return configTheme!.ToLowerInvariant();
        }

        return "dark";
    }

    private static bool IsSupportedTheme(string? theme)
    {
        return string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)
               || string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadThemeFromConfig()
    {
        try
        {
            var configPath = ResolveConfigPath();
            if (string.IsNullOrWhiteSpace(configPath))
            {
                return null;
            }

            if (!File.Exists(configPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!document.RootElement.TryGetProperty("theme", out var themeProperty))
            {
                return null;
            }

            return themeProperty.ValueKind == JsonValueKind.String
                ? themeProperty.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveConfigPath()
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
        if (string.IsNullOrWhiteSpace(home))
        {
            return null;
        }

        return Path.Combine(home, ".unifocl", "config.json");
    }
}
