internal static class CliVersion
{
    public const int Major = 0;
    public const int Minor = 21;
    public const int Patch = 0;
    public const string DevCycle = "a3";
    public const string Protocol = "v7";

    public static string SemVer => string.IsNullOrWhiteSpace(DevCycle)
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}{DevCycle}";
}
