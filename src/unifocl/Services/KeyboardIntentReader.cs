using System.Text;

internal static class KeyboardIntentReader
{
    public static KeyboardIntent ReadIntent()
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Escape)
        {
            var (sequenceIntent, consumedSequence) = TryReadAnsiEscapeSequenceIntent();
            if (sequenceIntent is not null)
            {
                return sequenceIntent.Value;
            }

            if (consumedSequence)
            {
                // Unknown escape sequence: consume and ignore instead of treating it as literal Esc,
                // which causes accidental mode transitions/flicker in focus UIs.
                return KeyboardIntent.None;
            }
        }

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

        if (key.Key == ConsoleKey.LeftArrow)
        {
            return KeyboardIntent.Left;
        }

        if (key.Key == ConsoleKey.RightArrow)
        {
            return KeyboardIntent.Right;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            return KeyboardIntent.Enter;
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

        return KeyboardIntent.None;
    }

    private static (KeyboardIntent? Intent, bool ConsumedSequence) TryReadAnsiEscapeSequenceIntent()
    {
        if (!Console.KeyAvailable)
        {
            return (null, false);
        }

        var sequence = new StringBuilder(capacity: 4);
        var startedAt = Environment.TickCount64;
        while (Environment.TickCount64 - startedAt <= 25)
        {
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(1);
                continue;
            }

            var next = Console.ReadKey(intercept: true);
            sequence.Append(next.KeyChar);
            if (TryMapAnsiSequence(sequence.ToString(), out var intent))
            {
                return (intent, true);
            }

            if (sequence.Length >= 8 || IsAnsiSequenceTerminator(next.KeyChar))
            {
                break;
            }
        }

        return (null, sequence.Length > 0);
    }

    private static bool TryMapAnsiSequence(string sequence, out KeyboardIntent intent)
    {
        intent = KeyboardIntent.None;
        if (sequence.Length >= 2 && sequence[0] == '[')
        {
            var terminator = sequence[^1];
            intent = terminator switch
            {
                'A' => KeyboardIntent.Up,
                'B' => KeyboardIntent.Down,
                'C' => KeyboardIntent.Right,
                'D' => KeyboardIntent.Left,
                'Z' => KeyboardIntent.ShiftTab,
                _ => KeyboardIntent.None
            };
            if (intent != KeyboardIntent.None)
            {
                return true;
            }
        }

        intent = sequence switch
        {
            "[A" or "OA" => KeyboardIntent.Up,
            "[B" or "OB" => KeyboardIntent.Down,
            "[C" or "OC" => KeyboardIntent.Right,
            "[D" or "OD" => KeyboardIntent.Left,
            "[Z" => KeyboardIntent.ShiftTab,
            _ => KeyboardIntent.None
        };

        return intent != KeyboardIntent.None;
    }

    private static bool IsAnsiSequenceTerminator(char ch)
    {
        return (ch >= 'A' && ch <= 'Z')
               || (ch >= 'a' && ch <= 'z')
               || ch == '~';
    }
}
