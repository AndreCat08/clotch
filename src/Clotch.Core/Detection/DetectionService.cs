using Microsoft.Win32;

namespace Clotch.Core.Detection;

public sealed record ClaudeCodeInfo(bool Installed, string? ExecutablePath, string? Version, bool ConfigDirExists);

public enum DesktopInstallKind { None, Msix, Squirrel }

public sealed record ClaudeDesktopInfo(bool Installed, DesktopInstallKind Kind, string? Version);

public sealed record DetectionResult(ClaudeCodeInfo ClaudeCode, ClaudeDesktopInfo ClaudeDesktop)
{
    public bool AnyInstalled => ClaudeCode.Installed || ClaudeDesktop.Installed;
}

/// <summary>Machine paths the detector looks at. Defaults mirror the real environment; tests override them.</summary>
public sealed record DetectionEnvironment(
    string ConfigDir,
    string? PathVariable,
    string AppData,
    string LocalAppData,
    string UserProfile,
    Func<IReadOnlyList<string>>? MsixPackageNames = null)
{
    public static DetectionEnvironment Current() => new(
        ClaudePaths.ConfigDir(),
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        RegistryMsixPackageNames);

    /// <summary>
    /// Full package names registered for the current user (e.g. Claude_2.42304.2.0_x64__pzs8sxrjxfjjc).
    /// Read from the per-user AppModel repository — no admin rights or WinRT needed.
    /// </summary>
    public static IReadOnlyList<string> RegistryMsixPackageNames()
    {
        const string key = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            return k?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }
}

public static class ClaudePaths
{
    /// <summary>Same resolution as Claude Code: CLAUDE_CONFIG_DIR, else %USERPROFILE%\.claude.</summary>
    public static string ConfigDir()
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return !string.IsNullOrWhiteSpace(env)
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    public static string CredentialsFile(string configDir) => Path.Combine(configDir, ".credentials.json");

    /// <summary>Claude Code's global config: CLAUDE_CONFIG_DIR\.claude.json, else %USERPROFILE%\.claude.json.</summary>
    public static string AccountConfigFile()
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return !string.IsNullOrWhiteSpace(env)
            ? Path.Combine(env, ".claude.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
    }
    public static string ProjectsDir(string configDir) => Path.Combine(configDir, "projects");
}

public sealed class DetectionService(DetectionEnvironment env)
{
    public const string DesktopPackageFamily = "Claude_pzs8sxrjxfjjc";
    private const string DesktopPublisherSuffix = "__pzs8sxrjxfjjc";

    public DetectionResult Detect() => new(DetectClaudeCode(), DetectDesktop());

    public ClaudeCodeInfo DetectClaudeCode()
    {
        var exe = FindClaudeExecutable();
        var version = exe is null ? null : TryReadNpmVersion();
        return new ClaudeCodeInfo(exe is not null, exe, version, Directory.Exists(env.ConfigDir));
    }

    public ClaudeDesktopInfo DetectDesktop()
    {
        var names = env.MsixPackageNames?.Invoke() ?? [];
        var msix = names
            .Where(n => n.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase)
                        && n.EndsWith(DesktopPublisherSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Split('_'))
            .Where(p => p.Length >= 2 && Version.TryParse(p[1], out _))
            .Select(p => Version.Parse(p[1]))
            .OrderDescending()
            .FirstOrDefault();
        if (msix is not null) return new ClaudeDesktopInfo(true, DesktopInstallKind.Msix, msix.ToString());

        // Package data folder exists even when the registry view is unavailable.
        if (Directory.Exists(Path.Combine(env.LocalAppData, "Packages", DesktopPackageFamily)))
            return new ClaudeDesktopInfo(true, DesktopInstallKind.Msix, null);

        // Legacy Squirrel install: %LOCALAPPDATA%\AnthropicClaude\app-X.Y.Z
        var squirrel = Path.Combine(env.LocalAppData, "AnthropicClaude");
        if (Directory.Exists(squirrel))
        {
            var best = Directory.EnumerateDirectories(squirrel, "app-*")
                .Select(d => Path.GetFileName(d)[4..])
                .Where(v => Version.TryParse(v, out _))
                .Select(Version.Parse)
                .OrderDescending()
                .FirstOrDefault();
            if (best is not null) return new ClaudeDesktopInfo(true, DesktopInstallKind.Squirrel, best.ToString());
        }

        return new ClaudeDesktopInfo(false, DesktopInstallKind.None, null);
    }

    private string? FindClaudeExecutable()
    {
        string[] names = ["claude.exe", "claude.cmd"];
        var dirs = (env.PathVariable ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat([
                Path.Combine(env.UserProfile, ".local", "bin"),
                Path.Combine(env.AppData, "npm"),
                Path.Combine(env.UserProfile, ".claude", "local"),
            ]);

        foreach (var dir in dirs)
        {
            foreach (var name in names)
            {
                string candidate;
                try { candidate = Path.Combine(dir.Trim('"'), name); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private string? TryReadNpmVersion()
    {
        var pkg = Path.Combine(env.AppData, "npm", "node_modules", "@anthropic-ai", "claude-code", "package.json");
        try
        {
            if (!File.Exists(pkg)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pkg));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
