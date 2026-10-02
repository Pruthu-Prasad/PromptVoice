namespace PromptVoice;

/// <summary>
/// Separates program files from user data.
///
/// Installed builds live under %LOCALAPPDATA%\Programs\PromptVoice, which the
/// user can write to but should not: an installer replaces that folder wholesale
/// on upgrade, and a machine-wide install would be read-only. Everything the app
/// creates therefore goes to <see cref="DataDirectory"/> instead.
/// </summary>
internal static class AppPaths
{
    /// <summary>Where the app and its bundled model were installed.</summary>
    public static string InstallDirectory { get; } = AppContext.BaseDirectory;

    /// <summary>whisper-cli.exe, its DLLs, and any model shipped by the installer.</summary>
    public static string BundledDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "runtime");

    /// <summary>Settings, history, logs, and scratch audio.</summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    /// <summary>Models the user downloaded from inside the app.</summary>
    public static string DownloadedModelDirectory { get; } = Path.Combine(DataDirectory, "models");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static string HistoryPath => Path.Combine(DataDirectory, "history.json");
    public static string LogPath => Path.Combine(DataDirectory, "diagnostics.log");
    public static string TranscriptPath => Path.Combine(DataDirectory, "last-transcription.txt");
    public static string CapturePath => Path.Combine(DataDirectory, "input.wav");

    /// <summary>Ensures both writable locations exist; safe to call repeatedly.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(DownloadedModelDirectory);
    }

    /// <summary>
    /// Both model locations, downloaded first, so a model the user fetched wins
    /// over a bundled one of the same name.
    /// </summary>
    public static IEnumerable<string> ModelSearchPath()
    {
        yield return DownloadedModelDirectory;
        yield return BundledDirectory;
    }

    public static bool TryGetFreeDiskBytes(string path, out long free)
    {
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)) ?? "C:\\").AvailableFreeSpace;
            return true;
        }
        catch
        {
            free = 0;
            return false;
        }
    }

    private static string ResolveDataDirectory()
    {
        // A portable copy keeps its data alongside the executable, but only when
        // that folder is genuinely writable — otherwise fall back to LOCALAPPDATA.
        string portableMarker = Path.Combine(AppContext.BaseDirectory, "portable.marker");
        if (File.Exists(portableMarker) && IsWritable(AppContext.BaseDirectory))
            return Path.Combine(AppContext.BaseDirectory, "data");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PromptVoice");
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
