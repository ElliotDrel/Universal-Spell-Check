using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UniversalSpellCheck;

internal sealed class OpenAiSpellcheckService : IDisposable
{
    private const string Endpoint = "https://api.openai.com/v1/responses";
    private const string ModelsEndpoint = "https://api.openai.com/v1/models";
    public const string Model = "gpt-4.1";

    // AHK-canonical instruction text — keep byte-for-byte identical to legacy.
    public const string PromptInstruction =
        "Fix the grammar and spelling of the text below. Preserve all formatting, line breaks, and special characters. Do not add or remove any content. Return only the corrected text.";

    // Pre-built UTF-8 byte slabs around the JSON-escaped user text. Built once
    // at type init so the hot path only allocates one byte[] per request.
    private static readonly byte[] PrefixBytes = Encoding.UTF8.GetBytes(
        "{\"model\":\"" + Model + "\"," +
        "\"input\":[{\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" +
        "instructions: " + JsonEscape(PromptInstruction) + "\\n" +
        "text input: ");
    private static readonly byte[] SuffixBytes = Encoding.UTF8.GetBytes(
        "\"}]}],\"store\":true,\"text\":{\"verbosity\":\"medium\"},\"temperature\":0.3}");

    private static readonly MediaTypeHeaderValue JsonMediaType =
        new("application/json") { CharSet = "utf-8" };

    private readonly CachedSettings _cachedSettings;
    private readonly DiagnosticsLogger _logger;
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _httpClient;
    private System.Threading.Timer? _rewarmTimer;

    public OpenAiSpellcheckService(CachedSettings cachedSettings, DiagnosticsLogger logger)
    {
        _cachedSettings = cachedSettings;
        _logger = logger;
        _handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        _httpClient = new HttpClient(_handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };
    }

    // One-time + periodic background warm-up. Forces DNS+TCP+TLS+H2 negotiation
    // so the first hotkey doesn't pay handshake cost.
    public void StartConnectionWarmer()
    {
        _ = WarmConnectionAsync();
        _rewarmTimer = new System.Threading.Timer(
            _ => _ = WarmConnectionAsync(),
            null,
            TimeSpan.FromMinutes(4),
            TimeSpan.FromMinutes(4));
    }

