using UrlShortener.Domain;

namespace UrlShortener.Application;

public interface IUrlShortenerService
{
    Task<CreateShortUrlResult> CreateAsync(CreateShortUrlRequest request, CancellationToken ct = default);
    Task<ResolveResult> ResolveAsync(string code, CancellationToken ct = default);
    Task RecordClickAsync(string code, string? referrer, string? userAgent, string? ipHash, CancellationToken ct = default);
    Task<AnalyticsResult?> GetAnalyticsAsync(string code, CancellationToken ct = default);
    Task<bool> ExpireAsync(string code, CancellationToken ct = default);
}

public class UrlShortenerService(
    IShortUrlRepository shortUrlRepository,
    IClickEventRepository clickEventRepository,
    IShortCodeGenerator codeGenerator,
    TimeProvider timeProvider) : IUrlShortenerService
{
    private const int MaxCodeGenerationAttempts = 5;
    private static readonly string[] AllowedSchemes = ["http", "https"];

    public async Task<CreateShortUrlResult> CreateAsync(CreateShortUrlRequest request, CancellationToken ct = default)
    {
        if (!IsValidAbsoluteUrl(request.OriginalUrl))
        {
            throw new InvalidUrlException(request.OriginalUrl);
        }

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await shortUrlRepository.GetByIdempotencyKeyAsync(request.IdempotencyKey, ct);
            if (existing is not null)
            {
                return ToResult(existing, wasIdempotentReplay: true);
            }
        }

        var now = timeProvider.GetUtcNow();
        string code;

        if (!string.IsNullOrWhiteSpace(request.CustomAlias))
        {
            code = request.CustomAlias.Trim();
            if (await shortUrlRepository.ExistsAsync(code, ct))
            {
                throw new AliasAlreadyTakenException(code);
            }
        }
        else
        {
            code = await GenerateUniqueCodeAsync(ct);
        }

        var shortUrl = new ShortUrl
        {
            Code = code,
            OriginalUrl = request.OriginalUrl,
            IdempotencyKey = request.IdempotencyKey,
            CreatedAt = now,
            ExpiresAt = request.ExpiresAt,
            IsActive = true
        };

        await shortUrlRepository.AddAsync(shortUrl, ct);
        await shortUrlRepository.SaveChangesAsync(ct);

        return ToResult(shortUrl, wasIdempotentReplay: false);
    }

    public async Task<ResolveResult> ResolveAsync(string code, CancellationToken ct = default)
    {
        var shortUrl = await shortUrlRepository.GetByCodeAsync(code, ct);
        if (shortUrl is null)
        {
            return new ResolveResult(ResolveOutcome.NotFound, null);
        }

        if (!shortUrl.IsUsable(timeProvider.GetUtcNow()))
        {
            return new ResolveResult(ResolveOutcome.ExpiredOrInactive, null);
        }

        return new ResolveResult(ResolveOutcome.Resolved, shortUrl.OriginalUrl);
    }

    public async Task RecordClickAsync(string code, string? referrer, string? userAgent, string? ipHash, CancellationToken ct = default)
    {
        await clickEventRepository.AddAsync(new ClickEvent
        {
            Code = code,
            OccurredAt = timeProvider.GetUtcNow(),
            Referrer = referrer,
            UserAgent = userAgent,
            IpHash = ipHash
        }, ct);
        await clickEventRepository.SaveChangesAsync(ct);
    }

    public async Task<AnalyticsResult?> GetAnalyticsAsync(string code, CancellationToken ct = default)
    {
        var shortUrl = await shortUrlRepository.GetByCodeAsync(code, ct);
        if (shortUrl is null)
        {
            return null;
        }

        var clicks = await clickEventRepository.GetByCodeAsync(code, ct);
        var byReferrer = clicks
            .GroupBy(c => string.IsNullOrWhiteSpace(c.Referrer) ? "(direct)" : c.Referrer!)
            .ToDictionary(g => g.Key, g => g.Count());

        return new AnalyticsResult(
            code,
            shortUrl.OriginalUrl,
            clicks.Count,
            clicks.Count > 0 ? clicks.Max(c => c.OccurredAt) : null,
            byReferrer);
    }

    public async Task<bool> ExpireAsync(string code, CancellationToken ct = default)
    {
        var shortUrl = await shortUrlRepository.GetByCodeAsync(code, ct);
        if (shortUrl is null)
        {
            return false;
        }

        shortUrl.IsActive = false;
        await shortUrlRepository.UpdateAsync(shortUrl, ct);
        await shortUrlRepository.SaveChangesAsync(ct);
        return true;
    }

    private async Task<string> GenerateUniqueCodeAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxCodeGenerationAttempts; attempt++)
        {
            var candidate = codeGenerator.Generate();
            if (!await shortUrlRepository.ExistsAsync(candidate, ct))
            {
                return candidate;
            }
        }

        throw new ShortCodeExhaustionException();
    }

    private static bool IsValidAbsoluteUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && AllowedSchemes.Contains(parsed.Scheme);

    private static CreateShortUrlResult ToResult(ShortUrl shortUrl, bool wasIdempotentReplay) => new(
        shortUrl.Code,
        shortUrl.OriginalUrl,
        shortUrl.CreatedAt,
        shortUrl.ExpiresAt,
        wasIdempotentReplay);
}
