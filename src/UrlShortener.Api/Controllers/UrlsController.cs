using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UrlShortener.Application;

namespace UrlShortener.Api.Controllers;

[ApiController]
[Route("api/urls")]
public class UrlsController(IUrlShortenerService service, ILogger<UrlsController> logger) : ControllerBase
{
    [HttpPost]
    [EnableRateLimiting(RateLimiting.CreatePolicy)]
    public async Task<IActionResult> Create([FromBody] CreateShortUrlApiRequest request, CancellationToken ct)
    {
        var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault();

        try
        {
            var result = await service.CreateAsync(
                new CreateShortUrlRequest(request.OriginalUrl, request.CustomAlias, request.ExpiresAt, idempotencyKey),
                ct);

            var response = new CreateShortUrlApiResponse(
                result.Code,
                BuildShortUrl(result.Code),
                result.OriginalUrl,
                result.CreatedAt,
                result.ExpiresAt);

            return CreatedAtAction(nameof(GetAnalytics), new { code = result.Code }, response);
        }
        catch (InvalidUrlException ex)
        {
            return BadRequest(new ProblemDetailsResponse("invalid_url", ex.Message));
        }
        catch (AliasAlreadyTakenException ex)
        {
            return Conflict(new ProblemDetailsResponse("alias_taken", ex.Message));
        }
        catch (ShortCodeExhaustionException ex)
        {
            logger.LogError(ex, "Short code generation exhausted");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ProblemDetailsResponse("code_space_exhausted", ex.Message));
        }
    }

    [HttpGet("{code}/analytics")]
    public async Task<IActionResult> GetAnalytics(string code, CancellationToken ct)
    {
        var analytics = await service.GetAnalyticsAsync(code, ct);
        if (analytics is null)
        {
            return NotFound(new ProblemDetailsResponse("not_found", $"No short URL found for code '{code}'."));
        }

        return Ok(new AnalyticsApiResponse(
            analytics.Code,
            analytics.OriginalUrl,
            analytics.TotalClicks,
            analytics.LastClickAt,
            analytics.ClicksByReferrer));
    }

    [HttpDelete("{code}")]
    public async Task<IActionResult> Expire(string code, CancellationToken ct)
    {
        var expired = await service.ExpireAsync(code, ct);
        return expired
            ? NoContent()
            : NotFound(new ProblemDetailsResponse("not_found", $"No short URL found for code '{code}'."));
    }

    private string BuildShortUrl(string code) =>
        $"{Request.Scheme}://{Request.Host}/{code}";
}
