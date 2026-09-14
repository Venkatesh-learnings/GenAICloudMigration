namespace UrlShortener.Application;

public record CreateShortUrlRequest(
    string OriginalUrl,
    string? CustomAlias,
    DateTimeOffset? ExpiresAt,
    string? IdempotencyKey);

public record CreateShortUrlResult(
    string Code,
    string OriginalUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool WasIdempotentReplay);

public record AnalyticsResult(
    string Code,
    string OriginalUrl,
    int TotalClicks,
    DateTimeOffset? LastClickAt,
    IReadOnlyDictionary<string, int> ClicksByReferrer);

public enum ResolveOutcome
{
    Resolved,
    NotFound,
    ExpiredOrInactive
}

public record ResolveResult(ResolveOutcome Outcome, string? OriginalUrl);
