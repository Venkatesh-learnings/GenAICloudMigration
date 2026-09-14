namespace Orchestrator.Cli;

public static class ScenarioLibrary
{
    public static (string Requirement, string CodebaseContext) Get(string scenario) => scenario switch
    {
        "greenfield" =>
        (
            "Build the core URL shortener service from scratch: an endpoint to create a short " +
            "URL from a long URL (optionally with a custom alias and an expiry date), an endpoint " +
            "that redirects a short code to its original URL, and an endpoint that returns click " +
            "analytics (total clicks, last click time, clicks broken down by referrer) for a given code.",
            ""
        ),

        "brownfield" =>
        (
            "Add a bulk-expire operation to the existing URL shortener: given a cutoff date, deactivate " +
            "every short URL created before that date in one call, without breaking the existing " +
            "single-URL expire endpoint or its idempotency guarantees.",
            "Existing service: ASP.NET Core Web API (UrlShortener.Api) over a layered UrlShortener.Domain / " +
            "UrlShortener.Application / UrlShortener.Infrastructure split. UrlShortenerService.ExpireAsync(code) " +
            "already deactivates a single ShortUrl by code via IShortUrlRepository, backed by EF Core over " +
            "SQLite (UrlShortenerDbContext, ShortUrls table keyed by Code with a CreatedAt column). " +
            "DELETE /api/urls/{code} in UrlsController already exposes single-expire. A bulk operation must " +
            "reuse IShortUrlRepository rather than bypass it, and must not lock the ShortUrls table for an " +
            "unbounded scan on a large dataset."
        ),

        "ambiguous" =>
        (
            "Make the URL shortener handle high traffic and be more reliable.",
            "Existing service: ASP.NET Core Web API with SQLite persistence, a fixed-window rate limiter " +
            "on the create and redirect endpoints, and no caching layer."
        ),

        // A fourth scenario beyond the three the assignment requires: it exists to exercise
        // the governance path end to end. Its requirement genuinely touches an auth surface,
        // so SecuritySensitiveChangeRule forces an approval and the conditional SecurityReview
        // stage's entry gate opens (in the other scenarios that stage is correctly Skipped).
        "security" =>
        (
            "Add API key authentication to the URL shortener's create endpoint: callers must present " +
            "a token, tokens are issued per tenant and stored hashed, and an invalid or revoked token " +
            "must be rejected without leaking whether the key ever existed.",
            "Existing service: ASP.NET Core Web API with no authentication on any endpoint today. " +
            "UrlsController exposes POST /api/urls anonymously, and UrlShortenerDbContext has no " +
            "tenant or credential tables."
        ),

        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario.")
    };
}
