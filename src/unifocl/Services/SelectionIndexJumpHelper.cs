internal static class SelectionIndexJumpHelper
{
    private const long BufferTimeoutMs = 1200;

    public static bool TryApply(
        KeyboardIntent intent,
        Func<int, bool> trySelectByIndex,
        ref string indexBuffer,
        ref long lastInputTick)
    {
        if (!KeyboardIntentReader.TryGetDigit(intent, out var digit))
        {
            indexBuffer = string.Empty;
            return false;
        }

        var now = Environment.TickCount64;
        if (string.IsNullOrEmpty(indexBuffer) || now - lastInputTick > BufferTimeoutMs)
        {
            indexBuffer = digit.ToString();
        }
        else
        {
            indexBuffer += digit.ToString();
        }

        lastInputTick = now;
        if (int.TryParse(indexBuffer, out var bufferedIndex) && trySelectByIndex(bufferedIndex))
        {
            return true;
        }

        // If a multi-digit prefix is invalid for the current list, still allow direct single-digit jumps.
        indexBuffer = digit.ToString();
        if (int.TryParse(indexBuffer, out var singleDigitIndex) && trySelectByIndex(singleDigitIndex))
        {
            return true;
        }

        return false;
    }
}
