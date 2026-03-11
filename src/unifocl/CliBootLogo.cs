using Spectre.Console;

internal static class CliBootLogo
{
    private const int LOGO_WIDTH = 72;
    private const int LOGO_HEIGHT = 18;

    private const string Logo = """
                           ███      ███                            ████
                          █████   █████                            ████
                          █████   █████                            ████
                           ███   ██████                            ████
                                 █████                             ████
████   ████  ████ ████     ███  ██████     ██████         ██████   ████
████   ████  ██████████   █████ ███████  █████████       ████████  ████
████   ████  ███████████  █████ ███████  ██████████     ██████████ ████
████   ████  ███████████  █████ ███████ ████████████   ██████████  ████
████   ████  █████  ████  █████  ████   ████   █████   █████  ██   ████
████   ████  ████   ████  █████  ████  █████    █████ █████        ████
████   ████  ████   ████  █████  ████  █████    █████ █████        ████
████   ████  ████   ████  █████  ████  █████    █████ █████        ████
████   ████  ████   ████  █████  ████  █████    ████   █████       ████
███████████  ████   ████  █████  ████   ████████████   █████████   ████
███████████  ████   ████  █████  ████   ███████████     █████████  ████
 █████████   ████   ████  █████  ████    ██████████     █████████  ████
  ███████    ████   ████  █████  ████      ██████         ██████   ████                                                                                                                                                    
""";

    public static void SeedBootLog(List<string> streamLog)
    {
        EnsureLogoViewport(streamLog, LOGO_WIDTH, LOGO_HEIGHT);

        streamLog.Add("[bold deepskyblue1]unifocl[/]");
        streamLog.Add("[bold green]Welcome to unifocl[/]");
        streamLog.Add($"[grey]cli[/]: [white]{Markup.Escape(CliVersion.SemVer)}[/] ([white]{Markup.Escape(CliVersion.Protocol)}[/])");
        streamLog.Add(string.Empty);

        foreach (var line in Logo.Split('\n'))
        {
            streamLog.Add($"[{CliTheme.Brand}]{Markup.Escape(line)}[/]");
        }

        streamLog.Add(string.Empty);
        streamLog.Add("[grey]No project attached.[/]");
        streamLog.Add(string.Empty);
    }

    private static void EnsureLogoViewport(List<string> streamLog, int minimumWidth, int minimumHeight)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        try
        {
            var currentWindowWidth = Console.WindowWidth;
            var currentWindowHeight = Console.WindowHeight;
            var maxWindowWidth = Console.LargestWindowWidth;
            var maxWindowHeight = Console.LargestWindowHeight;

            var targetWindowWidth = Math.Min(Math.Max(currentWindowWidth, minimumWidth), maxWindowWidth);
            var targetWindowHeight = Math.Min(Math.Max(currentWindowHeight, minimumHeight), maxWindowHeight);

            if (targetWindowWidth > 0 && targetWindowHeight > 0)
            {
                try
                {
                    var targetBufferWidth = Math.Max(Console.BufferWidth, targetWindowWidth);
                    var targetBufferHeight = Math.Max(Console.BufferHeight, targetWindowHeight);
                    if (targetBufferWidth != Console.BufferWidth || targetBufferHeight != Console.BufferHeight)
                    {
                        Console.SetBufferSize(targetBufferWidth, targetBufferHeight);
                    }
                }
                catch
                {
                    // Some terminals do not support buffer resizing.
                }

                if (targetWindowWidth != currentWindowWidth || targetWindowHeight != currentWindowHeight)
                {
                    Console.SetWindowSize(targetWindowWidth, targetWindowHeight);
                }
            }
        }
        catch
        {
            // Non-fatal: keep startup resilient on terminals that disallow resize APIs.
        }

        try
        {
            if (Console.WindowWidth < minimumWidth || Console.WindowHeight < minimumHeight)
            {
                streamLog.Add($"[yellow]note[/]: logo expects terminal >= {minimumWidth}x{minimumHeight}; current is {Console.WindowWidth}x{Console.WindowHeight}.");
            }
        }
        catch
        {
            // Ignore size probe failures.
        }
    }
}
