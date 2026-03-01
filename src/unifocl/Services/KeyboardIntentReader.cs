internal static class KeyboardIntentReader
{
    public static KeyboardIntent ReadIntent()
    {
        var key = Console.ReadKey(intercept: true);
        return FromConsoleKey(key);
    }

    public static KeyboardIntent FromConsoleKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.UpArrow)
        {
            return KeyboardIntent.Up;
        }

        if (key.Key == ConsoleKey.DownArrow)
        {
            return KeyboardIntent.Down;
        }

        if (key.Key == ConsoleKey.Tab && key.Modifiers.HasFlag(ConsoleModifiers.Shift))
        {
            return KeyboardIntent.ShiftTab;
        }

        if (key.Key == ConsoleKey.Tab)
        {
            return KeyboardIntent.Tab;
        }

        if (key.Key == ConsoleKey.Escape)
        {
            return KeyboardIntent.Escape;
        }

        if (key.Key == ConsoleKey.F7)
        {
            return KeyboardIntent.FocusProject;
        }

        if (key.Key == ConsoleKey.F8)
        {
            return KeyboardIntent.FocusInspector;
        }

        return KeyboardIntent.None;
    }
}
