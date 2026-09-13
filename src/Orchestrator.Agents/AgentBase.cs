using System.Text.Json;
using Orchestrator.Core;

namespace Orchestrator.Agents;

public abstract class AgentBase(ILlmClient llm)
{
    protected readonly ILlmClient Llm = llm;

    /// <summary>
    /// Calls the LLM and returns (text, usedLlm: true) on success; on any
    /// <see cref="LlmUnavailableException"/> (missing key, network failure,
    /// non-2xx) falls back to a deterministic offline response instead of
    /// failing the whole stage, and reports usedLlm: false so callers can
    /// note the degradation in the stage rationale.
    /// </summary>
    protected async Task<(string Text, bool UsedLlm)> CompleteWithFallbackAsync(
        string systemPrompt, string userPrompt, Func<string> offlineFallback, CancellationToken ct)
    {
        try
        {
            var text = await Llm.CompleteAsync(systemPrompt, userPrompt, ct);
            return (text, true);
        }
        catch (LlmUnavailableException)
        {
            return (offlineFallback(), false);
        }
    }

    /// <summary>
    /// Best-effort JSON extraction: LLMs occasionally wrap JSON in prose or
    /// code fences even when asked for raw JSON. Finds the outermost {...}
    /// span and parses that; returns null (never throws) if nothing parses,
    /// so a malformed generation degrades gracefully instead of crashing the run.
    /// </summary>
    protected static JsonDocument? TryParseLenientJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(text[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    protected static string GetString(JsonElement element, string property, string fallback = "") =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    protected static IReadOnlyList<string> GetStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }

    protected static string Joined(IReadOnlyList<string> items) => items.Count == 0 ? "(none)" : string.Join("; ", items);
}
