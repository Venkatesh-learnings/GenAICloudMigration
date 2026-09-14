namespace UrlShortener.Domain;

public class ShortUrl
{
    public required string Code { get; init; }
    public required string OriginalUrl { get; init; }
    public string? IdempotencyKey { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsActive { get; set; } = true;

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is not null && ExpiresAt <= now;

    public bool IsUsable(DateTimeOffset now) => IsActive && !IsExpired(now);
}
