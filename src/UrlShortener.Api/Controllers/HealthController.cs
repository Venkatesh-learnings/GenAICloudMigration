using Microsoft.AspNetCore.Mvc;

namespace UrlShortener.Api.Controllers;

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "healthy", timestampUtc = DateTimeOffset.UtcNow });
}
