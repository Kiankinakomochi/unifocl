using Spectre.Console;

internal static class TuiTrackableProgress
{
    private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    public static Spinner StatusSpinner => Spinner.Known.Dots2;

    public static string BuildProgressBar(double progress01, int width)
    {
        var clamped = Math.Clamp(progress01, 0d, 1d);
        var filled = (int)Math.Round(clamped * width);
        filled = Math.Clamp(filled, 0, width);
        return new string('█', filled) + new string('░', width - filled);
    }

    public static double ComputeExpectedDurationProgress(
        TimeSpan elapsed,
        TimeSpan expectedDuration,
        double inWindowMax = 0.9d,
        double overrunMax = 0.99d)
    {
        if (expectedDuration.TotalMilliseconds <= 0d)
        {
            return 0d;
        }

        var ratio = elapsed.TotalMilliseconds / expectedDuration.TotalMilliseconds;
        if (ratio <= 1d)
        {
            return Math.Clamp(ratio * inWindowMax, 0d, inWindowMax);
        }

        var overrun = ratio - 1d;
        var tail = 1d - Math.Exp(-overrun);
        return Math.Clamp(inWindowMax + ((overrunMax - inWindowMax) * tail), inWindowMax, overrunMax);
    }

    public static string BuildTrackableLine(
        string activity,
        int tick,
        double progress01,
        TimeSpan elapsed,
        int barWidth = 18,
        bool done = false,
        bool failed = false)
    {
        var clamped = Math.Clamp(progress01, 0d, 1d);
        var bar = BuildProgressBar(clamped, barWidth);
        var percent = (int)Math.Round(clamped * 100d);
        var status = failed
            ? "[red]✖[/]"
            : done
                ? "[green]✔[/]"
                : $"[{CliTheme.Info}]{Markup.Escape(Frames[Math.Abs(tick) % Frames.Length])}[/]";
        return $"{status} {Markup.Escape(activity)} [{CliTheme.TextSecondary}][[{bar}]] {percent,3}% {elapsed.TotalSeconds,4:0.0}s[/]";
    }
}
