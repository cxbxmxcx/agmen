using System.IO;

namespace Agmen.Services;

/// <summary>
/// Where this instance keeps its state. Debug builds run as a separate "dev"
/// instance with their own data folder, single-instance lock, and tray icon,
/// so Agmen can be rebuilt and tested while the release copy keeps
/// hosting real sessions.
/// </summary>
public static class AppPaths
{
#if DEBUG
    public static bool IsDev => true;
#else
    public static bool IsDev => false;
#endif

    /// <summary>
    /// Base name for the data folder and the named mutex and event. AGMEN_INSTANCE
    /// overrides it, so a test copy can run beside the copies people use, with its own data.
    /// </summary>
    public static string InstanceName { get; } =
        Environment.GetEnvironmentVariable("AGMEN_INSTANCE") is { Length: > 0 } custom
        && custom.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            ? custom
            : IsDev ? "Agmen-dev" : "Agmen";

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), InstanceName);

    /// <summary>Full path of a file in the data folder, creating the folder if needed.</summary>
    public static string PathOf(string fileName)
    {
        Directory.CreateDirectory(DataDir);
        return Path.Combine(DataDir, fileName);
    }
}
