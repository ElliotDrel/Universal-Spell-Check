using System.Diagnostics;
using UniversalSpellCheck;

var cases = new[]
{
    ("url", "See https://example.com/open-ai?q=GitHub.", ProtectedLiteralKind.Url),
    ("uuid", "Session d5c821f1-68a1-4a4b-9e69-e2f8882f373e failed.", ProtectedLiteralKind.Uuid),
    ("openai key", "Use sk-proj-abcdefghijklmnopqrstuvwxyz123456.", ProtectedLiteralKind.ApiKey),
    ("github token", "Token ghp_abcdefghijklmnopqrstuvwxyz1234567890.", ProtectedLiteralKind.ApiKey),
    ("assigned key", "api_key = \"abcDEF1234567890+/=\"", ProtectedLiteralKind.ApiKey),
    ("windows path", "Open \"C:\\Users\\Elliot\\Documents\\Open AI\\notes.txt\" now.", ProtectedLiteralKind.FilePath),
    ("unc path", @"Open \\server\share\folder\notes.txt now.", ProtectedLiteralKind.FilePath),
    ("posix path", "Read /usr/local/share/app/config.json next.", ProtectedLiteralKind.FilePath),
    ("relative path", "Edit src/TextPostProcessor.cs next.", ProtectedLiteralKind.FilePath),
    ("opaque id", "Request req_abcDEF12345678901234567890 failed.", ProtectedLiteralKind.OpaqueId)
};

foreach (var (name, input, expectedKind) in cases)
{
    var protection = ProtectedText.Protect(input);
    Assert(protection.Entries.Count == 1, $"{name}: expected one protected literal");
    Assert(protection.Entries[0].Kind == expectedKind, $"{name}: wrong literal kind");
    Assert(!protection.Text.Contains(protection.Entries[0].Value, StringComparison.Ordinal),
        $"{name}: original literal remained in request text");

    var restored = ProtectedText.Restore(protection.Text, protection);
    Assert(restored.Success, $"{name}: restoration failed");
    Assert(restored.Text == input, $"{name}: round trip changed text");
}

var mixedInput =
    @"Fix teh file C:\code\app\Program.cs for session d5c821f1-68a1-4a4b-9e69-e2f8882f373e at https://example.com.";
var mixed = ProtectedText.Protect(mixedInput);
Assert(mixed.Entries.Count == 3, "mixed input: expected three protected literals");
Assert(ProtectedText.Restore(mixed.Text.Replace("teh", "the", StringComparison.Ordinal), mixed).Text
    == mixedInput.Replace("teh", "the", StringComparison.Ordinal), "mixed input: correction did not round trip");

var collision = ProtectedText.Protect("Keep __USC_LITERAL_0_1__ and https://example.com.");
Assert(!collision.Entries[0].Placeholder.StartsWith("__USC_LITERAL_0_", StringComparison.Ordinal),
    "placeholder collision: namespace was not advanced");
Assert(ProtectedText.Restore(collision.Text, collision).Text
    == "Keep __USC_LITERAL_0_1__ and https://example.com.", "placeholder collision: round trip failed");

var missing = ProtectedText.Restore(mixed.Text.Replace(mixed.Entries[0].Placeholder, "", StringComparison.Ordinal), mixed);
Assert(!missing.Success, "missing placeholder: corrupted output was accepted");

var duplicated = ProtectedText.Restore(
    mixed.Text.Replace(
        mixed.Entries[0].Placeholder,
        mixed.Entries[0].Placeholder + mixed.Entries[0].Placeholder,
        StringComparison.Ordinal),
    mixed);
Assert(!duplicated.Success, "duplicate placeholder: corrupted output was accepted");

const int iterations = 100_000;
var stopwatch = Stopwatch.StartNew();
for (var i = 0; i < iterations; i++)
{
    _ = ProtectedText.Protect(mixedInput);
}
stopwatch.Stop();

Console.WriteLine(
    $"ProtectedText tests passed. {iterations:N0} mixed-input extractions in {stopwatch.Elapsed.TotalMilliseconds:N1} ms " +
    $"({stopwatch.Elapsed.TotalMilliseconds * 1000 / iterations:N2} us/call).");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
