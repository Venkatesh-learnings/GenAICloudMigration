using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace UrlShortener.Api;

public static class RateLimiting
{
    public const string CreatePolicy = "create-url";
    public const string RedirectPolicy = "redirect";

    public static IServiceCollection AddUrlShortenerRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(CreatePolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ClientKey(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            options.AddPolicy(RedirectPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ClientKey(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 120,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });

        return services;
    }

    private static string ClientKey(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
