using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PromptVoice;

/// <summary>An English Whisper model present in the runtime folder.</summary>
internal sealed record WhisperModel(string FileName, string Name, string Detail)
{
    // The settings row already carries the explanation, so the drop-down shows
    // only the name and stays readable at any display scaling.
    public override string ToString() => Name;
}

/// <summary>
/// Runs whisper.cpp locally. Accuracy for coding dictation comes from two places:
/// a real model (base.en or better) and an initial prompt that primes the decoder
/// with developer vocabulary.
/// </summary>
internal static class WhisperEngine
{
    public const string PreferredModel = "ggml-base.en.bin";

    /// <summary>
    /// Measured on synthesised developer-prompt speech, word error rate:
    /// tiny.en 10.7%, base.en 7.8%, base.en+prompt 5.8%, small.en+prompt 3.9%.
    /// The prompt does not help tiny.en — it is too small to use the context.
    /// </summary>
    private static readonly WhisperModel[] Catalogue =
    {
        new("ggml-tiny.en.bin",  "Tiny",  "English, fastest, least accurate"),
        new("ggml-base.en.bin",  "Base",  "English, recommended balance"),
        new("ggml-small.en.bin", "Small", "English, most accurate but slower")
    };

    /// <summary>
    /// Primes the decoder toward coding and AI-agent terms. Whisper treats this as
    /// preceding context, so spelled-out tool names are far likelier to survive.
    /// </summary>
    private const string TechnicalVocabulary =
        "Technical dictation for a coding assistant. Terms: Claude Code, Claude, Anthropic, ChatGPT, Copilot, " +
        "LLM, prompt, agent, MCP, API, CLI, SDK, JSON, YAML, TOML, Markdown, regex, UTF-8, " +
        "TypeScript, JavaScript, Python, Rust, Go, C#, SQL, HTML, CSS, React, Next.js, Node, npm, pnpm, yarn, " +
        "git, GitHub, commit, branch, merge, rebase, pull request, diff, stash, repo, repository, " +
        "refactor, async, await, promise, callback, function, method, class, interface, struct, enum, " +
        "const, let, var, null, undefined, boolean, integer, float, array, object, payload, schema, " +
        "endpoint, middleware, handler, route, auth, token, OAuth, JWT, header, request, response, " +
        "Postgres, MySQL, SQLite, Redis, MongoDB, query, index, migration, transaction, " +
        "Docker, Kubernetes, container, deploy, CI, CD, pipeline, build, compile, lint, unit test, " +
        "stack trace, exception, breakpoint, stdout, stderr, localhost, port, environment variable.";

    public static string Executable => Path.Combine(AppPaths.BundledDirectory, "whisper-cli.exe");

