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

        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario.")
    };
}
