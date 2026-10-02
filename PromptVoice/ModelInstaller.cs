namespace PromptVoice;

/// <summary>
/// Fetches an optional Whisper model on demand. The installer bundles base.en so
/// the app works offline immediately; this is only for models a user chooses to
/// add afterwards.
/// </summary>
internal static class ModelInstaller
{
    private const string Hub = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

    /// <summary>Approximate sizes, used for the disk-space check and the prompt.</summary>
    private static readonly Dictionary<string, long> ExpectedBytes = new()
    {
        ["ggml-tiny.en.bin"] = 77_704_715,
        ["ggml-base.en.bin"] = 147_964_211,
        ["ggml-small.en.bin"] = 487_614_201
    };

    public static long ApproximateSize(string fileName) =>
        ExpectedBytes.TryGetValue(fileName, out long size) ? size : 0;

    public static string DescribeSize(string fileName)
    {
        long bytes = ApproximateSize(fileName);
        return bytes == 0 ? "unknown size" : $"{bytes / 1024d / 1024d:N0} MB";
    }

    public sealed record Result(bool Success, string Message);

    /// <summary>
    /// Downloads to a temporary file and renames on success, so an interrupted
    /// download can never leave a truncated model that Whisper would fail on.
    /// </summary>
    public static async Task<Result> Download(
        string fileName,
        IProgress<double> progress,
        CancellationToken cancellation)
    {
        long needed = ApproximateSize(fileName);

        try
        {
            AppPaths.EnsureCreated();
        }
        catch (Exception ex)
        {
            return new Result(false, "Could not create the model folder: " + ex.Message);
        }

        if (needed > 0 &&
            AppPaths.TryGetFreeDiskBytes(AppPaths.DownloadedModelDirectory, out long free) &&
            free < needed + 64L * 1024 * 1024)
        {
            return new Result(false,
                $"Not enough disk space. {DescribeSize(fileName)} is needed, " +
                $"but only {free / 1024d / 1024d:N0} MB is free.");
        }

        string target = Path.Combine(AppPaths.DownloadedModelDirectory, fileName);
        string temporary = target + ".part";

        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await http.GetAsync($"{Hub}/{fileName}",
                HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? needed;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[128 * 1024];
                long written = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    written += read;

                    if (total > 0)
                        progress.Report(Math.Clamp((double)written / total, 0, 1));
                }
            }

            File.Move(temporary, target, overwrite: true);
            Diagnostics.Log($"model downloaded: {fileName}");
            return new Result(true, $"{WhisperEngine.Describe(fileName).Name} is ready.");
        }
        catch (OperationCanceledException)
        {
            Cleanup(temporary);
            return new Result(false, "Download cancelled.");
        }
        catch (HttpRequestException ex)
        {
            Cleanup(temporary);
            Diagnostics.Log("model download failed: " + ex);
            return new Result(false, "Download failed - check your internet connection.");
        }
        catch (IOException ex)
        {
            Cleanup(temporary);
            Diagnostics.Log("model download failed: " + ex);
            return new Result(false, "Could not write the model file: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Cleanup(temporary);
            Diagnostics.Log("model download denied: " + ex);
            return new Result(false, "Permission denied writing to the model folder.");
        }
        catch (Exception ex)
        {
            Cleanup(temporary);
            Diagnostics.Log("model download failed: " + ex);
            return new Result(false, "Download failed: " + ex.Message);
        }
    }

    private static void Cleanup(string temporary)
    {
        try
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        catch
        {
            // A leftover .part file is harmless; it is overwritten next attempt.
        }
    }
}
