using System.Text;

internal static class KeyboardIntentReader
{
    public static KeyboardIntent ReadIntent()
    {
        var key = Console.ReadKey(intercept: true);
        return ReadIntentFromFirstKey(key);
    }

    public static KeyboardIntent ReadIntentFromFirstKey(ConsoleKeyInfo key)
    {
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
        if (TryMapDigitIntent(key, out var digitIntent))
        {
            return digitIntent;
        }

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

        if (key.Key is ConsoleKey.F7 or ConsoleKey.F8)
        {
            return KeyboardIntent.FocusProject;
        }

        return KeyboardIntent.None;
    }

    public static bool TryGetDigit(KeyboardIntent intent, out int value)
    {
        value = intent switch
        {
            KeyboardIntent.Digit0 => 0,
            KeyboardIntent.Digit1 => 1,
            KeyboardIntent.Digit2 => 2,
            KeyboardIntent.Digit3 => 3,
            KeyboardIntent.Digit4 => 4,
            KeyboardIntent.Digit5 => 5,
            KeyboardIntent.Digit6 => 6,
            KeyboardIntent.Digit7 => 7,
            KeyboardIntent.Digit8 => 8,
            KeyboardIntent.Digit9 => 9,
            _ => -1
        };

        return value >= 0;
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

    private static bool TryMapDigitIntent(ConsoleKeyInfo key, out KeyboardIntent intent)
    {
        intent = KeyboardIntent.None;
        if (char.IsDigit(key.KeyChar))
        {
            intent = key.KeyChar switch
            {
                '0' => KeyboardIntent.Digit0,
                '1' => KeyboardIntent.Digit1,
                '2' => KeyboardIntent.Digit2,
                '3' => KeyboardIntent.Digit3,
                '4' => KeyboardIntent.Digit4,
                '5' => KeyboardIntent.Digit5,
                '6' => KeyboardIntent.Digit6,
                '7' => KeyboardIntent.Digit7,
                '8' => KeyboardIntent.Digit8,
                '9' => KeyboardIntent.Digit9,
                _ => KeyboardIntent.None
            };
            return intent != KeyboardIntent.None;
        }

        intent = key.Key switch
        {
            ConsoleKey.NumPad0 => KeyboardIntent.Digit0,
            ConsoleKey.NumPad1 => KeyboardIntent.Digit1,
            ConsoleKey.NumPad2 => KeyboardIntent.Digit2,
            ConsoleKey.NumPad3 => KeyboardIntent.Digit3,
            ConsoleKey.NumPad4 => KeyboardIntent.Digit4,
            ConsoleKey.NumPad5 => KeyboardIntent.Digit5,
            ConsoleKey.NumPad6 => KeyboardIntent.Digit6,
            ConsoleKey.NumPad7 => KeyboardIntent.Digit7,
            ConsoleKey.NumPad8 => KeyboardIntent.Digit8,
            ConsoleKey.NumPad9 => KeyboardIntent.Digit9,
            _ => KeyboardIntent.None
        };
        return intent != KeyboardIntent.None;
    }
}
