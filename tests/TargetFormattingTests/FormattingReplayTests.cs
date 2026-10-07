using System.Text;
using System.Text.Json;

internal static class FormattingReplayTests
{
    public static void Run()
    {
        const string source = "teh \U0001F600";
        const string corrected = "the \U0001F600";
        var detail = new Dictionary<string, object>
        {
            ["input_text"] = source, ["input_chars"] = 6,
            ["output_text"] = corrected, ["output_chars"] = 6,
            ["clipboard_html"] = "", ["clipboard_html_chars"] = 0,
            ["paste_text"] = corrected, ["paste_html"] = ""
        };
        var result = Replay(detail);
        Check(result.result.text == corrected && result.result.html == ""
            && result.result.reason == "no_html" && result.recorded_matches is true,
            "plain-text replay must preserve supplementary Unicode and compare the recorded paste");
        var fragment = "<p data-pm-slice=\"0 0 []\"><strong>" + source + "</strong></p>";
        var html = "<html><body><!--StartFragment-->" + fragment + "<!--EndFragment--></body></html>";
        const string header = "Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var headerSize = string.Format(header, 0, 0, 0, 0).Length;
        var start = headerSize + Encoding.UTF8.GetByteCount("<html><body><!--StartFragment-->");
        detail["clipboard_html"] = string.Format(header, headerSize,
            headerSize + Encoding.UTF8.GetByteCount(html), start,
            start + Encoding.UTF8.GetByteCount(fragment)) + html;
        detail["clipboard_html_chars"] = ((string)detail["clipboard_html"]).Length;
        var rich = Replay(detail);
        Check(System.Net.WebUtility.HtmlDecode(rich.result.html).Contains("<strong>" + corrected + "</strong>")
            && rich.result.html.StartsWith("Version:") && rich.result.applied,
            "rich replay must return the full corrected CF_HTML payload using the actual mapper");
        detail["clipboard_html"] = "";
        detail["clipboard_html_chars"] = 0;
        detail["clipboard_formats"] = "UnicodeText, HTML Format";
        Reject(detail, "offered HTML with an empty capture must not be mistaken for plain-text-only source");
        detail["clipboard_formats"] = "UnicodeText";
        Check(Replay(detail).result.reason == "no_html", "confirmed absence of source HTML supports plain replay");
        detail["clipboard_html_truncated"] = true;
        Reject(detail, "truncated HTML must not become a false reproduction");
        detail["clipboard_html_truncated"] = false;
        detail["input_chars"] = 7;
        Reject(detail, "incomplete Unicode must fail even without a truncation flag");
        detail["input_chars"] = 6;
        detail.Remove("output_text");
        Reject(detail, "missing mapper output must fail rather than replay raw model output");
        detail["output_text"] = corrected;
        detail["paste_text_truncated"] = true;
        Check(Replay(detail).recorded_matches is null, "truncated historical paste must not claim equality");
        using var testCase = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            schema_version = 1, kind = "formatting_replay_case", detail,
            expected = new { text = "wrong", html = "", mode = "none", reason = "no_html" }
        }));
        Check(FormattingReplay.Replay(testCase.RootElement).expected_matches is false,
            "a saved regression expectation must detect a changed result");
    }

    private static ReplayOutput Replay(Dictionary<string, object> detail)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { detail }));
        return FormattingReplay.Replay(document.RootElement);
    }

    private static void Reject(Dictionary<string, object> detail, string message)
    {
        try { Replay(detail); }
        catch (InvalidDataException) { return; }
        throw new Exception(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
