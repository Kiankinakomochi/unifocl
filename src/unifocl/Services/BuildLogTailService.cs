using System.Diagnostics;
using System.Text;
using Spectre.Console;

internal static class BuildLogTailService
{
    public static bool TryRunFromArgs(string[] args)
    {
        if (args.Length < 2 || !args[0].Equals("--build-log-tail", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        RunInteractive(args[1], "Build Logs");
        return true;
    }

    public static bool TryLaunchSecondaryTerminal(string logPath)
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return false;
            }

            if (OperatingSystem.IsMacOS())
            {
                var escapedBinary = processPath.Replace("\"", "\\\"");
                var escapedLog = logPath.Replace("\"", "\\\"");
                var command = $"\\\"{escapedBinary}\\\" --build-log-tail \\\"{escapedLog}\\\"";
                var script = $"tell application \"Terminal\" to do script \"{command}\"";
                var psi = new ProcessStartInfo("osascript", $"-e \"{script}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = Process.Start(psi);
                return process is not null;
            }
        }
        catch
        {
        }

        return false;
    }

    public static void RunInteractive(string logPath, string title)
    {
        var offset = 0L;
        var lines = new List<(string Level, string Text)>();
        var errorsOnly = false;
        var keep = 200;

        while (true)
        {
            ReadNewLines(logPath, ref offset, lines);
            if (lines.Count > keep)
            {
                lines.RemoveRange(0, lines.Count - keep);
            }

            if (!Console.IsOutputRedirected)
            {
                Console.Write("\u001b[H\u001b[0J");
            }

            var mode = errorsOnly ? "ERRORS ONLY" : "ALL";
            AnsiConsole.MarkupLine($"[bold deepskyblue1]{Markup.Escape(title)}[/] [grey]({Markup.Escape(mode)})[/]");
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(logPath)}[/]");
            AnsiConsole.MarkupLine("[dim]Keybinds: E = toggle error filter, Q = quit[/]");
            AnsiConsole.WriteLine();

            foreach (var line in lines)
            {
                if (errorsOnly && !line.Level.Equals("error", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var style = line.Level.Equals("error", StringComparison.OrdinalIgnoreCase)
                    ? "red"
                    : (line.Level.Equals("warning", StringComparison.OrdinalIgnoreCase) ? "yellow" : "grey");
                AnsiConsole.MarkupLine($"[{style}]{Markup.Escape(line.Text)}[/]");
            }

            var end = DateTime.UtcNow.AddMilliseconds(250);
            while (DateTime.UtcNow < end)
            {
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(25);
                    continue;
                }

                var key = Console.ReadKey(intercept: true);
                if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
                {
                    return;
                }

                if (key.Key == ConsoleKey.E)
                {
                    errorsOnly = !errorsOnly;
                    break;
                }
            }
        }
    }

    public static void ShowSnapshotAndWait(string logPath, string title, bool waitForKey)
    {
        var lines = new List<(string Level, string Text)>();
        var offset = 0L;
        ReadNewLines(logPath, ref offset, lines);
        var visible = lines.TakeLast(60).ToList();

        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold deepskyblue1]{Markup.Escape(title)}[/]");
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(logPath)}[/]");
        AnsiConsole.WriteLine();

        if (visible.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]no log lines captured[/]");
        }
        else
        {
            foreach (var line in visible)
            {
                var style = line.Level.Equals("error", StringComparison.OrdinalIgnoreCase)
                    ? "red"
                    : (line.Level.Equals("warning", StringComparison.OrdinalIgnoreCase) ? "yellow" : "grey");
                AnsiConsole.MarkupLine($"[{style}]{Markup.Escape(line.Text)}[/]");
            }
        }

        if (!waitForKey)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Press any key to return to project mode...[/]");
        _ = Console.ReadKey(intercept: true);
    }

    private static void ReadNewLines(string path, ref long offset, List<(string Level, string Text)> lines)
    {
        if (!File.Exists(path))
        {
            return;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset > stream.Length)
        {
            offset = 0;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!reader.EndOfStream)
        {
            var raw = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            lines.Add(ParseLine(raw));
        }

        offset = stream.Position;
    }

    private static (string Level, string Text) ParseLine(string line)
    {
        var first = line.IndexOf('|');
        if (first < 0)
        {
            return ("info", line);
        }

        var second = line.IndexOf('|', first + 1);
        if (second < 0)
        {
            return ("info", line[(first + 1)..]);
        }

        var level = line[(first + 1)..second];
        var text = line[(second + 1)..];
        return (string.IsNullOrWhiteSpace(level) ? "info" : level, text);
    }
}
