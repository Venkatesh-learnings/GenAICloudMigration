namespace UrlShortener.Domain;

public class ClickEvent
{
    public long Id { get; init; }
    public required string Code { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string? Referrer { get; init; }
    public string? UserAgent { get; init; }
    public string? IpHash { get; init; }
}
