internal static class CliVersion
{
    public const int Major = 3;
    public const int Minor = 10;
    public const int Patch = 0;
    public const string DevCycle = "";
    public const string Protocol = "v21";

    public static string SemVer => string.IsNullOrWhiteSpace(DevCycle)
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}{DevCycle}";
}
