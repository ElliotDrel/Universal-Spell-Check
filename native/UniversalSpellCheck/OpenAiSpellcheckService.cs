using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UniversalSpellCheck;

internal sealed class OpenAiSpellcheckService : IDisposable
{
    private const string Endpoint = "https://api.openai.com/v1/responses";
    private const string Model = "gpt-4.1";
    public const string PromptInstruction =
        "Correct spelling and grammar in the selected text. Preserve the user's wording, line breaks, and formatting as much as possible. Return only the corrected replacement text. Do not explain the changes.";

    private readonly SettingsStore _settingsStore;
    private readonly DiagnosticsLogger _logger;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public OpenAiSpellcheckService(SettingsStore settingsStore, DiagnosticsLogger logger)
    {
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public async Task<SpellcheckResult> SpellcheckAsync(string inputText)
    {
        var apiKey = _settingsStore.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return SpellcheckResult.Fail(SpellcheckErrorCodes.MissingApiKey, "Missing OpenAI API key.", 0, 0, null);
        }

        var stopwatch = Stopwatch.StartNew();
        const int maxAttempts = 2;
        SpellcheckResult? lastFailure = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var result = await TrySpellcheckOnceAsync(inputText, apiKey, stopwatch, attempt);
            if (result.Success)
            {
                return result;
            }

            lastFailure = result;
            if (!ShouldRetry(result.StatusCode) || attempt == maxAttempts)
            {
                return result;
            }

            _logger.Log(
                "request_retrying " +
                $"attempt={attempt} status_code={result.StatusCode ?? 0} " +
                $"error_code={result.ErrorCode}");
            await Task.Delay(500);
        }

        return lastFailure ?? SpellcheckResult.Fail(
            SpellcheckErrorCodes.RequestFailed,
            "OpenAI request failed.",
            stopwatch.ElapsedMilliseconds,
            maxAttempts,
            null);
    }

    private async Task<SpellcheckResult> TrySpellcheckOnceAsync(
        string inputText,
        string apiKey,
        Stopwatch stopwatch,
        int attempt)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(BuildPayload(inputText), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var error = ExtractErrorMessage(body) ?? response.ReasonPhrase ?? "OpenAI request failed.";
                return SpellcheckResult.Fail(
                    response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        ? SpellcheckErrorCodes.InvalidApiKey
                        : SpellcheckErrorCodes.RequestFailed,
                    error,
                    stopwatch.ElapsedMilliseconds,
                    attempt,
                    (int)response.StatusCode);
            }

            var output = ExtractOutputText(body);
            if (string.IsNullOrWhiteSpace(output))
            {
                _logger.Log("parse_failed empty_output_text");
                return SpellcheckResult.Fail(
                    SpellcheckErrorCodes.ParseFailed,
                    "OpenAI returned no replacement text.",
                    stopwatch.ElapsedMilliseconds,
                    attempt,
                    (int)response.StatusCode);
            }

            return SpellcheckResult.Ok(output, stopwatch.ElapsedMilliseconds, attempt, (int)response.StatusCode);
        }
        catch (TaskCanceledException ex)
        {
            return SpellcheckResult.Fail(
                SpellcheckErrorCodes.Timeout,
                ex.Message,
                stopwatch.ElapsedMilliseconds,
                attempt,
                null);
        }
        catch (Exception ex)
        {
            return SpellcheckResult.Fail(
                SpellcheckErrorCodes.RequestFailed,
                ex.Message,
                stopwatch.ElapsedMilliseconds,
                attempt,
                null);
        }
    }

    private static bool ShouldRetry(int? statusCode)
    {
        if (statusCode is null)
        {
            return true;
        }

        return statusCode == (int)HttpStatusCode.RequestTimeout
            || statusCode == (int)HttpStatusCode.TooManyRequests
            || statusCode >= 500;
    }

    private static string BuildPayload(string inputText)
    {
        var prompt =
            "instructions: " + PromptInstruction + "\n" +
            "text input: " +
            inputText;

        var payload = new
        {
            model = Model,
            input = new[]
            {
                new
                {
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "input_text",
                            text = prompt
                        }
                    }
                }
            },
            store = true,
            text = new
            {
                verbosity = "medium"
            },
            temperature = 0.3
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string? ExtractOutputText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        if (root.TryGetProperty("output_text", out var outputText)
            && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString();
        }

        if (!root.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var type)
                    && type.GetString() != "output_text")
                {
                    continue;
                }

                if (part.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString();
                }
            }
        }

        return null;
    }

    private static string? ExtractErrorMessage(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message))
            {
                return message.GetString();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

internal static class SpellcheckErrorCodes
{
    public const string MissingApiKey = "missing_api_key";
    public const string InvalidApiKey = "invalid_api_key";
    public const string Timeout = "timeout";
    public const string RequestFailed = "request_failed";
    public const string ParseFailed = "parse_failed";
}

internal sealed class SpellcheckResult
{
    public bool Success { get; init; }
    public string? OutputText { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public long DurationMs { get; init; }
    public int Attempts { get; init; }
    public int? StatusCode { get; init; }

    public static SpellcheckResult Ok(string outputText, long durationMs, int attempts, int? statusCode) => new()
    {
        Success = true,
        OutputText = outputText,
        DurationMs = durationMs,
        Attempts = attempts,
        StatusCode = statusCode
    };

    public static SpellcheckResult Fail(
        string errorCode,
        string errorMessage,
        long durationMs,
        int attempts,
        int? statusCode) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage,
        DurationMs = durationMs,
        Attempts = attempts,
        StatusCode = statusCode
    };
}
