namespace UrlShortener.Domain;

public interface IShortUrlRepository
{
    Task<ShortUrl?> GetByCodeAsync(string code, CancellationToken ct = default);
    Task<ShortUrl?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);
    Task<bool> ExistsAsync(string code, CancellationToken ct = default);
    Task AddAsync(ShortUrl shortUrl, CancellationToken ct = default);
    Task UpdateAsync(ShortUrl shortUrl, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IClickEventRepository
{
    Task AddAsync(ClickEvent clickEvent, CancellationToken ct = default);
    Task<IReadOnlyList<ClickEvent>> GetByCodeAsync(string code, CancellationToken ct = default);
    Task<int> CountByCodeAsync(string code, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
