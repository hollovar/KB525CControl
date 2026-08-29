using System.Diagnostics;

namespace KB525CControl;

/// <summary>
/// Starts programs for the Run / RunInActiveFolder / Macro actions.
///
/// .ps1 files go through PowerShell, .bat/.cmd through cmd, executables directly (PATH is
/// searched), anything else (documents, URLs) through the shell. Environment variables
/// (KB525C_*) and the working directory are passed whenever the process is started directly.
/// </summary>
public static class ProcessLauncher
{
    /// <summary>Starts the program and returns a short description for the log / OSD.</summary>
    public static string Start(string value, string? arguments, string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
    {
        value = Environment.ExpandEnvironmentVariables(value.Trim());
        arguments = arguments is null ? "" : Environment.ExpandEnvironmentVariables(arguments);
        var ext = Path.GetExtension(value).ToLowerInvariant();

        ProcessStartInfo psi;
        switch (ext)
        {
            case ".ps1":
                psi = new ProcessStartInfo(PowerShellHost())
                {
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{value}\" {arguments}".TrimEnd(),
                    UseShellExecute = false
                };
                break;

            case ".bat":
            case ".cmd":
                psi = new ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/d /c \"\"{value}\" {arguments}\"",
                    UseShellExecute = false
                };
                break;

            case ".exe":
            case "":
                psi = new ProcessStartInfo(value) { Arguments = arguments, UseShellExecute = false };
                break;

            default:
                // Document or URL: let the shell pick the handler. No env vars possible here.
                psi = new ProcessStartInfo(value) { Arguments = arguments, UseShellExecute = true };
                break;
        }

        if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        if (!psi.UseShellExecute && environment is not null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;

        try
        {
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception) when (!psi.UseShellExecute && ext == "")
        {
            // A bare name that is really a .cmd (npm, code, ...): retry through cmd.
            var viaCmd = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/d /c \"{value} {arguments}\"".TrimEnd(),
                UseShellExecute = false,
                WorkingDirectory = psi.WorkingDirectory
            };
            if (environment is not null)
                foreach (var (k, v) in environment) viaCmd.Environment[k] = v;
            Process.Start(viaCmd);
        }

        var name = Path.GetFileName(value);
        return string.IsNullOrEmpty(psi.WorkingDirectory) ? name : $"{name} in {psi.WorkingDirectory}";
    }

    static string PowerShellHost()
    {
        // Prefer PowerShell 7 when present.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "pwsh.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return "powershell.exe";
    }
}
