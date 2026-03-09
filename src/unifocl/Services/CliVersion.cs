internal static class CliVersion
{
    public const int Major = 0;
    public const int Minor = 5;
    public const int Patch = 2;
    public const string DevCycle = "";
    public const string Protocol = "v3";

    public static string SemVer => string.IsNullOrWhiteSpace(DevCycle)
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}{DevCycle}";
}
