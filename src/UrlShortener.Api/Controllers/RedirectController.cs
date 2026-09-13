using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UrlShortener.Application;

namespace UrlShortener.Api.Controllers;

[ApiController]
public class RedirectController(IUrlShortenerService service) : ControllerBase
{
    [HttpGet("/{code:regex(^(?!api|swagger|health).+$)}")]
    [EnableRateLimiting(RateLimiting.RedirectPolicy)]
    public async Task<IActionResult> RedirectToOriginal(string code, CancellationToken ct)
    {
        var result = await service.ResolveAsync(code, ct);

        switch (result.Outcome)
        {
            case ResolveOutcome.Resolved:
                var referrer = Request.Headers.Referer.FirstOrDefault();
                var userAgent = Request.Headers.UserAgent.FirstOrDefault();
                var ipHash = HashIp(HttpContext.Connection.RemoteIpAddress?.ToString());

                await service.RecordClickAsync(code, referrer, userAgent, ipHash, ct);
                return Redirect(result.OriginalUrl!);

            case ResolveOutcome.ExpiredOrInactive:
                return StatusCode(StatusCodes.Status410Gone,
                    new ProblemDetailsResponse("expired", $"Short URL '{code}' has expired or been deactivated."));

            case ResolveOutcome.NotFound:
            default:
                return NotFound(new ProblemDetailsResponse("not_found", $"No short URL found for code '{code}'."));
        }
    }

    private static string? HashIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip))
        {
            return null;
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ip));
        return Convert.ToHexString(bytes)[..16];
    }
}
