using System.Net;
using System.Text;
using System.Text.Json;

namespace UniversalSpellCheck.Bench;

// Only the transport is substituted; production payload construction and response parsing still run.
internal sealed class ReplayTransport(List<ReplayFixture> fixtures) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
        var text = payload.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString();
        var matches = fixtures.Where(f => BenchHarness.Normalize(text ?? "") == "instructions: " + OpenAiSpellcheckService.PromptInstruction
            + "\ntext input: " + BenchHarness.Normalize(ProtectedText.Protect(f.Input).Text)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Replay request does not match exactly one frozen input.");
        var response = JsonSerializer.Serialize(new
        {
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = matches[0].RawAiOutput } } } },
            usage = new { input_tokens = 0, output_tokens = 0, total_tokens = 0 }
        });
        return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
    }
}
