using Microsoft.EntityFrameworkCore;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure;

public class ShortUrlRepository(UrlShortenerDbContext dbContext) : IShortUrlRepository
{
    public Task<ShortUrl?> GetByCodeAsync(string code, CancellationToken ct = default) =>
        dbContext.ShortUrls.FirstOrDefaultAsync(s => s.Code == code, ct);

    public Task<ShortUrl?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default) =>
        dbContext.ShortUrls.FirstOrDefaultAsync(s => s.IdempotencyKey == idempotencyKey, ct);

    public Task<bool> ExistsAsync(string code, CancellationToken ct = default) =>
        dbContext.ShortUrls.AnyAsync(s => s.Code == code, ct);

    public async Task AddAsync(ShortUrl shortUrl, CancellationToken ct = default) =>
        await dbContext.ShortUrls.AddAsync(shortUrl, ct);

    public Task UpdateAsync(ShortUrl shortUrl, CancellationToken ct = default)
    {
        dbContext.ShortUrls.Update(shortUrl);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => dbContext.SaveChangesAsync(ct);
}
