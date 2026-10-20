using System.Text.RegularExpressions;

namespace Clotch.Core.Diagnostics;

/// <summary>Strips anything that looks like a credential before text reaches a log or the UI.</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]+")]
    private static partial Regex AnthropicToken();

    [GeneratedRegex(@"Bearer\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(""(?:accessToken|refreshToken)""\s*:\s*"")[^""]*("")", RegexOptions.IgnoreCase)]
    private static partial Regex JsonToken();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var s = JsonToken().Replace(text, "$1[redacted]$2");
        s = BearerToken().Replace(s, "[redacted-auth]");
        s = AnthropicToken().Replace(s, "[redacted-token]");
        return s;
    }
}

/// <summary>Minimal daily-rolling file log under %LOCALAPPDATA%\Clotch\logs. Every line is redacted.</summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static string? _dir;

    public static string Directory => _dir ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clotch", "logs");

    /// <summary>Overrides the log directory (tests).</summary>
    public static void UseDirectory(string dir) => _dir = dir;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {Redactor.Redact(message)}{Environment.NewLine}";
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(Path.Combine(Directory, $"clotch-{DateTime.Now:yyyyMMdd}.log"), line);
            }
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
