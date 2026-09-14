using Microsoft.EntityFrameworkCore;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure;

public class ClickEventRepository(UrlShortenerDbContext dbContext) : IClickEventRepository
{
    public async Task AddAsync(ClickEvent clickEvent, CancellationToken ct = default) =>
        await dbContext.ClickEvents.AddAsync(clickEvent, ct);

    public async Task<IReadOnlyList<ClickEvent>> GetByCodeAsync(string code, CancellationToken ct = default) =>
        await dbContext.ClickEvents.Where(c => c.Code == code)
            .OrderBy(c => c.Id) // Id is monotonically increasing, so this preserves chronological order;
            .ToListAsync(ct);   // SQLite cannot translate ORDER BY over DateTimeOffset directly.

    public Task<int> CountByCodeAsync(string code, CancellationToken ct = default) =>
        dbContext.ClickEvents.CountAsync(c => c.Code == code, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => dbContext.SaveChangesAsync(ct);
}
