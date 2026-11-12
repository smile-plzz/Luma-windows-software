using System.Text.RegularExpressions;

namespace Luma.App;

public static class PathRedactor
{
    private static readonly string CurrentUser = Environment.UserName;

    public static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var result = input;

        // Redact exact current username
        if (!string.IsNullOrWhiteSpace(CurrentUser))
        {
            result = result.Replace(CurrentUser, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
        }

        // Redact Windows user directory patterns (e.g. C:\Users\john\...)
        result = Regex.Replace(result, @"(?i)(C:\\Users\\)[^\\]+(\\)", "$1[REDACTED]$2");

        // Redact Unix home directory patterns (e.g. /home/john/...)
        result = Regex.Replace(result, @"(/home/)[^/]+(/)", "$1[REDACTED]$2");

        // Redact macOS user directory patterns (e.g. /Users/john/...)
        result = Regex.Replace(result, @"(/Users/)[^/]+(/)", "$1[REDACTED]$2");

        return result;
    }
}

