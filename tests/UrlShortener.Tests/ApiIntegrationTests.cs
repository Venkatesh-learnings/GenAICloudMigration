using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using UrlShortener.Api;

namespace UrlShortener.Tests;

public class ApiIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ApiIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateNonRedirectingClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string UniqueAlias(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Fact]
    public async Task FullFlow_Create_Redirect_Analytics_Works()
    {
        var client = _factory.CreateClient();
        var redirectClient = CreateNonRedirectingClient();

        var createResponse = await client.PostAsJsonAsync("/api/urls",
            new CreateShortUrlApiRequest("https://example.com/full-flow", null, null));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<CreateShortUrlApiResponse>();
        Assert.NotNull(created);
        Assert.Equal(7, created!.Code.Length);
        Assert.Equal("https://example.com/full-flow", created.OriginalUrl);

        var redirectResponse = await redirectClient.GetAsync($"/{created.Code}");
        Assert.Equal(HttpStatusCode.Redirect, redirectResponse.StatusCode);
        Assert.Equal("https://example.com/full-flow", redirectResponse.Headers.Location?.ToString());

        var analyticsResponse = await client.GetAsync($"/api/urls/{created.Code}/analytics");
        Assert.Equal(HttpStatusCode.OK, analyticsResponse.StatusCode);

        var analytics = await analyticsResponse.Content.ReadFromJsonAsync<AnalyticsApiResponse>();
        Assert.NotNull(analytics);
        Assert.Equal(1, analytics!.TotalClicks);
        Assert.Equal("https://example.com/full-flow", analytics.OriginalUrl);
    }

    [Fact]
    public async Task Create_WithCustomAlias_UsesProvidedAlias()
    {
        var client = _factory.CreateClient();
        var alias = UniqueAlias("myalias");

        var response = await client.PostAsJsonAsync("/api/urls",
            new CreateShortUrlApiRequest("https://example.com/custom-alias", alias, null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateShortUrlApiResponse>();
        Assert.Equal(alias, body!.Code);
    }

    [Fact]
    public async Task Create_WithDuplicateAlias_ReturnsConflict()
    {
        var client = _factory.CreateClient();
        var alias = UniqueAlias("dup");

        var first = await client.PostAsJsonAsync("/api/urls",
            new CreateShortUrlApiRequest("https://example.com/dup-1", alias, null));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/urls",
            new CreateShortUrlApiRequest("https://example.com/dup-2", alias, null));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Create_WithInvalidUrl_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/urls",
            new CreateShortUrlApiRequest("not-a-valid-url", null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Redirect_UnknownCode_ReturnsNotFound()
    {
        var client = CreateNonRedirectingClient();

        var response = await client.GetAsync($"/does-not-exist-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Analytics_UnknownCode_ReturnsNotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/urls/does-not-exist-{Guid.NewGuid():N}/analytics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Redirect_ExpiredUrl_ReturnsGone()
    {
        var client = _factory.CreateClient();
        var redirectClient = CreateNonRedirectingClient();

        var createResponse = await client.PostAsJsonAsync("/api/urls",
            new CreateShortUrlApiRequest("https://example.com/expired", null, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CreateShortUrlApiResponse>();

        var redirectResponse = await redirectClient.GetAsync($"/{created!.Code}");

        Assert.Equal(HttpStatusCode.Gone, redirectResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_ExpiresUrl_SoSubsequentRedirectReturnsGone()
    {
        var client = _factory.CreateClient();
        var redirectClient = CreateNonRedirectingClient();

        var createResponse = await client.PostAsJsonAsync("/api/urls",
            new CreateShortUrlApiRequest("https://example.com/to-delete", null, null));
        var created = await createResponse.Content.ReadFromJsonAsync<CreateShortUrlApiResponse>();

        var deleteResponse = await client.DeleteAsync($"/api/urls/{created!.Code}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var redirectResponse = await redirectClient.GetAsync($"/{created.Code}");
        Assert.Equal(HttpStatusCode.Gone, redirectResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_UnknownCode_ReturnsNotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.DeleteAsync($"/api/urls/does-not-exist-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithIdempotencyKey_ReplayReturnsSameCode()
    {
        var client = _factory.CreateClient();
        var idempotencyKey = Guid.NewGuid().ToString();

        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/urls")
        {
            Content = JsonContent.Create(new CreateShortUrlApiRequest("https://example.com/idempotent", null, null))
        };
        request1.Headers.Add("Idempotency-Key", idempotencyKey);
        var response1 = await client.SendAsync(request1);
        Assert.Equal(HttpStatusCode.Created, response1.StatusCode);
        var body1 = await response1.Content.ReadFromJsonAsync<CreateShortUrlApiResponse>();

        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/urls")
        {
            Content = JsonContent.Create(
                new CreateShortUrlApiRequest("https://example.com/idempotent-second-call", null, null))
        };
        request2.Headers.Add("Idempotency-Key", idempotencyKey);
        var response2 = await client.SendAsync(request2);
        Assert.Equal(HttpStatusCode.Created, response2.StatusCode);
        var body2 = await response2.Content.ReadFromJsonAsync<CreateShortUrlApiResponse>();

        Assert.Equal(body1!.Code, body2!.Code);
        Assert.Equal(body1.OriginalUrl, body2.OriginalUrl);
    }

    [Fact]
    public async Task Create_RateLimit_TripsAfterTooManyRapidRequests()
    {
        // Uses its own fresh factory (and thus its own rate limiter state) so this doesn't
        // interfere with, or get interfered with by, the other tests sharing the class fixture.
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();

        var statusCodes = new List<HttpStatusCode>();
        for (var i = 0; i < 25; i++)
        {
            var response = await client.PostAsJsonAsync("/api/urls",
                new CreateShortUrlApiRequest($"https://example.com/rate-limit-{i}", null, null));
            statusCodes.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statusCodes);
    }
}
