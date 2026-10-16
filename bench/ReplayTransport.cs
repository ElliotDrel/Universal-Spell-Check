using System.Net;
using System.Text;
using System.Text.Json;

namespace UniversalSpellCheck.Bench;

// Only the transport is substituted; production payload construction and response parsing still run.
internal sealed class ReplayTransport(List<ReplayFixture> fixtures) : HttpMessageHandler
{
    // Prepare lookup/response bytes once. Rebuilding every fixture on each request would
    // benchmark the harness rather than the application's payload/parser work.
    private readonly Dictionary<string, byte[]> _responses = fixtures.ToDictionary(
        f => "instructions: " + OpenAiSpellcheckService.PromptInstruction + "\ntext input: "
            + BenchHarness.Normalize(ProtectedText.Protect(f.Input).Text),
        f => JsonSerializer.SerializeToUtf8Bytes(new
        {
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = f.RawAiOutput } } } },
            usage = new { input_tokens = 0, output_tokens = 0, total_tokens = 0 }
        }), StringComparer.Ordinal);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
        var text = payload.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString();
        if (!_responses.TryGetValue(BenchHarness.Normalize(text ?? ""), out var response))
            throw new InvalidOperationException("Replay request does not match exactly one frozen input.");
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(response) };
    }
}
