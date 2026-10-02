using Microsoft.Win32;

namespace PromptVoice;

/// <summary>
/// "Start with Windows", via the per-user Run key.
///
/// Chosen over a Startup-folder shortcut because it needs no admin rights, is
/// trivially reversible, and stores an absolute path we can re-verify — so a
/// stale entry left by a moved or reinstalled build is corrected rather than
/// silently failing at every login.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PromptVoice";

    /// <summary>The launcher, with a flag so an auto-start run opens no window.</summary>
    private static string Command => $"\"{ExecutablePath}\" --autostart";

    private static string ExecutablePath =>
        Path.Combine(AppPaths.InstallDirectory, "PromptVoice.exe");

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string existing && existing.Length > 0;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("could not read the startup registry key: " + ex.Message);
            return false;
        }
    }

    /// <summary>Returns true if the change was actually applied.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new InvalidOperationException("the Run key could not be opened");

            if (enabled)
                key.SetValue(ValueName, Command, RegistryValueKind.String);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);

            Diagnostics.Log($"start with Windows {(enabled ? "enabled" : "disabled")}");
            return true;
        }
        catch (Exception ex)
        {
            // Group policy or a locked-down profile can deny this; report it
            // rather than leaving the toggle showing a state that is not real.
            Diagnostics.Log("could not write the startup registry key: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Makes the registry match the saved preference and repairs a stale path
    /// left behind by an upgrade or a moved installation.
    /// </summary>
    public static void Reconcile(AppSettings settings)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            string? current = key?.GetValue(ValueName) as string;

            if (settings.LaunchAtStartup)
            {
                if (!string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                    Set(true);
            }
            else if (current is not null)
            {
                Set(false);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("startup reconcile failed: " + ex.Message);
        }
    }
}
