using System.Net.Http.Json;
using System.Text.Json;

namespace AbsPlayer.Core;

/// <summary>What an ISBN belongs to.</summary>
public sealed record IsbnInfo(string Isbn, string Title, List<string> Authors)
{
    /// <summary>"The Martian by Andy Weir".</summary>
    public string Description => Authors.Count > 0 ? $"{Title} by {string.Join(", ", Authors)}" : Title;

    /// <summary>The title without a subtitle ("The Martian: A Novel" → "The Martian"), for searching the library.</summary>
    public string ShortTitle => Title.Split(':')[0].Trim();
}

/// <summary>
/// ISBN search. Audiobookshelf only matches ISBNs stored on its books (often none), so an ISBN is looked up
/// on Open Library (free, no key) and the library is searched for the book's title. Same rules as Audiobook Requester.
/// </summary>
public sealed class IsbnLookup(HttpClient http, string baseUrl = "https://openlibrary.org")
{
    private readonly Dictionary<string, IsbnInfo?> _cache = [];

    /// <summary>The ISBN in what was typed (hyphens and spaces allowed, check digit verified), or null.</summary>
    public static string? Parse(string? text)
    {
        var s = new string((text ?? "").Where(c => c is not ('-' or ' ')).ToArray()).ToUpperInvariant();
        if (s.Length == 10 && s[..9].All(char.IsAsciiDigit) && (char.IsAsciiDigit(s[9]) || s[9] == 'X'))
        {
            var sum = 0;
            for (var i = 0; i < 10; i++) sum += (s[i] == 'X' ? 10 : s[i] - '0') * (10 - i);
            return sum % 11 == 0 ? s : null;
        }
        if (s.Length == 13 && s.All(char.IsAsciiDigit) && (s.StartsWith("978") || s.StartsWith("979")))
        {
            var sum = 0;
            for (var i = 0; i < 13; i++) sum += (s[i] - '0') * (i % 2 == 1 ? 3 : 1);
            return sum % 10 == 0 ? s : null;
        }
        return null;
    }

    /// <summary>The book for an ISBN, or null when Open Library doesn't know it. Throws HttpRequestException when it can't be reached.</summary>
    public async Task<IsbnInfo?> Lookup(string isbn)
    {
        lock (_cache) if (_cache.TryGetValue(isbn, out var hit)) return hit;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Dictionary<string, JsonElement>? data;
        try
        {
            data = await http.GetFromJsonAsync<Dictionary<string, JsonElement>>(
                $"{baseUrl.TrimEnd('/')}/api/books?bibkeys=ISBN:{isbn}&format=json&jscmd=data", cts.Token);
        }
        catch (Exception e) when (e is TaskCanceledException or JsonException)
        {
            throw new HttpRequestException("Couldn't reach Open Library to look up the ISBN", e);
        }
        IsbnInfo? info = null;
        if (data?.Values.FirstOrDefault() is { ValueKind: JsonValueKind.Object } book
            && book.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } title)
        {
            var authors = book.TryGetProperty("authors", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>().Distinct().ToList()
                : [];
            info = new IsbnInfo(isbn, title, authors);
        }
        lock (_cache) _cache[isbn] = info;
        return info;
    }
}