    private async Task WarmConnectionAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);
            req.Version = HttpVersion.Version20;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            // Status doesn't matter — we only want the live socket in the pool.
        }
        catch (Exception ex)
        {
            _logger.Log($"connection_warm_failed error=\"{Escape(ex.Message)}\"");
        }
    }

    public async Task<HotPathSpellcheckResult> SpellcheckAsync(string inputText, RunRecord record)
    {
        var apiKey = _cachedSettings.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var payload = BuildPayload(inputText);
            return new HotPathSpellcheckResult
            {
                Success = false,
                ErrorCode = SpellcheckErrorCodes.MissingApiKey,
                ErrorMessage = "Missing OpenAI API key.",
                Attempts = 0,
                RequestPayloadBytes = payload
            };
        }

        const int maxAttempts = 2;
        HotPathSpellcheckResult? lastFailure = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var result = await TrySpellcheckOnceAsync(inputText, apiKey, attempt, record);
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

        return lastFailure ?? new HotPathSpellcheckResult
        {
            Success = false,
            ErrorCode = SpellcheckErrorCodes.RequestFailed,
            ErrorMessage = "OpenAI request failed.",
            Attempts = maxAttempts
        };
    }

    private async Task<HotPathSpellcheckResult> TrySpellcheckOnceAsync(
        string inputText,
        string apiKey,
        int attempt,
        RunRecord record)
    {
        byte[]? payload = null;
        try
        {
            payload = BuildPayload(inputText);
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = JsonMediaType;
            request.Content = content;
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;

            if (attempt == 1)
            {
                record.T_RequestSendStart = Stopwatch.GetTimestamp();
            }

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);

            record.T_ResponseFirstByte = Stopwatch.GetTimestamp();
            if (record.T_RequestSendEnd == 0)
            {
                record.T_RequestSendEnd = record.T_ResponseFirstByte;
            }

            var bodyBytes = await response.Content.ReadAsByteArrayAsync();
            record.T_ResponseEnd = Stopwatch.GetTimestamp();

            if (!response.IsSuccessStatusCode)
            {
                var error = ExtractErrorMessage(bodyBytes) ?? response.ReasonPhrase ?? "OpenAI request failed.";
                return new HotPathSpellcheckResult
                {
                    Success = false,
                    ErrorCode = response.StatusCode == HttpStatusCode.Unauthorized
                        ? SpellcheckErrorCodes.InvalidApiKey
                        : SpellcheckErrorCodes.RequestFailed,
                    ErrorMessage = error,
                    Attempts = attempt,
                    StatusCode = (int)response.StatusCode,
                    RawResponseBytes = bodyBytes,
                    RequestPayloadBytes = payload
                };
            }

            var (output, tokens) = ExtractOutputAndTokens(bodyBytes);
            if (string.IsNullOrWhiteSpace(output))
            {
                _logger.Log("parse_failed empty_output_text");
                return new HotPathSpellcheckResult
                {
                    Success = false,
                    ErrorCode = SpellcheckErrorCodes.ParseFailed,
                    ErrorMessage = "OpenAI returned no replacement text.",
                    Attempts = attempt,
                    StatusCode = (int)response.StatusCode,
                    RawResponseBytes = bodyBytes,
                    RequestPayloadBytes = payload,
                    Tokens = tokens
                };
            }

            return new HotPathSpellcheckResult
            {
                Success = true,
                OutputText = output,
                Attempts = attempt,
                StatusCode = (int)response.StatusCode,
                RawResponseBytes = bodyBytes,
                RequestPayloadBytes = payload,
                Tokens = tokens
            };
        }
        catch (TaskCanceledException ex)
        {
            return new HotPathSpellcheckResult
            {
                Success = false,
                ErrorCode = SpellcheckErrorCodes.Timeout,
                ErrorMessage = ex.Message,
                Attempts = attempt,
                RequestPayloadBytes = payload
            };
        }
        catch (Exception ex)
        {
            return new HotPathSpellcheckResult
            {
                Success = false,
                ErrorCode = SpellcheckErrorCodes.RequestFailed,
                ErrorMessage = ex.Message,
                Attempts = attempt,
                RequestPayloadBytes = payload
            };
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

    // Build the request payload by sandwiching the JSON-escaped user text
    // between two pre-built UTF-8 slabs. No anonymous-object serialize, no
    // string allocations beyond the escape step.
    private static byte[] BuildPayload(string inputText)
    {
        var escaped = JsonEncodedText.Encode(inputText).EncodedUtf8Bytes;
        var buffer = new byte[PrefixBytes.Length + escaped.Length + SuffixBytes.Length];
        var span = buffer.AsSpan();
        PrefixBytes.AsSpan().CopyTo(span);
        escaped.CopyTo(span[PrefixBytes.Length..]);
        SuffixBytes.AsSpan().CopyTo(span[(PrefixBytes.Length + escaped.Length)..]);
        return buffer;
    }

    // JSON-escape a constant string at type-init time. Returns the escaped
    // representation without surrounding quotes.
    private static string JsonEscape(string value)
    {
        return Encoding.UTF8.GetString(JsonEncodedText.Encode(value).EncodedUtf8Bytes);
    }

    // Single-pass walk: extract output text and token usage in one go.
    private static (string? output, TokenUsage tokens) ExtractOutputAndTokens(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? output = null;
            var tokens = new TokenUsage();

            if (root.TryGetProperty("output_text", out var outputText)
                && outputText.ValueKind == JsonValueKind.String)
            {
                output = outputText.GetString();
            }

            if (output is null
                && root.TryGetProperty("output", out var outputArr)
                && outputArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outputArr.EnumerateArray())
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
                            output = text.GetString();
                            break;
                        }
                    }

                    if (output is not null) break;
                }
            }

            if (root.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                tokens = new TokenUsage
                {
                    Input = ReadInt(usage, "input_tokens"),
                    Output = ReadInt(usage, "output_tokens"),
                    Total = ReadInt(usage, "total_tokens"),
                    Cached = ReadNestedInt(usage, "input_tokens_details", "cached_tokens"),
                    Reasoning = ReadNestedInt(usage, "output_tokens_details", "reasoning_tokens")
                };
            }

            return (output, tokens);
        }
        catch
        {
            return (null, new TokenUsage());
        }
    }

    private static int ReadInt(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var i)
            ? i
            : 0;
    }

    private static int ReadNestedInt(JsonElement parent, string nested, string name)
    {
        if (!parent.TryGetProperty(nested, out var details)
            || details.ValueKind != JsonValueKind.Object)
        {
            // Fall back to a flat lookup in case the schema flattens.
            return ReadInt(parent, name);
        }

        return ReadInt(details, name);
    }

    private static string? ExtractErrorMessage(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error)
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

    private static string Escape(string? value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    public void Dispose()
    {
        _rewarmTimer?.Dispose();
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

internal sealed class HotPathSpellcheckResult
{
    public bool Success { get; init; }
    public string? OutputText { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public int Attempts { get; init; }
    public int? StatusCode { get; init; }
    public byte[]? RawResponseBytes { get; init; }
    public byte[]? RequestPayloadBytes { get; init; }
    public TokenUsage Tokens { get; init; } = new();
}

internal sealed class TokenUsage
{
    public int Input { get; init; }
    public int Output { get; init; }
    public int Total { get; init; }
    public int Cached { get; init; }
    public int Reasoning { get; init; }
}
