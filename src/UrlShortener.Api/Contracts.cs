namespace UrlShortener.Api;

public record CreateShortUrlApiRequest(
    string OriginalUrl,
    string? CustomAlias,
    DateTimeOffset? ExpiresAt);

public record CreateShortUrlApiResponse(
    string Code,
    string ShortUrl,
    string OriginalUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

public record AnalyticsApiResponse(
    string Code,
    string OriginalUrl,
    int TotalClicks,
    DateTimeOffset? LastClickAt,
    IReadOnlyDictionary<string, int> ClicksByReferrer);

public record ProblemDetailsResponse(string Title, string Detail);
