namespace AbsPlayer.Core.Api;

public enum ApiErrorKind
{
    InvalidUrl,
    Unauthorized,
    ServerError,
    Decoding,
    Network,
    NotAudiobookshelf,
    WrongCredentials,
}

public sealed class ApiException(ApiErrorKind kind, int? statusCode = null, Exception? inner = null)
    : Exception(Describe(kind, statusCode, inner), inner)
{
    public ApiErrorKind Kind { get; } = kind;
    public int? StatusCode { get; } = statusCode;

    private static string Describe(ApiErrorKind kind, int? code, Exception? inner) => kind switch
    {
        ApiErrorKind.InvalidUrl => "Invalid server URL",
        ApiErrorKind.Unauthorized => "Unauthorized — please log in again",
        ApiErrorKind.ServerError => $"Server error: {code}",
        ApiErrorKind.Decoding => $"Decode error: {inner?.Message}",
        ApiErrorKind.Network => inner?.Message ?? "Network error",
        ApiErrorKind.NotAudiobookshelf => "Couldn't find an Audiobookshelf server at that address",
        ApiErrorKind.WrongCredentials => "Wrong username or password",
        _ => kind.ToString(),
    };
}
