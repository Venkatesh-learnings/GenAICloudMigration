using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Orchestrator.Agents;

public class LlmUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public interface ILlmClient
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct);
}

/// <summary>
/// Thin wrapper over the Anthropic Messages API. Reads ANTHROPIC_API_KEY and
/// (optionally) ANTHROPIC_MODEL from the environment; throws
/// <see cref="LlmUnavailableException"/> for any failure so callers can fall
/// back to a deterministic offline stage implementation instead of the whole
/// run failing because a key wasn't configured or the network hiccuped.
/// </summary>
public class AnthropicLlmClient : ILlmClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly string? _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    private readonly string _model = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-3-5-sonnet-latest";

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new LlmUnavailableException("ANTHROPIC_API_KEY is not configured.");
        }

        var requestBody = JsonSerializer.Serialize(new
        {
            model = _model,
            max_tokens = 1024,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userPrompt } }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await Http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new LlmUnavailableException($"Anthropic API returned {(int)response.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString();
            return text ?? throw new LlmUnavailableException("Anthropic API response had no text content.");
        }
        catch (Exception ex) when (ex is not LlmUnavailableException)
        {
            throw new LlmUnavailableException($"Failed to call Anthropic API: {ex.Message}", ex);
        }
    }
}
