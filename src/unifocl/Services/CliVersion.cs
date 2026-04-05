internal static class CliVersion
{
    public const int Major = 3;
    public const int Minor = 8;
    public const int Patch = 6;
    public const string DevCycle = "";
    public const string Protocol = "v20";

    public static string SemVer => string.IsNullOrWhiteSpace(DevCycle)
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}{DevCycle}";
}
