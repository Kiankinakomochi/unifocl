internal static class CliVersion
{
    public const int Major = 0;
    public const int Minor = 3;
    public const int Patch = 2;
    public const string DevCycle = "a6";
    public const string Protocol = "v2";

    public static string SemVer => string.IsNullOrWhiteSpace(DevCycle)
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}{DevCycle}";
}
