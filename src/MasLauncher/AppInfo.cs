using System.Text.RegularExpressions;

namespace MasLauncher;

/// <summary>
/// The launcher has its own version, independent of the embedded engine: 1.0.7 is the app,
/// 3.12 is the MAS file inside it. This is the only version the UI shows. Keep
/// <c>&lt;Version&gt;</c> in the csproj in step.
/// </summary>
internal static class AppInfo
{
    public static string Version
    {
        get
        {
            var version = typeof(AppInfo).Assembly.GetName().Version;
            if (version is null) return "1.0.7";

            // 1.0.7.0 reads as "1.0.7"; 1.1.0.0 reads as "1.1".
            return version.Build > 0
                ? $"{version.Major}.{version.Minor}.{version.Build}"
                : $"{version.Major}.{version.Minor}";
        }
    }

    /// <summary>Returns a positive number when <paramref name="a"/> is newer than <paramref name="b"/>.</summary>
    public static int Compare(string a, string b)
    {
        static int[] Parts(string value) => value.Split('.')
            .Select(p => int.TryParse(p, out var n) ? n : 0)
            .ToArray();

        var left = Parts(a);
        var right = Parts(b);
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var x = i < left.Length ? left[i] : 0;
            var y = i < right.Length ? right[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>Pulls a dotted version out of a release tag or a file version.</summary>
    public static string? FromTag(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, @"[0-9]+(?:\.[0-9]+)+");
        return match.Success ? match.Value : null;
    }
}
