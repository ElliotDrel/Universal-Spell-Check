using System.Globalization;

namespace UniversalSpellCheck;

internal sealed record DocsParagraphEdit(string Source, string Text, string Html)
{
    internal int SourceSteps => StringInfo.ParseCombiningCharacters(Source).Length;
    internal int TargetSteps => StringInfo.ParseCombiningCharacters(Text).Length;
}

internal sealed record DocsParagraphPasteResult(bool Success, int Completed, string Stage, string VerificationNative = "", int Attempted = 0);

// Docs rebases a structural paste under the insertion paragraph. Replace only
// its text ranges instead; freshly copied selections authorize every mutation.
internal static class DocsParagraphPaste
{
    internal static async Task<DocsParagraphPasteResult> PasteAsync(
        DocsParagraphEdit[] edits, string originalNative, IntPtr window, IntPtr? clipboardOwner = null)
    {
        var completed = 0;
        var attempted = 0;
        DocsParagraphPasteResult Result(bool success, string phase, string native = "") => new(success, completed, phase, native, attempted);
        var beforeLayout = GoogleDocsClipboard.ParagraphLayout(originalNative, edits.Length);
        if (beforeLayout is null || edits.Any(e => e.SourceSteps == 0 || e.TargetSteps == 0))
            return Result(false, "invalid_plan_or_layout");
        var stage = "initial_selection";
        bool FocusMatches() => ActiveWindowInfo.Capture().WindowHandle == window;
        async Task<CaptureResult?> CopyAsync(bool originalText = false)
        {
            if (!FocusMatches()) return null;
            // CaptureSelectionAsync requires a new clipboard sequence, never a
            // stale copy. It reads Docs HTML/native before any clipboard rewrite.
            var copy = await ClipboardLoop.CaptureSelectionAsync();
            if (copy.Success && originalText && clipboardOwner is { } owner)
                ClipboardLoop.ExcludeTextFromHistory(copy.Text!, owner, out _);
            return copy.Success && FocusMatches() ? copy : null;
        }
        try
        {
            if (!FocusMatches()) return Result(false, "foreground_changed");
            SendKeys.SendWait("{RIGHT}");
            for (var i = edits.Length - 1; i >= 0; i--)
            {
                var edit = edits[i];
                stage = $"source_selection_{i}";
                if (!FocusMatches()) return Result(false, "foreground_changed");
                SendKeys.SendWait("+{LEFT " + edit.SourceSteps + "}");
                var source = await CopyAsync(originalText: true);
                if (source is null || !RichTextClipboard.IsCopiedDocsParagraph(source.Html, source.Text!, edit.Source))
                    return Result(false, stage);
                stage = $"clipboard_write_{i}";
                var written = await ClipboardLoop.TrySetReplacementTextAsync(edit.Text, edit.Html);
                if (!written.Success || !FocusMatches()) return Result(false, stage);
                await Task.Delay(50);
                if (!FocusMatches()) return Result(false, "foreground_changed");
                SendKeys.SendWait("^v");
                attempted++;
                // The following fresh copy acknowledges actual editor content;
                // a delay or successful keyboard injection alone is insufficient.
                await Task.Delay(75);
                stage = $"replacement_verification_{i}";
                if (!FocusMatches()) return Result(false, "foreground_changed");
                SendKeys.SendWait("+{LEFT " + edit.TargetSteps + "}");
                var pasted = await CopyAsync();
                if (pasted is null || !RichTextClipboard.IsCopiedDocsParagraph(pasted.Html, pasted.Text!, edit.Text))
                    return Result(false, stage);
                completed++;
                if (i > 0)
                {
                    // Collapse to this verified range's start, then cross the
                    // existing delimiter. No delimiter is ever pasted or deleted.
                    if (!FocusMatches()) return Result(false, "foreground_changed");
                    SendKeys.SendWait("{LEFT 2}");
                }
            }
            stage = "whole_selection_verification";
            if (!FocusMatches()) return Result(false, "foreground_changed");
            SendKeys.SendWait("{LEFT}");
            var text = string.Join("\n", edits.Select(e => e.Text));
            SendKeys.SendWait("+{RIGHT " + StringInfo.ParseCombiningCharacters(text).Length + "}");
            var final = await CopyAsync();
            if (final is null) return Result(false, stage);
            var normalized = final.Text!.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\u00a0', ' ');
            if (normalized != text && normalized != text + "\n") return Result(false, stage);
            var afterLayout = GoogleDocsClipboard.ParagraphLayout(final.ChromiumMimeBase64, edits.Length);
            if (beforeLayout is null || afterLayout != beforeLayout)
                return Result(false, "paragraph_layout_verification", final.ChromiumMimeBase64);
            if (!FocusMatches()) return Result(false, "foreground_changed");
            SendKeys.SendWait("{RIGHT}");
            return Result(true, "verified", final.ChromiumMimeBase64);
        }
        catch (Exception ex)
        { return Result(false, stage + ":" + ex.GetType().Name); }
    }
}
