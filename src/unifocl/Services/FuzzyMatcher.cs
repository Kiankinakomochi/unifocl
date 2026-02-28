internal static class FuzzyMatcher
{
    public static bool TryScore(string query, string candidate, out double score)
    {
        score = -1d;
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var q = query.Trim().ToLowerInvariant();
        var c = candidate.ToLowerInvariant();

        var qIndex = 0;
        var streak = 0;
        var total = 0d;

        for (var i = 0; i < c.Length && qIndex < q.Length; i++)
        {
            if (c[i] != q[qIndex])
            {
                streak = 0;
                continue;
            }

            streak++;
            total += 1d + streak * 0.65d;
            if (i == 0 || c[i - 1] is '/' or '\\' or '_' or '-' or ' ')
            {
                total += 1.5d;
            }

            qIndex++;
        }

        if (qIndex != q.Length)
        {
            return false;
        }

        var densityBonus = (double)q.Length / c.Length;
        score = total + densityBonus * 8d;
        return true;
    }
}

