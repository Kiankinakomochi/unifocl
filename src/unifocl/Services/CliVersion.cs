internal static class CliVersion
{
    public const int Major = 2;
    public const int Minor = 4;
    public const int Patch = 1;
    public const string DevCycle = "a7";
    public const string Protocol = "v11";

    public static string SemVer => string.IsNullOrWhiteSpace(DevCycle)
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}{DevCycle}";
}