    /// <summary>Resolves a model across the bundled and downloaded locations.</summary>
    public static string ModelPath(string fileName)
    {
        foreach (string directory in AppPaths.ModelSearchPath())
        {
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        // Not installed yet; return the download target so callers can report it.
        return Path.Combine(AppPaths.DownloadedModelDirectory, fileName);
    }

    public static bool IsInstalled(string fileName) =>
        AppPaths.ModelSearchPath().Any(d => File.Exists(Path.Combine(d, fileName)));

    /// <summary>Every English model this build knows about, fastest first.</summary>
    public static IReadOnlyList<WhisperModel> Catalogued => Catalogue;

    /// <summary>Models actually present on disk, in catalogue order.</summary>
    public static List<WhisperModel> AvailableModels() =>
        Catalogue.Where(m => IsInstalled(m.FileName)).ToList();

    public static WhisperModel Describe(string fileName) =>
        Catalogue.FirstOrDefault(m => m.FileName == fileName)
        ?? new WhisperModel(fileName, fileName, "custom model");

    /// <summary>
    /// The CMake build ships GPU support as a separate backend DLL, so its
    /// presence is the honest test for whether a GPU option can do anything.
    /// </summary>
    public static bool GpuAvailable =>
        File.Exists(Path.Combine(AppPaths.BundledDirectory, "ggml-vulkan.dll")) ||
        File.Exists(Path.Combine(AppPaths.BundledDirectory, "ggml-cuda.dll"));

    public static bool UsesGpu(AppSettings settings) =>
        GpuAvailable && settings.Compute != ComputeDevice.Cpu;

    public static bool IsReady(AppSettings settings) =>
        File.Exists(Executable) && File.Exists(ModelPath(settings.Model));

    public static string BuildPrompt(AppSettings settings)
    {
        if (!settings.UseTechnicalVocabulary)
            return settings.CustomVocabulary.Trim();

        string extra = settings.CustomVocabulary.Trim();
        return extra.Length == 0
            ? TechnicalVocabulary
            : TechnicalVocabulary + " " + extra.TrimEnd('.') + ".";
    }

    public static async Task<string> Transcribe(string wavPath, AppSettings settings)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Executable,
            WorkingDirectory = AppPaths.BundledDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(ModelPath(settings.Model));
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(wavPath);
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("en");
        psi.ArgumentList.Add("-nt");
        psi.ArgumentList.Add("-np");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(settings.EffectiveThreads.ToString());

        // Drops "(clears throat)" style bracketed noise tokens from short clips.
        psi.ArgumentList.Add("-sns");

        if (!UsesGpu(settings))
            psi.ArgumentList.Add("-ng");

        string prompt = BuildPrompt(settings);
        if (prompt.Length > 0)
        {
            psi.ArgumentList.Add("--prompt");
            psi.ArgumentList.Add(prompt);
            psi.ArgumentList.Add("--carry-initial-prompt");
        }

        var watch = Stopwatch.StartNew();

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start whisper-cli.exe.");

        // Read both pipes concurrently; a full stderr buffer would otherwise
        // deadlock the child before it exits.
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        watch.Stop();

        Diagnostics.Log($"whisper {settings.Model} t={settings.EffectiveThreads} device={(UsesGpu(settings) ? "gpu" : "cpu")} " +
                        $"prompt={(prompt.Length > 0 ? "on" : "off")} took {watch.ElapsedMilliseconds} ms " +
                        $"exit={process.ExitCode}");

        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"whisper-cli exited with code {process.ExitCode}."
                    : detail.Trim());
        }

        return ApplyCorrections(Clean(Collect(stdout)), settings);
    }

    /// <summary>Whole-phrase, case-insensitive replacement; longest phrases first.</summary>
    public static string ApplyCorrections(string text, AppSettings settings)
    {
        if (text.Length == 0)
            return text;

        var pairs = settings.Corrections
            .Select(line => line.Split("=>", 2, StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && p[0].Length > 0)
            .OrderByDescending(p => p[0].Length);

        foreach (var pair in pairs)
        {
            string pattern = @"(?<![\w.])" + Regex.Escape(pair[0]) + @"(?![\w])";
            text = Regex.Replace(text, pattern, pair[1], RegexOptions.IgnoreCase);
        }

        return text;
    }

    private static string Collect(string stdout)
    {
        var result = new StringBuilder();

        foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string s = Regex.Replace(line.Trim(), @"^\[[^\]]+\]\s*", "");
            if (s.Length > 0)
                result.Append(s).Append(' ');
        }

        return result.ToString();
    }

    /// <summary>
    /// Whisper emits bracketed non-speech markers and a bare "[BLANK_AUDIO]" for
    /// silence; neither belongs in a prompt typed into another application.
    /// </summary>
    private static string Clean(string text)
    {
        text = Regex.Replace(text, @"\[[A-Z_ ]+\]", " ");
        text = Regex.Replace(text, @"\(\s*[^)]{0,40}\s*\)", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();

        if (text.Length == 0 || text == ".")
            return string.Empty;

        return char.ToUpper(text[0]) + text[1..];
    }
}
