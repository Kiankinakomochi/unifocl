internal static class TuiConsoleViewport
{
    public const int DefaultColumns = 80;
    public const int DefaultRows = 24;

    public static (int Width, int Height) GetWindowSizeOrDefault()
    {
        try
        {
            var width = Console.WindowWidth;
            var height = Console.WindowHeight;
            return (
                width > 0 ? width : DefaultColumns,
                height > 0 ? height : DefaultRows);
        }
        catch
        {
            return (DefaultColumns, DefaultRows);
        }
    }

    public static bool WaitForKeyOrResize(ref int knownWidth, ref int knownHeight, out ConsoleKeyInfo key)
    {
        while (true)
        {
            if (TryReadKey(out key))
            {
                return true;
            }

            var (width, height) = GetWindowSizeOrDefault();
            if (width != knownWidth || height != knownHeight)
            {
                knownWidth = width;
                knownHeight = height;
                key = default;
                return false;
            }

            Thread.Sleep(16);
        }
    }

    public static int GetExcessColumns(int intednedContentColumns)
    {
        var (width, _) = GetWindowSizeOrDefault();
        return Math.Max(0, intednedContentColumns - Math.Max(1, width));
    }

    public static int GetExcessRows(int intednedContentRows)
    {
        var (_, height) = GetWindowSizeOrDefault();
        return Math.Max(0, intednedContentRows - Math.Max(1, height));
    }

    private static bool TryReadKey(out ConsoleKeyInfo key)
    {
        key = default;
        try
        {
            if (!Console.KeyAvailable)
            {
                return false;
            }

            key = Console.ReadKey(intercept: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
