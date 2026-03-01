internal static class TuiTextWrap
{
    public static List<string> WrapPlainText(string? value, int width)
    {
        var safeWidth = Math.Max(1, width);
        if (string.IsNullOrEmpty(value))
        {
            return [string.Empty];
        }

        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var logicalLines = normalized.Split('\n');
        var wrapped = new List<string>(logicalLines.Length);
        foreach (var logicalLine in logicalLines)
        {
            var line = logicalLine.Replace('\t', ' ');
            if (line.Length == 0)
            {
                wrapped.Add(string.Empty);
                continue;
            }

            for (var offset = 0; offset < line.Length; offset += safeWidth)
            {
                var length = Math.Min(safeWidth, line.Length - offset);
                wrapped.Add(line.Substring(offset, length));
            }
        }

        return wrapped.Count == 0 ? [string.Empty] : wrapped;
    }
}
