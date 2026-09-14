using UrlShortener.Domain;

namespace UrlShortener.Tests;

public class ShortUrlTests
{
    private static ShortUrl CreateShortUrl(DateTimeOffset? expiresAt, bool isActive = true) => new()
    {
        Code = "abc1234",
        OriginalUrl = "https://example.com",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = expiresAt,
        IsActive = isActive
    };

    [Fact]
    public void IsExpired_WithNoExpiryDate_ReturnsFalse()
    {
        var url = CreateShortUrl(null);
        Assert.False(url.IsExpired(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsExpired_WithFutureExpiry_ReturnsFalse()
    {
        var now = DateTimeOffset.UtcNow;
        var url = CreateShortUrl(now.AddHours(1));
        Assert.False(url.IsExpired(now));
    }

    [Fact]
    public void IsExpired_WithPastExpiry_ReturnsTrue()
    {
        var now = DateTimeOffset.UtcNow;
        var url = CreateShortUrl(now.AddHours(-1));
        Assert.True(url.IsExpired(now));
    }

    [Fact]
    public void IsExpired_WhenExpiryExactlyEqualsNow_ReturnsTrue()
    {
        var now = DateTimeOffset.UtcNow;
        var url = CreateShortUrl(now);
        Assert.True(url.IsExpired(now));
    }

    [Fact]
    public void IsUsable_WhenActiveAndNotExpired_ReturnsTrue()
    {
        var now = DateTimeOffset.UtcNow;
        var url = CreateShortUrl(now.AddHours(1), isActive: true);
        Assert.True(url.IsUsable(now));
    }

    [Fact]
    public void IsUsable_WhenInactive_ReturnsFalseEvenIfNotExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var url = CreateShortUrl(now.AddHours(1), isActive: false);
        Assert.False(url.IsUsable(now));
    }

    [Fact]
    public void IsUsable_WhenExpired_ReturnsFalseEvenIfActive()
    {
        var now = DateTimeOffset.UtcNow;
        var url = CreateShortUrl(now.AddHours(-1), isActive: true);
        Assert.False(url.IsUsable(now));
    }

    [Fact]
    public void IsUsable_WithNoExpiryAndActive_ReturnsTrue()
    {
        var now = DateTimeOffset.UtcNow;
        var url = CreateShortUrl(null, isActive: true);
        Assert.True(url.IsUsable(now));
    }
}
