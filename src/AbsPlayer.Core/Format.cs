using System.Globalization;
using System.Text.RegularExpressions;
using AbsPlayer.Core.Models;

namespace AbsPlayer.Core;

public static partial class Format
{
    public static string Duration(double seconds)
    {
        var t = (int)Math.Max(seconds, 0);
        int h = t / 3600, m = t % 3600 / 60, s = t % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    /// <summary>"5h 20m" style, for remaining-time labels.</summary>
    public static string Remaining(double seconds)
    {
        var t = (int)Math.Max(seconds, 0);
        int h = t / 3600, m = t % 3600 / 60;
        if (h > 0) return $"{h}h {m}m";
        if (m > 0) return $"{m}m";
        return $"{t}s";
    }

    public static string Speed(double s) => s.ToString("0.##", CultureInfo.InvariantCulture) + "x";

    public static string StripHtml(string s) => HtmlTag().Replace(s, "");

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTag();

    /// <summary>"Chapter 3 of 20: Title · Left off at 1:02:03 (12%)", or null if the book isn't started.</summary>
    public static string? ResumeDescription(MediaProgressResponse? p, IReadOnlyList<Chapter> chapters)
    {
        if (p is null || p.CurrentTime <= 5 || p.IsFinished) return null;
        var text = $"Left off at {Duration(p.CurrentTime)} ({(int)(p.Progress * 100)}%)";
        var i = -1;
        for (var j = 0; j < chapters.Count; j++)
            if (p.CurrentTime >= chapters[j].Start) i = j;
        if (i >= 0) text = $"Chapter {i + 1} of {chapters.Count}: {chapters[i].Title} · " + text;
        return text;
    }
}
