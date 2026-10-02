using System.Text.Json;

namespace PromptVoice;

internal sealed record DictationEntry(string Text, DateTime Utc, bool Inserted, string Model, long Milliseconds);

/// <summary>
/// The last few dictations, so a transcript is never lost to a failed paste.
/// Local file, newest first, hard-capped — this is a convenience, not an archive.
/// </summary>
internal static class DictationHistory
{
    private const int MaxEntries = 50;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static List<DictationEntry>? cache;

    public static event Action? Changed;

    private static string Path => AppPaths.HistoryPath;

    public static List<DictationEntry> All()
    {
        lock (Gate)
        {
            if (cache is null)
            {
                try
                {
                    cache = File.Exists(Path)
                        ? JsonSerializer.Deserialize<List<DictationEntry>>(File.ReadAllText(Path), Json) ?? new()
                        : new();
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("history could not be read: " + ex.Message);
                    cache = new();
                }
            }

            return new List<DictationEntry>(cache);
        }
    }

    public static void Add(DictationEntry entry)
    {
        lock (Gate)
        {
            All();
            cache!.Insert(0, entry);

            if (cache.Count > MaxEntries)
                cache.RemoveRange(MaxEntries, cache.Count - MaxEntries);

            Persist();
        }

        Changed?.Invoke();
    }

    public static void Clear()
    {
        lock (Gate)
        {
            cache = new();
            Persist();
        }

        Changed?.Invoke();
    }

    private static void Persist()
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(Path, JsonSerializer.Serialize(cache, Json));
        }
        catch (Exception ex)
        {
            Diagnostics.Log("history could not be saved: " + ex.Message);
        }
    }
}
