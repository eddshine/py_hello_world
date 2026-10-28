using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MasLauncher;

/// <summary>
/// The activation engine is compiled into the launcher as an embedded resource. At startup it is
/// written to a private per-user folder and run from there: no engine file sits beside the EXE and
/// nothing is downloaded separately. Updating the engine means replacing the whole launcher, which
/// is what the release channel does.
///
/// The folder must not be under %TEMP%: the engine refuses to run from a temp folder (it assumes
/// an archive was opened in place) and exits before doing anything. %LOCALAPPDATA% is a normal
/// data folder, so that check passes and the engine still leaves no file next to the EXE.
/// </summary>
internal static class Engine
{
    private const string ResourceName = "WinAct.cmd";
    private const string FileName = "WinAct.cmd";
    private const long MinBytes = 20_000;
    private const long MaxBytes = 4 * 1024 * 1024;

    private static string? _folder;

    /// <summary>The extracted engine path, or null until <see cref="Prepare"/> succeeds.</summary>
    public static string? FilePath { get; private set; }

    /// <summary>The <c>set masver=</c> value of the embedded engine.</summary>
    public static string Version { get; private set; } = "?";

    /// <summary>Where extracted engines live: a normal per-user data folder, never %TEMP%.</summary>
    public static string RootFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsActivator");

    /// <summary>Extracts the embedded engine once and returns its path.</summary>
    public static string? Prepare(out string? error)
    {
        error = null;

        if (FilePath is not null && File.Exists(FilePath)) return FilePath;

        try
        {
            var bytes = ReadEmbedded();
            if (bytes.Length < MinBytes)
            {
                error = $"the embedded engine is only {bytes.Length:N0} bytes";
                return null;
            }

            if (bytes.Length > MaxBytes)
            {
                error = $"the embedded engine is {bytes.Length:N0} bytes, which is too large";
                return null;
            }

            _folder = Path.Combine(RootFolder, "engine-" + Environment.ProcessId);
            Directory.CreateDirectory(_folder);
            CleanupStale(_folder);

            var target = Path.Combine(_folder, FileName);
            File.WriteAllBytes(target, Normalise(bytes));

            var problem = Validate(target, out var version);
            if (problem is not null)
            {
                error = "the embedded engine did not validate (" + problem + ")";
                FilePath = null;
                return null;
            }

            FilePath = target;
            Version = version;
            return target;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            FilePath = null;
            return null;
        }
    }

    /// <summary>Removes the per-process copy. Best effort: a running child may still hold it.</summary>
    public static void Cleanup()
    {
        try
        {
            if (_folder is not null && Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }
        catch
        {
            // the file is still open; the next launch removes the folder
        }

        _folder = null;
        FilePath = null;
    }

    /// <summary>
    /// Removes copies left behind by launches that ended without cleanup (a crash or a kill).
    /// Copies whose process is still running are left alone.
    /// </summary>
    private static void CleanupStale(string current)
    {
        try
        {
            foreach (var folder in Directory.GetDirectories(RootFolder, "engine-*"))
            {
                if (string.Equals(folder, current, StringComparison.OrdinalIgnoreCase)) continue;

                var pidText = Path.GetFileName(folder)["engine-".Length..];
                if (int.TryParse(pidText, out var pid) && IsRunning(pid)) continue;

                try { Directory.Delete(folder, true); } catch { }
            }
        }
        catch
        {
            // no root folder yet, or it cannot be listed
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the path sits inside the folder the engine refuses to start from. The engine
    /// compares its own full path against <c>%USERPROFILE%\AppData\Local\Temp</c> as plain text,
    /// so this mirrors that test rather than resolving the current TEMP setting.
    /// </summary>
    public static bool InTempFolder(string path)
    {
        var temp = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Temp");
        return path.Contains(temp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Structural sanity check. Returns null when the file looks like the engine.</summary>
    public static string? Validate(string path, out string version)
    {
        version = "?";
        try
        {
            var length = new FileInfo(path).Length;
            if (length < MinBytes) return $"{length:N0} bytes is far too small to be a WinAct file";

            var text = File.ReadAllText(path, Encoding.Latin1);
            if (!text.Contains(":MainMenu", StringComparison.OrdinalIgnoreCase))
                return "it has no main menu, so it is not a valid WinAct build";
            if (!Regex.IsMatch(text, @"(?im)^[\s@]*set\s+""?masver""?\s*="))
                return "it has no version marker, so it is not a valid WinAct build";

            version = ReadVersion(path) ?? "?";
            return null;
        }
        catch (Exception ex)
        {
            return "cannot read the file (" + ex.Message + ")";
        }
    }

    /// <summary>Reads the <c>set masver=</c> line the engine uses for its own version.</summary>
    public static string? ReadVersion(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path).Take(60))
            {
                var match = Regex.Match(line, @"^[\s@]*set\s+""?masver""?\s*=\s*v?([0-9][0-9.]*)", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.TrimEnd('.');
            }
        }
        catch
        {
            // unreadable file
        }
        return null;
    }

    /// <summary>The raw bytes compiled into this build.</summary>
    public static byte[] ReadEmbedded()
    {
        using var stream = typeof(Engine).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("this build has no embedded WinAct engine");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>The engine aborts unless every line ends in CRLF and the file ends with one.</summary>
    private static byte[] Normalise(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes); // byte-preserving for the ASCII file
        var converted = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        if (!converted.EndsWith("\r\n", StringComparison.Ordinal)) converted += "\r\n";
        return Encoding.Latin1.GetBytes(converted);
    }
}
