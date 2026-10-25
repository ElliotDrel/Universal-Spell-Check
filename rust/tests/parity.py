"""Run the existing C# formatting suite with a recording wrapper; compare each real call to Rust.
No clipboard, UI, API calls, production keys, or production runtime logs are used.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.sax.saxutils as xml


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet", required=True)
    parser.add_argument("--rust", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--processing-arm", choices=["baseline", "replacements-ac", "restore-splice", "rich-index"])
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    generated = output / "oracle"
    generated.mkdir()
    source = (root / "src/RichTextClipboard.cs").read_text(encoding="utf-8-sig")
    signature = """    public static RichTextReplacementResult TryCreateReplacement(
        string sourceHtml, string sourceText, string correctedText, string nativeDocsBase64)
    {"""
    assert source.count(signature) == 1
    wrapper = """    public static RichTextReplacementResult TryCreateReplacement(
        string sourceHtml, string sourceText, string correctedText, string nativeDocsBase64)
    {
        var result = RecordedReplacement(sourceHtml, sourceText, correctedText, nativeDocsBase64);
        var record = System.Text.Json.JsonSerializer.Serialize(new {
            source_html = sourceHtml, input = sourceText, output = correctedText,
            native_base64 = nativeDocsBase64,
            expected = new { text = result.Text, html = result.Html, mode = result.Mode,
                applied = result.Applied, reason = result.Reason, paragraph_count = result.ParagraphCount,
                native_base64 = result.NativeDocsBytes is null ? "" : Convert.ToBase64String(result.NativeDocsBytes),
                native_status = result.NativeDocsStatus }
        });
        System.IO.File.AppendAllText(Environment.GetEnvironmentVariable("USC_PARITY_CAPTURE")!, record + "\\n");
        return result;
    }
    private static RichTextReplacementResult RecordedReplacement(
        string sourceHtml, string sourceText, string correctedText, string nativeDocsBase64)
    {"""
    (generated / "RichTextClipboard.cs").write_text(source.replace(signature, wrapper), encoding="utf-8")
    paths = [root / "tests/TargetFormattingTests/Program.cs",
             root / "tests/TargetFormattingTests/GoogleDocsClipboardTests.cs",
             root / "tests/TargetFormattingTests/FormattingReplay.cs",
             root / "tests/TargetFormattingTests/FormattingReplayTests.cs"]
    includes = "\n".join(f'<Compile Include="{xml.escape(str(p))}" />' for p in paths)
    project = f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><AssemblyName>UniversalSpellCheck.TargetFormattingTests</AssemblyName><NoWarn>CS0436</NoWarn></PropertyGroup>
<ItemGroup><ProjectReference Include="{xml.escape(str(root / 'src/UniversalSpellCheck.csproj'))}" />{includes}</ItemGroup>
</Project>'''
    (generated / "Oracle.csproj").write_text(project, encoding="utf-8")
    capture = output / "csharp-cases.jsonl"
    env = os.environ.copy()
    env["DOTNET_ROOT"] = str(Path(args.dotnet).resolve().parent)
    env["USC_PARITY_CAPTURE"] = str(capture)
    completed = subprocess.run([args.dotnet, "run", "--project", str(generated / "Oracle.csproj"), "-c", "Release"], cwd=root, env=env, text=True, encoding="utf-8", errors="replace", stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (output / "csharp-suite.txt").write_text(completed.stdout, encoding="utf-8")
    if completed.returncode:
        print(completed.stdout[-12000:])
        return completed.returncode
    cases = [json.loads(line) for line in capture.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    rust_args = [args.rust, "--rich-json"]
    if args.processing_arm:
        rust_args += ["--processing-arm", args.processing_arm]
    rust = subprocess.run(rust_args, input="".join(json.dumps(c, ensure_ascii=False) + "\n" for c in cases), text=True, encoding="utf-8", stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    (output / "rust-results.jsonl").write_text(rust.stdout, encoding="utf-8")
    if rust.returncode:
        print(rust.stderr)
        return rust.returncode
    results = [json.loads(line) for line in rust.stdout.splitlines()]
    assert len(results) == len(cases)
    mismatches = []
    for i, (case, result) in enumerate(zip(cases, results)):
        difference = {key: {"csharp": expected, "rust": result.get(key)} for key, expected in case["expected"].items() if result.get(key) != expected}
        if difference:
            mismatches.append({"index": i, "input": case["input"], "source_html": case["source_html"], "difference": difference})
    report = {"cases": len(cases), "passed": len(cases)-len(mismatches), "failed": len(mismatches), "mismatches": mismatches}
    (output / "report.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({k: report[k] for k in ["cases", "passed", "failed"]}))
    return int(bool(mismatches))


if __name__ == "__main__":
    sys.exit(main())
