using System.Text.Json;
using AbsPlayer.Core.Models;

namespace AbsPlayer.Core.Tests;

public class ModelTests
{
    /// <summary>Fixtures are written with single quotes for readability.</summary>
    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json.Replace('\'', '"'), AbsJson.Options)!;

    private static string Item(string id, string title, string extra = "", string metaExtra = "") =>
        "{'id':'" + id + "','media':{'metadata':{'title':'" + title + "'" + metaExtra + "}}" + extra + "}";

    [Fact]
    public void Series_as_single_object_gives_sequence()
    {
        var item = Parse<LibraryItem>(Item("a", "T", metaExtra: ",'series':{'id':'s','name':'S','sequence':'2'}"));
        Assert.Equal("2", item.Media.Metadata.SeriesSequence);
    }

    [Fact]
    public void Series_as_array_gives_first_sequence()
    {
        var item = Parse<LibraryItem>(Item("a", "T", metaExtra: ",'series':[{'id':'s','name':'S','sequence':'3.5'},{'sequence':'9'}]"));
        Assert.Equal("3.5", item.Media.Metadata.SeriesSequence);
    }

    [Theory]
    [InlineData(",'series':null")]
    [InlineData(",'series':'weird'")]
    [InlineData("")]
    public void Series_missing_or_odd_is_null_and_item_still_parses(string metaExtra)
    {
        var item = Parse<LibraryItem>(Item("a", "T", metaExtra: metaExtra));
        Assert.Null(item.Media.Metadata.SeriesSequence);
    }

    [Fact]
    public void Sequence_prefers_item_field_then_metadata_then_series_name_then_title()
    {
        Assert.Equal("7", Parse<LibraryItem>(Item("a", "T", extra: ",'seriesSequence':'7'")).Sequence("S"));
        Assert.Equal("4", Parse<LibraryItem>(Item("a", "T", metaExtra: ",'seriesName':'Other #1, Dungeon Crawler Carl #4'")).Sequence("Dungeon Crawler Carl"));
        Assert.Equal("3", Parse<LibraryItem>(Item("a", "Dungeon Crawler Carl, Book 3")).Sequence("X"));
        Assert.Null(Parse<LibraryItem>(Item("a", "Plain")).Sequence("X"));
    }

    [Fact]
    public void In_progress_list_skips_items_that_do_not_parse()
    {
        var r = Parse<InProgressResponse>("{'libraryItems':[" + Item("a", "Good")
            + ",{'id':'pod','media':{'episodes':[]}},{'id':'b','media':{'metadata':{'title':null}}},"
            + Item("c", "Also good") + "]}");
        Assert.Equal(["a", "c"], r.LibraryItems.Select(i => i.Id));
    }

    [Fact]
    public void Series_books_are_sorted_by_sequence_with_unknown_last()
    {
        var s = Parse<BookSeries>("{'id':'s','name':'Saga','books':["
            + Item("x", "No number") + ","
            + Item("b", "Two", extra: ",'seriesSequence':'2'") + ","
            + Item("a", "One", extra: ",'seriesSequence':'1'") + ","
            + Item("h", "Half", extra: ",'seriesSequence':'1.5'") + "]}");
        Assert.Equal(["a", "h", "b", "x"], s.Books.Select(b => b.Id));
    }

    [Fact]
    public void Search_response_is_flattened()
    {
        var raw = Parse<SearchResponse.Raw>(
            "{'book':[{'libraryItem':" + Item("a", "A") + "},{'libraryItem':{'bad':1}}],"
            + "'series':[{'series':{'id':'s1','name':'S'},'books':["
            + Item("b", "B", extra: ",'seriesSequence':'2'") + "," + Item("c", "C", extra: ",'seriesSequence':'1'") + "]}],"
            + "'authors':[{'id':'au','name':'Author','numBooks':3}],'narrators':[],'tags':[]}");
        var r = raw.ToResponse();
        Assert.Equal(["a"], r.Books.Select(b => b.Id));
        Assert.Equal(["c", "b"], r.Series.Single().Books.Select(b => b.Id));
        Assert.Equal("Author", r.Authors.Single().Name);
        Assert.False(r.IsEmpty);
    }

    [Fact]
    public void A_bad_chapter_doesnt_make_the_book_unplayable()
    {
        var s = Parse<PlaybackSessionResponse>("{'id':'s','libraryItemId':'b','duration':10,'currentTime':0,"
            + "'audioTracks':[{'index':1,'startOffset':0,'duration':10,'contentUrl':'/f'}],"
            + "'chapters':[{'id':0,'start':0,'end':5,'title':'One'},{'id':1,'start':5,'end':10,'title':null}]}");
        Assert.Single(s.AudioTracks);
        Assert.Equal(["One"], s.Chapters.Select(c => c.Title));
    }

    [Fact]
    public void Requests_serialize_camel_case()
    {
        var json = JsonSerializer.Serialize(new PlayItemRequest
        {
            DeviceInfo = new DeviceInfo { DeviceId = "d", ClientName = "c", ClientVersion = "1", Model = "m" },
        }, AbsJson.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("forceDirectPlay").GetBoolean());
        Assert.Equal("d", doc.RootElement.GetProperty("deviceInfo").GetProperty("deviceId").GetString());
        Assert.Contains("audio/x-m4b", doc.RootElement.GetProperty("supportedMimeTypes").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Format_helpers_match_the_apple_app()
    {
        Assert.Equal("1:02:03", Format.Duration(3723.9));
        Assert.Equal("4:05", Format.Duration(245));
        Assert.Equal("0:00", Format.Duration(-3));
        Assert.Equal("5h 20m", Format.Remaining(5 * 3600 + 20 * 60 + 59));
        Assert.Equal("12m", Format.Remaining(720));
        Assert.Equal("40s", Format.Remaining(40));
        Assert.Equal("1.25x", Format.Speed(1.25));
        Assert.Equal("2x", Format.Speed(2));
        Assert.Equal("Hi there", Format.StripHtml("<p>Hi <b>there</b></p>"));
        Assert.Equal("229 MB", Format.Bytes(229_290_413));
        Assert.Equal("1.4 GB", Format.Bytes(1_400_000_000));
        Assert.Equal("512 bytes", Format.Bytes(512));
        Assert.Equal("under a minute", Format.Estimate(20));
        Assert.Equal("about 3 min", Format.Estimate(150));
        Assert.Equal("about 1 hr 20 min", Format.Estimate(4790));
        Assert.Equal("about 2 hr", Format.Estimate(7190));

        var chapters = new List<Chapter> { new(0, 0, 100, "Intro"), new(1, 100, 200, "Middle"), new(2, 200, 300, "End") };
        var p = new MediaProgressResponse { LibraryItemId = "a", CurrentTime = 150, Progress = 0.5 };
        Assert.Equal("Chapter 2 of 3: Middle · Left off at 2:30 (50%)", Format.ResumeDescription(p, chapters));
        Assert.Null(Format.ResumeDescription(new MediaProgressResponse { LibraryItemId = "a", CurrentTime = 3 }, chapters));
    }
}
