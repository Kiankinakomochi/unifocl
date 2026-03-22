internal static class CliVersion
{
    public const int Major = 1;
    public const int Minor = 4;
    public const int Patch = 0;
    public const string DevCycle = "a14";
    public const string Protocol = "v9";

    public static string SemVer => string.IsNullOrWhiteSpace(DevCycle)
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}{DevCycle}";
}
