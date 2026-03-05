internal static class CliVersion
{
    public const int Major = 0;
    public const int Minor = 3;
    public const int Patch = 0;
    public const string Protocol = "v2";

    public static string SemVer => $"{Major}.{Minor}.{Patch}";
}
