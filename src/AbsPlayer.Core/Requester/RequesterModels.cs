namespace AbsPlayer.Core.Requester;

// Shapes returned by Audiobook Requester (https://github.com/druboni/audiobook-requester).

public sealed class RequesterUser
{
    public int Id { get; init; }
    public required string Username { get; init; }
    public string Role { get; init; } = "user";
    public bool IsAdmin => Role == "admin";
}

public sealed class RequesterLogin
{
    public required string Token { get; init; }
    public required RequesterUser User { get; init; }
}

public sealed class RequesterQuota
{
    public bool Unlimited { get; init; }
    public int Limit { get; init; }
    public int Days { get; init; }
    public int Used { get; init; }
    public int? Remaining { get; init; }

    public string? Label => Unlimited ? null : $"{Remaining} of {Limit} requests left this {Days} days";
}

public sealed class RequesterMe
{
    public required RequesterUser User { get; init; }
    public RequesterQuota? Quota { get; init; }
}

public sealed record Rating(double Overall, int Count);

/// <summary>A book from Audible, marked with whether the library already has it.</summary>
public sealed class RequesterBook
{
    public required string Asin { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public List<string> Authors { get; init; } = [];
    public List<string> Narrators { get; init; } = [];
    public string? Series { get; init; }
    public string? SeriesNumber { get; init; }
    public string? Cover { get; init; }
    public double? RuntimeMin { get; init; }
    public string? ReleaseDate { get; init; }
    /// <summary>none, available (in the library), requested, or wanted (Listenarr knows it, no file yet).</summary>
    public string Status { get; init; } = "none";
    public int? RequestId { get; init; }
    public string? RequestStatus { get; init; }
    public Rating? Rating { get; init; }

    public bool CanRequest => Status is "none";
}

/// <summary>Where an in-flight request is in Listenarr.</summary>
public sealed class RequestStage
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public double? Percent { get; init; }
}

public sealed class BookRequest
{
    public int Id { get; init; }
    public int UserId { get; init; }
    public string? Username { get; init; }
    public required string Asin { get; init; }
    public required string Title { get; init; }
    public List<string> Authors { get; init; } = [];
    public string? Cover { get; init; }
    /// <summary>pending, approved, processing, available, declined, failed or removed.</summary>
    public required string Status { get; init; }
    /// <summary>Why it was declined or failed.</summary>
    public string? Message { get; init; }
    /// <summary>The Audiobookshelf item once the book is in the library.</summary>
    public string? AbsItemId { get; init; }
    public RequestStage? Stage { get; init; }
    public string? Note { get; init; }
    public string? CreatedAt { get; init; }

    public bool IsPending => Status == "pending";
}
