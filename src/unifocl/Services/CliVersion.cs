internal static class CliVersion
{
    public const int Major = 2;
    public const int Minor = 22;
    public const int Patch = 2;
    public const string DevCycle = "a3";
    public const string Protocol = "v17";

    public static string SemVer => string.IsNullOrWhiteSpace(DevCycle)
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}{DevCycle}";
}
