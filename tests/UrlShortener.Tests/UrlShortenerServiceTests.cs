using Microsoft.EntityFrameworkCore;
using UrlShortener.Application;
using UrlShortener.Domain;
using UrlShortener.Infrastructure;

namespace UrlShortener.Tests;

public class UrlShortenerServiceTests : IDisposable
{
    private readonly UrlShortenerDbContext _dbContext;
    private readonly FakeTimeProvider _timeProvider;
    private readonly UrlShortenerService _service;

    public UrlShortenerServiceTests()
    {
        var options = new DbContextOptionsBuilder<UrlShortenerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new UrlShortenerDbContext(options);
        var shortUrlRepository = new ShortUrlRepository(_dbContext);
        var clickEventRepository = new ClickEventRepository(_dbContext);
        var codeGenerator = new ShortCodeGenerator();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        _service = new UrlShortenerService(shortUrlRepository, clickEventRepository, codeGenerator, _timeProvider);
    }

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public async Task CreateAsync_WithoutCustomAlias_GeneratesSevenCharacterCode()
    {
        var result = await _service.CreateAsync(new CreateShortUrlRequest("https://example.com", null, null, null));

        Assert.Equal(7, result.Code.Length);
        Assert.False(result.WasIdempotentReplay);
        Assert.Equal("https://example.com", result.OriginalUrl);

        var stored = await _dbContext.ShortUrls.FirstOrDefaultAsync(s => s.Code == result.Code);
        Assert.NotNull(stored);
    }

    [Fact]
    public async Task CreateAsync_WithCustomAlias_UsesProvidedCode()
    {
        var result = await _service.CreateAsync(
            new CreateShortUrlRequest("https://example.com/alias-target", "my-custom-alias", null, null));

        Assert.Equal("my-custom-alias", result.Code);
    }

    [Fact]
    public async Task CreateAsync_WithDuplicateCustomAlias_ThrowsAliasAlreadyTakenException()
    {
        await _service.CreateAsync(new CreateShortUrlRequest("https://example.com/a", "taken-alias", null, null));

        await Assert.ThrowsAsync<AliasAlreadyTakenException>(() =>
            _service.CreateAsync(new CreateShortUrlRequest("https://example.com/b", "taken-alias", null, null)));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/file")]
    [InlineData("/relative/path")]
    [InlineData("")]
    public async Task CreateAsync_WithInvalidUrl_ThrowsInvalidUrlException(string url)
    {
        await Assert.ThrowsAsync<InvalidUrlException>(() =>
            _service.CreateAsync(new CreateShortUrlRequest(url, null, null, null)));
    }

    [Fact]
    public async Task CreateAsync_WithSameIdempotencyKey_ReturnsSameShortUrl_WithoutDuplicating()
    {
        var result1 = await _service.CreateAsync(
            new CreateShortUrlRequest("https://example.com/first", null, null, "idem-key-1"));

        var result2 = await _service.CreateAsync(
            new CreateShortUrlRequest("https://example.com/second", null, null, "idem-key-1"));

        Assert.False(result1.WasIdempotentReplay);
        Assert.True(result2.WasIdempotentReplay);
        Assert.Equal(result1.Code, result2.Code);
        Assert.Equal("https://example.com/first", result2.OriginalUrl);

        var count = await _dbContext.ShortUrls.CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ResolveAsync_ActiveUrl_ReturnsResolvedWithOriginalUrl()
    {
        var created = await _service.CreateAsync(
            new CreateShortUrlRequest("https://example.com/active", null, null, null));

        var result = await _service.ResolveAsync(created.Code);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal("https://example.com/active", result.OriginalUrl);
    }

    [Fact]
    public async Task ResolveAsync_ExpiredUrl_ReturnsExpiredOrInactive()
    {
        var created = await _service.CreateAsync(new CreateShortUrlRequest(
            "https://example.com/expiring", null, _timeProvider.GetUtcNow().AddMinutes(5), null));

        _timeProvider.Advance(TimeSpan.FromMinutes(10));

        var result = await _service.ResolveAsync(created.Code);

        Assert.Equal(ResolveOutcome.ExpiredOrInactive, result.Outcome);
        Assert.Null(result.OriginalUrl);
    }

    [Fact]
    public async Task ResolveAsync_NonexistentCode_ReturnsNotFound()
    {
        var result = await _service.ResolveAsync("does-not-exist");

        Assert.Equal(ResolveOutcome.NotFound, result.Outcome);
        Assert.Null(result.OriginalUrl);
    }

    [Fact]
    public async Task GetAnalyticsAsync_AggregatesClicksByReferrerAndTracksLastClick()
    {
        var created = await _service.CreateAsync(
            new CreateShortUrlRequest("https://example.com/tracked", null, null, null));

        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        await _service.RecordClickAsync(created.Code, "https://google.com", "agent-1", "hash-1");

        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        await _service.RecordClickAsync(created.Code, "https://google.com", "agent-2", "hash-2");

        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        await _service.RecordClickAsync(created.Code, null, "agent-3", "hash-3");

        _timeProvider.Advance(TimeSpan.FromMinutes(1));
        var lastClickTime = _timeProvider.GetUtcNow();
        await _service.RecordClickAsync(created.Code, "", "agent-4", "hash-4");

        var analytics = await _service.GetAnalyticsAsync(created.Code);

        Assert.NotNull(analytics);
        Assert.Equal(4, analytics!.TotalClicks);
        Assert.Equal(2, analytics.ClicksByReferrer["https://google.com"]);
        Assert.Equal(2, analytics.ClicksByReferrer["(direct)"]);
        Assert.Equal(lastClickTime, analytics.LastClickAt);
    }

    [Fact]
    public async Task GetAnalyticsAsync_WithNoClicks_ReturnsZeroTotalsAndNullLastClick()
    {
        var created = await _service.CreateAsync(
            new CreateShortUrlRequest("https://example.com/no-clicks", null, null, null));

        var analytics = await _service.GetAnalyticsAsync(created.Code);

        Assert.NotNull(analytics);
        Assert.Equal(0, analytics!.TotalClicks);
        Assert.Null(analytics.LastClickAt);
        Assert.Empty(analytics.ClicksByReferrer);
    }

    [Fact]
    public async Task GetAnalyticsAsync_NonexistentCode_ReturnsNull()
    {
        var analytics = await _service.GetAnalyticsAsync("does-not-exist");

        Assert.Null(analytics);
    }

    [Fact]
    public async Task ExpireAsync_DeactivatesUrl_SoItCanNoLongerResolve()
    {
        var created = await _service.CreateAsync(
            new CreateShortUrlRequest("https://example.com/to-expire", null, null, null));

        var wasExpired = await _service.ExpireAsync(created.Code);

        Assert.True(wasExpired);

        var result = await _service.ResolveAsync(created.Code);
        Assert.Equal(ResolveOutcome.ExpiredOrInactive, result.Outcome);
    }

    [Fact]
    public async Task ExpireAsync_NonexistentCode_ReturnsFalse()
    {
        var wasExpired = await _service.ExpireAsync("does-not-exist");

        Assert.False(wasExpired);
    }
}
