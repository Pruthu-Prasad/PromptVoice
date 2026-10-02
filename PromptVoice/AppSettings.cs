using System.Text.Json;
using System.Text.Json.Serialization;

namespace PromptVoice;

/// <summary>Where Whisper runs. GPU needs a Vulkan build of whisper-cli; see setup-whisper.ps1 -Vulkan.</summary>
internal enum ComputeDevice { Auto, Cpu, Gpu }

/// <summary>The push-to-talk combination. Space is always the trigger key.</summary>
internal enum Hotkey
{
    CtrlSpace,
    AltSpace,
    CtrlShiftSpace,
    WinSpace
}

/// <summary>
/// Portable user settings stored beside the model, so copying the publish folder
/// carries the configuration with it.
/// </summary>
internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string Model { get; set; } = WhisperEngine.PreferredModel;

    public string? CaptureDeviceId { get; set; }

    public Hotkey Hotkey { get; set; } = Hotkey.CtrlSpace;

    /// <summary>
    /// Feeds Whisper an initial prompt of developer and AI-tooling terms. Measured
    /// on base.en this cut the word error rate from 7.8% to 5.8%.
    /// </summary>
    public bool UseTechnicalVocabulary { get; set; } = true;

    /// <summary>Extra comma-separated terms appended to the built-in vocabulary.</summary>
    public string CustomVocabulary { get; set; } = string.Empty;

    /// <summary>0 means "half the logical processors", which is the right default here.</summary>
    public int Threads { get; set; }

    public bool LaunchAtStartup { get; set; }

    public bool KeepHistory { get; set; } = true;

    /// <summary>Set once the user dismisses the first-run steps.</summary>
    public bool OnboardingComplete { get; set; }

    public ComputeDevice Compute { get; set; } = ComputeDevice.Auto;

    /// <summary>
    /// "heard => meant" pairs applied after transcription, whole-phrase and
    /// case-insensitive. Whisper mishears the same term the same way every time,
    /// so a fixed table beats any amount of model swapping for recurring errors.
    /// </summary>
    public List<string> Corrections { get; set; } = DefaultCorrections();

    /// <summary>Seeds observed, repeatable mishearings of developer vocabulary.</summary>
    public static List<string> DefaultCorrections() => new()
    {
        "cloud code => Claude Code",
        "clod code => Claude Code",
        "claud code => Claude Code",
        "get commit => git commit",
        "get push => git push",
        "get pull => git pull",
        "get status => git status",
        "get diff => git diff",
        "pass config => parse config",
        "n p m => npm",
        "j son => JSON",
        "jason => JSON",
        "type script => TypeScript",
        "java script => JavaScript",
        "post gress => Postgres",
        "post grass => Postgres",
        "dot t s => .ts",
        "dot tias => .ts",
        "dot tiers => .ts",
        "dot j s => .js",
        "dot p y => .py",
        "dot c s => .cs",
        "use effect => useEffect",
        "use state => useState",
        "a sync => async",
        "a wait => await"
    };

    [JsonIgnore]
    public int EffectiveThreads =>
        Threads > 0 ? Threads : Math.Max(2, Environment.ProcessorCount / 2);

    private static string Path => AppPaths.SettingsPath;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("settings could not be read, using defaults: " + ex.Message);
        }

        var defaults = new AppSettings();
        defaults.Normalize();
        return defaults;
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Diagnostics.Log("settings could not be saved: " + ex.Message);
        }
    }

    /// <summary>Falls back to a model that is actually on disk.</summary>
    private void Normalize()
    {
        var available = WhisperEngine.AvailableModels();
        if (available.Count == 0)
            return;

        if (!available.Any(m => m.FileName == Model))
        {
            string replacement = available.Any(m => m.FileName == WhisperEngine.PreferredModel)
                ? WhisperEngine.PreferredModel
                : available[0].FileName;

            Diagnostics.Log($"model '{Model}' is not installed; falling back to '{replacement}'");
            Model = replacement;
        }
    }
}
