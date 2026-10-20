namespace JluDrcom.Core.Configuration;

/// <summary>Well-known paths. Keep these in one place so deployment and apps agree.</summary>
public static class AppPaths
{
    public const string ProductDirectoryName = "JluDrcom";

    public static string ProgramDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ProductDirectoryName);

    public static string DefaultConfigPath => Path.Combine(ProgramDataRoot, "config.json");

    public static string LogsDirectory => Path.Combine(ProgramDataRoot, "logs");

    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ProductDirectoryName);

    public static string LegacyLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Yesterday17",
        "JLUDrcomService",
        "Log.log");
}
