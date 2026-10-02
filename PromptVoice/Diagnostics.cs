namespace PromptVoice;

/// <summary>
/// Local-only troubleshooting trail. Nothing here leaves the machine; both
/// files live next to the Whisper runtime so the tray menu can open them.
/// </summary>
internal static class Diagnostics
{
    private const long MaxLogBytes = 256 * 1024;
    private static readonly object Gate = new();

    /// <summary>Writable user-data location; see <see cref="AppPaths"/>.</summary>
    public static string RuntimeDirectory => AppPaths.DataDirectory;

    public static string LogPath => AppPaths.LogPath;

    public static string TranscriptPath => AppPaths.TranscriptPath;

    public static void Log(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(RuntimeDirectory);

                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxLogBytes)
                    File.Delete(LogPath);

                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostics must never break dictation.
            }
        }
    }

    /// <summary>Keeps the transcript recoverable by hand when insertion fails.</summary>
    public static void SaveTranscript(string text)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(RuntimeDirectory);
                File.WriteAllText(TranscriptPath, text);
            }
            catch (Exception ex)
            {
                Log("could not write last-transcription.txt: " + ex.Message);
            }
        }
    }
}
