namespace UrlShortener.Tests;

/// <summary>
/// Minimal controllable TimeProvider test double (no dependency on
/// Microsoft.Extensions.TimeProvider.Testing) so tests can deterministically
/// advance "now" to exercise expiry logic.
/// </summary>
public class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public FakeTimeProvider(DateTimeOffset start) => _utcNow = start;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan span) => _utcNow += span;

    public void SetUtcNow(DateTimeOffset now) => _utcNow = now;
}
