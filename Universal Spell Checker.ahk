#Requires AutoHotkey v2.0

; Manual script build version. Use a simple integer string like "5", "6", "7".
; Bump it before commits that include this script and before asking anyone to
; reload or manually retest it. Every log entry records this value as
; `script_version`, so stale reloads and "forgot to reload" test runs are easy
; to spot immediately.
scriptVersion := "15"

; Logging configuration
enableLogging := true
logDir := A_ScriptDir . "\logs"
logFilePrefix := "spellcheck"
maxLogSize := 5 * 1024 * 1024  ; 5 MiB max per weekly log file before suffix rollover

; Post-processing replacements
replacementsPath := A_ScriptDir . "\replacements.json"
postReplacements := []    ; Array of [variant, canonical] pairs, sorted longest-first
replacementsLastModified := ""
replacementsFileSize := -1

; API configuration
; Select the model module here (single source of truth):
; - "gpt-4.1"    -> standard model, uses temperature, verbosity "medium"
; - "gpt-5.1"    -> reasoning model, uses reasoning.effort "none", verbosity "low"
; - "gpt-5-mini" -> reasoning model, uses reasoning.effort "minimal", verbosity "low"
; The script applies the correct payload shape automatically.
modelModule := "gpt-4.1"

; Defaults (overridden by switch below)
apiModel := modelModule
apiUsesReasoning := false
Verbosity := "medium"
Temperature := 0.3
reasoningEffort := "none"
reasoningSummary := "auto"

switch modelModule {
    case "gpt-4.1":
        apiUsesReasoning := false
        Verbosity := "medium"
        Temperature := 0.3
        reasoningEffort := "none"
        reasoningSummary := "auto"
    case "gpt-5.1":
        apiUsesReasoning := true
        Verbosity := "low"
        reasoningEffort := "none"
        reasoningSummary := "auto"
    case "gpt-5-mini":
        apiUsesReasoning := true
        Verbosity := "low"
        reasoningEffort := "minimal"
        reasoningSummary := "auto"
    default:
        MsgBox("Invalid modelModule: " . modelModule . "`nUse one of: gpt-4.1, gpt-5.1, gpt-5-mini")
        ExitApp
}

apiUrl := "https://api.openai.com/v1/responses"

; Prompt text (single source of truth).
; Reused for request construction and prompt-leak safeguard detection.
promptInstructionText := "Fix the grammar and spelling of the text below. Preserve all formatting, line breaks, and special characters. Do not add or remove any content. Return only the corrected text."

; Create logs directory if it doesn't exist
if (!DirExist(A_ScriptDir . "\logs")) {
    DirCreate(A_ScriptDir . "\logs")
}

; Configure per-app paste behavior
; - Add exe names (e.g., "notepad.exe") to `sendTextApps` to use keystroke typing
; - Default for all other apps is clipboard + Ctrl+V paste
sendTextApps := [
    ; Examples (replace or extend as needed):
    ; "SomeApp.exe",
    ; "AnotherApp.exe"
]

UseSendTextForExe(activeExe) {
    global sendTextApps
    for exe in sendTextApps {
        if (StrLower(exe) = StrLower(activeExe))
            return true
    }
    return false
}

ShowTemporaryToolTip(message, durationMs := 3000) {
    ToolTip(message)
    SetTimer(() => ToolTip(), -durationMs)
}

SetTransientClipboardText(text) {
    A_Clipboard := text
    SetClipboardHistoryPolicy(false, false)
}

AbortRun(logData, errorMessage, eventMessage := "", tooltipMessage := "", tooltipDurationMs := 3000) {
    logData.error := errorMessage
    logData.pasteTime := A_TickCount
    if (eventMessage != "")
        logData.events.Push(eventMessage)
    if (tooltipMessage != "")
        ShowTemporaryToolTip(tooltipMessage, tooltipDurationMs)
    return true
}

MakeLogPreview(text, maxLen := 160) {
    preview := StrReplace(text, "`r", "\r")
    preview := StrReplace(preview, "`n", "\n")
    preview := StrReplace(preview, "`t", "\t")
    if (StrLen(preview) > maxLen)
        return SubStr(preview, 1, maxLen) . "..."
    return preview
}

GetHotkeyPhysicalState() {
    return "Ctrl=" . (GetKeyState("Ctrl", "P") ? "down" : "up")
        . ", Alt=" . (GetKeyState("Alt", "P") ? "down" : "up")
        . ", U=" . (GetKeyState("u", "P") ? "down" : "up")
}

GetActiveWindowDebugInfo() {
    info := {
        title: "",
        exe: "unknown",
        class: "",
        hwnd: 0,
        focusedControl: ""
    }

    info.hwnd := WinExist("A")
    try info.title := WinGetTitle("A")
    catch
        info.title := ""
    try info.exe := WinGetProcessName("A")
    catch
        info.exe := "unknown"
    try info.class := WinGetClass("A")
    catch
        info.class := ""
    try info.focusedControl := ControlGetFocus("A")
    catch
        info.focusedControl := ""

    return info
}

BuildClipboardDebugSummary(details) {
    if !IsObject(details)
        return "clipboard details unavailable"

    return "selected="
        . (details.HasOwnProp("selectedFormat") ? details.selectedFormat : "<unknown>")
        . ", chars="
        . (details.HasOwnProp("selectedChars") ? details.selectedChars : 0)
        . ", html="
        . ((details.HasOwnProp("htmlAvailable") && details.htmlAvailable) ? "yes" : "no")
        . ", unicode="
        . ((details.HasOwnProp("unicodeAvailable") && details.unicodeAvailable) ? "yes" : "no")
        . ", ansi="
        . ((details.HasOwnProp("ansiAvailable") && details.ansiAvailable) ? "yes" : "no")
        . ((details.HasOwnProp("htmlChars") && details.htmlChars > 0) ? ", html_chars=" . details.htmlChars : "")
        . ((details.HasOwnProp("fragmentChars") && details.fragmentChars > 0) ? ", fragment_chars=" . details.fragmentChars : "")
        . ((details.HasOwnProp("fallbackUsed") && details.fallbackUsed) ? ", fallback=yes" : "")
}

; Load post-processing replacements from replacements.json.
; JSON format: { "canonical": ["variant1", "variant2", ...], ... }
; Builds a flat list of [variant, canonical] sorted longest-first so longer
; phrases are replaced before any shorter substring could interfere.
FormatReplacementsMetadata(lastModified, fileSize) {
    return "mtime=" . (lastModified != "" ? lastModified : "<none>") . ", size=" . fileSize
}

TryBuildReplacementsCache(&pairs, &lastModified, &fileSize, &errorMessage) {
    global replacementsPath

    pairs := []
    lastModified := ""
    fileSize := -1
    errorMessage := ""

    if (!FileExist(replacementsPath))
        return false

    try {
        rawJson := FileRead(replacementsPath, "UTF-8")
        if (SubStr(rawJson, 1, 1) = Chr(0xFEFF))
            rawJson := SubStr(rawJson, 2)
        obj := JsonLoad(rawJson)

        for canonical, variants in obj {
            if !IsObject(variants)
                continue
            for , variant in variants {
                if (variant != "" && StrCompare(variant, canonical, true) != 0)
                    pairs.Push([variant, canonical])
            }
        }

        ; Insertion sort by variant length descending (list is typically tiny)
        n := pairs.Length
        loop n - 1 {
            i := A_Index + 1
            while (i > 1 && StrLen(pairs[i][1]) > StrLen(pairs[i - 1][1])) {
                temp := pairs[i]
                pairs[i] := pairs[i - 1]
                pairs[i - 1] := temp
                i--
            }
        }
        lastModified := FileGetTime(replacementsPath, "M")
        fileSize := FileGetSize(replacementsPath)
        return true

    } catch Error as e {
        errorMessage := e.Message
        return false
    }
}

LoadReplacements(&errorMessage := "") {
    global postReplacements, replacementsLastModified, replacementsFileSize

    pairs := []
    lastModified := ""
    fileSize := -1
    if !TryBuildReplacementsCache(&pairs, &lastModified, &fileSize, &errorMessage)
        return false

    postReplacements := pairs
    replacementsLastModified := lastModified
    replacementsFileSize := fileSize
    return true
}

GetReplacementsMetadata(&lastModified, &fileSize, &status := "") {
    global replacementsPath

    lastModified := ""
    fileSize := -1
    status := "ok"

    if (!FileExist(replacementsPath)) {
        status := "missing"
        return false
    }

    try {
        lastModified := FileGetTime(replacementsPath, "M")
        fileSize := FileGetSize(replacementsPath)
        return true
    } catch {
        status := "error"
        return false
    }
}

RefreshReplacementsIfChanged(&status, &details) {
    global postReplacements, replacementsLastModified, replacementsFileSize

    status := "cached"
    details := ""
    currentModified := ""
    currentSize := -1
    cachedMetadata := FormatReplacementsMetadata(replacementsLastModified, replacementsFileSize)
    metadataStatus := ""

    if !GetReplacementsMetadata(&currentModified, &currentSize, &metadataStatus) {
        if (metadataStatus = "missing") {
            postReplacements := []
            replacementsLastModified := ""
            replacementsFileSize := -1
            status := "missing"
            details := "cached " . cachedMetadata . ", replacements file not found; cache cleared"
            return false
        }

        status := "metadata_error"
        details := "cached " . cachedMetadata . ", disk metadata unavailable"
        return true
    }

    currentMetadata := FormatReplacementsMetadata(currentModified, currentSize)
    if (currentModified != replacementsLastModified || currentSize != replacementsFileSize) {
        loadError := ""
        if LoadReplacements(&loadError) {
            status := "reloaded"
            details := "cached " . cachedMetadata . " -> disk " . currentMetadata
            return false
        }

        status := "reload_failed"
        details := "cached " . cachedMetadata . " -> disk " . currentMetadata . (loadError != "" ? " (" . loadError . ")" : "")
        return true
    }

    details := currentMetadata
    return false
}

RetryReplacementsReloadAfterPaste(events) {
    global replacementsLastModified, replacementsFileSize

    events.Push("Deferred replacements reload started")
    loadError := ""
    if LoadReplacements(&loadError) {
        events.Push("Deferred replacements reload succeeded; cache refreshed to " . FormatReplacementsMetadata(replacementsLastModified, replacementsFileSize))
    } else {
        events.Push("Deferred replacements reload failed; still using last known-good cache" . (loadError != "" ? " (" . loadError . ")" : ""))
    }
}

; Prime the replacements cache on startup. Later runs only reparse when metadata changes.
LoadReplacements()

; Apply post-processing replacements to AI output. Runs in microseconds.
; &applied receives a list of "variant->canonical" strings for every replacement that fired.
; URLs are protected: extracted before replacements, restored after.
ApplyReplacements(text, &applied, &urlCount) {
    global postReplacements
    applied := []
    urlCount := 0

    ; Extract URLs into placeholders so replacements don't break them
    urls := []
    pos := 1
    while (pos := RegExMatch(text, "https?://\S+", &m, pos)) {
        urls.Push(m[0])
        placeholder := "__URL_" . urls.Length . "__"
        text := SubStr(text, 1, pos - 1) . placeholder . SubStr(text, pos + StrLen(m[0]))
        pos += StrLen(placeholder)
    }

    urlCount := urls.Length

    ; Run replacements on placeholder-protected text
    for pair in postReplacements {
        if InStr(text, pair[1], true) {
            replaceCount := 0
            text := StrReplace(text, pair[1], pair[2], true, &replaceCount)
            if (replaceCount > 0)
                applied.Push(pair[1] . " -> " . pair[2])
        }
    }

    ; Restore original URLs
    i := urls.Length
    while (i >= 1) {
        text := StrReplace(text, "__URL_" . i . "__", urls[i])
        i--
    }

    return text
}

; Remove leaked prompt text if model accidentally echoes the instruction block.
; Simple rule: if output contains the instruction prompt, remove it.
StripPromptLeak(text, promptText, &details) {
    details := {
        triggered: false,
        occurrences: 0,
        textInputRemoved: false,
        removedChars: 0,
        beforeLength: StrLen(text),
        afterLength: StrLen(text)
    }

    if (promptText = "")
        return text

    leakedPromptLine := "instructions: " . promptText
    if !InStr(text, leakedPromptLine, true)
        return text

    replaced := 0
    textAfter := StrReplace(text, leakedPromptLine, "", true, &replaced)
    textAfter := LTrim(textAfter, " `t`r`n")

    textInputLabel := "text input:"
    if (InStr(textAfter, textInputLabel, true) = 1) {
        textAfter := SubStr(textAfter, StrLen(textInputLabel) + 1)
        textAfter := LTrim(textAfter, " `t`r`n")
        details.textInputRemoved := true
    }

    details.triggered := true
    details.occurrences := replaced
    details.afterLength := StrLen(textAfter)
    details.removedChars := details.beforeLength - details.afterLength
    return textAfter
}

; Read clipboard text preferring Unicode; fall back to CP1252 if only ANSI is present
GetClipboardText(&details := 0) {
    ; Default to AutoHotkey's view in case the clipboard can't be opened
    text := A_Clipboard
    details := {
        selectedFormat: "ahk_fallback",
        selectedChars: StrLen(text),
        htmlAvailable: false,
        unicodeAvailable: false,
        ansiAvailable: false,
        htmlChars: 0,
        fragmentChars: 0,
        fallbackUsed: true,
        probeMs: 0
    }
    if !DllCall("OpenClipboard", "ptr", 0)
        return text

    static CF_TEXT := 1
    static CF_UNICODETEXT := 13
    static CF_HTML := DllCall("RegisterClipboardFormat", "str", "HTML Format", "uint")
    result := ""
    probeStart := A_TickCount

    try {
        ; Prefer HTML when available so we can strip formatting noise (empty paragraphs, etc.)
        details.htmlAvailable := CF_HTML && DllCall("IsClipboardFormatAvailable", "uint", CF_HTML)
        details.unicodeAvailable := DllCall("IsClipboardFormatAvailable", "uint", CF_UNICODETEXT) ? true : false
        details.ansiAvailable := DllCall("IsClipboardFormatAvailable", "uint", CF_TEXT) ? true : false

        if (details.htmlAvailable) {
            htmlBlob := __ReadClipboardString(CF_HTML, "UTF-8")
            if (htmlBlob != "") {
                details.htmlChars := StrLen(htmlBlob)
                fragment := __ExtractHtmlFragment(htmlBlob)
                if (fragment != "") {
                    details.fragmentChars := StrLen(fragment)
                    htmlText := __HtmlFragmentToPlainText(fragment)
                    if (htmlText != "") {
                        result := htmlText
                        details.selectedFormat := "html"
                        details.selectedChars := StrLen(result)
                        details.fallbackUsed := false
                    }
                }
            }
        }

        if (result = "" && details.unicodeAvailable) {
            unicodeText := __ReadClipboardString(CF_UNICODETEXT, "UTF-16")
            if (unicodeText != "") {
                result := unicodeText
                details.selectedFormat := "unicode"
                details.selectedChars := StrLen(result)
                details.fallbackUsed := false
            }
        }

        if (result = "" && details.ansiAvailable) {
            ansiText := __ReadClipboardString(CF_TEXT, "CP1252")
            if (ansiText != "") {
                result := ansiText
                details.selectedFormat := "ansi"
                details.selectedChars := StrLen(result)
                details.fallbackUsed := false
            }
        }
    } finally {
        DllCall("CloseClipboard")
    }

    if (result = "")
        details.selectedChars := StrLen(text)
    details.probeMs := A_TickCount - probeStart

    return result != "" ? result : text
}

; Wait briefly for the Ctrl+Alt+U chord to be physically released.
; This avoids Notepad interpreting our follow-up Ctrl+C / Ctrl+V while Alt is still active.
WaitForHotkeyRelease(maxWaitMs := 250) {
    deadline := A_TickCount + maxWaitMs
    while (A_TickCount <= deadline) {
        if !GetKeyState("Ctrl", "P") && !GetKeyState("Alt", "P") && !GetKeyState("u", "P")
            return true
        Sleep(10)
    }
    return false
}

; Copy selected text into the clipboard.
; Default path preserves the original single-attempt behavior for speed.
; Notepad gets a couple of quick retries because copy capture is flaky there.
CaptureSelectedText(activeExe, &text, events, &clipboardDetails := 0) {
    retryApps := Map("notepad.exe", true)
    exeKey := StrLower(activeExe)
    useRetries := retryApps.Has(exeKey)
    attempts := useRetries ? 3 : 1
    waitSeconds := useRetries ? 0.4 : 1
    text := ""
    clipboardDetails := ""

    events.Push("Clipboard copy strategy: " . (useRetries ? "retry" : "standard") . " for " . activeExe)

    loop attempts {
        attempt := A_Index
        A_Clipboard := ""
        events.Push("Clipboard copy attempt " . attempt . ": hotkey state before Ctrl+C: " . GetHotkeyPhysicalState())

        if (useRetries) {
            ; The first attempt gets only a tiny settle delay. Retries wait a bit longer
            ; so Notepad can finish releasing Ctrl+Alt+U before we send Ctrl+C again.
            Sleep(attempt = 1 ? 30 : 75)
        }

        keysStillDown := 0
        if GetKeyState("Ctrl", "P")
            keysStillDown++
        if GetKeyState("Alt", "P")
            keysStillDown++
        if GetKeyState("u", "P")
            keysStillDown++
        if (keysStillDown) {
            events.Push("Clipboard copy attempt " . attempt . ": hotkey keys still physically down before Ctrl+C")
            if WaitForHotkeyRelease(useRetries ? 350 : 120)
                events.Push("Clipboard copy attempt " . attempt . ": hotkey keys released before Ctrl+C")
            else {
                events.Push("Clipboard copy attempt " . attempt . ": hotkey keys still down after release wait")
                if (useRetries) {
                    events.Push("Clipboard copy attempt " . attempt . " aborted because hotkey keys never released")
                    continue
                }
                events.Push("Clipboard copy attempt " . attempt . ": continuing despite hotkey-release timeout for standard app")
            }
        }

        Send("^c")
        if ClipWait(waitSeconds) {
            text := GetClipboardText(&clipboardDetails)
            if (text = "") {
                events.Push("Clipboard copy attempt " . attempt . " reached clipboard but extracted empty text (" . BuildClipboardDebugSummary(clipboardDetails) . ")")
                events.Push("Clipboard copy attempt " . attempt . " produced empty text after extraction")
                continue
            }

            events.Push("Clipboard copy succeeded on attempt " . attempt . " (" . BuildClipboardDebugSummary(clipboardDetails) . ")")
            return true
        }

        events.Push("Clipboard copy attempt " . attempt . " timed out")
    }

    return false
}

; Mark current clipboard content for Windows clipboard-history/cloud behavior.
; `canIncludeInHistory := false` makes transient data (like source Ctrl+C text) less likely to appear in Win+V history.
SetClipboardHistoryPolicy(canIncludeInHistory := true, canUploadToCloud := true) {
    static CF_CAN_INCLUDE := DllCall("RegisterClipboardFormat", "str", "CanIncludeInClipboardHistory", "uint")
    static CF_CAN_UPLOAD := DllCall("RegisterClipboardFormat", "str", "CanUploadToCloudClipboard", "uint")

    if !DllCall("OpenClipboard", "ptr", 0)
        return false

    anySet := false
    try {
        if (CF_CAN_INCLUDE)
            anySet := __SetClipboardDwordFormat(CF_CAN_INCLUDE, canIncludeInHistory ? 1 : 0) || anySet
        if (CF_CAN_UPLOAD)
            anySet := __SetClipboardDwordFormat(CF_CAN_UPLOAD, canUploadToCloud ? 1 : 0) || anySet
    } finally {
        DllCall("CloseClipboard")
    }
    return anySet
}

__SetClipboardDwordFormat(formatId, value) {
    static GMEM_MOVEABLE := 0x2
    static GMEM_ZEROINIT := 0x40

    hMem := DllCall("GlobalAlloc", "uint", GMEM_MOVEABLE | GMEM_ZEROINIT, "uptr", 4, "ptr")
    if !hMem
        return false

    pMem := DllCall("GlobalLock", "ptr", hMem, "ptr")
    if !pMem {
        DllCall("GlobalFree", "ptr", hMem)
        return false
    }

    NumPut("uint", value, pMem, 0)
    DllCall("GlobalUnlock", "ptr", hMem)

    if !DllCall("SetClipboardData", "uint", formatId, "ptr", hMem, "ptr") {
        DllCall("GlobalFree", "ptr", hMem)
        return false
    }
    return true
}

__ReadClipboardString(format, encoding) {
    if (hData := DllCall("GetClipboardData", "uint", format, "ptr")) {
        if (pData := DllCall("GlobalLock", "ptr", hData, "ptr")) {
            text := StrGet(pData, , encoding)
            DllCall("GlobalUnlock", "ptr", hData)
            return text
        }
    }
    return ""
}

__ExtractHtmlFragment(htmlBlob) {
    startMarker := "<!--StartFragment-->"
    endMarker := "<!--EndFragment-->"
    startIdx := InStr(htmlBlob, startMarker)
    endIdx := InStr(htmlBlob, endMarker)
    if (startIdx && endIdx) {
        startIdx += StrLen(startMarker)
        return SubStr(htmlBlob, startIdx, endIdx - startIdx)
    }

    if (RegExMatch(htmlBlob, "StartFragment:(\d+)", &startMatch) && RegExMatch(htmlBlob, "EndFragment:(\d+)", &endMatch)) {
        startPos := Integer(startMatch[1]) + 1
        length := Integer(endMatch[1]) - Integer(startMatch[1])
        return SubStr(htmlBlob, startPos, length)
    }
    return ""
}

__HtmlFragmentToPlainText(fragment) {
    try {
        doc := ComObject("HTMLFile")
        doc.Write(fragment)
        doc.Close()
        if (doc.body)
            return doc.body.innerText
    } catch {
        ; Fall through to caller so it can try Unicode/plain text representations
    }
    return ""
}

GetWeekStartStamp(timestamp := "") {
    if (timestamp = "")
        timestamp := A_Now

    if InStr(timestamp, "-")
        timestamp := RegExReplace(timestamp, "[^0-9]")

    wday := Integer(FormatTime(timestamp, "WDay"))
    daysFromMonday := (wday = 1) ? 6 : (wday - 2)
    weekStart := DateAdd(timestamp, -daysFromMonday, "days")
    return FormatTime(weekStart, "yyyy-MM-dd")
}

GetWeekEndStamp(timestamp := "") {
    weekStartStamp := GetWeekStartStamp(timestamp)
    weekStartValue := RegExReplace(weekStartStamp, "[^0-9]")
    weekEnd := DateAdd(weekStartValue, 6, "days")
    return FormatTime(weekEnd, "yyyy-MM-dd")
}

BuildWeeklyLogPath(baseDir, filePrefix, weekStartStamp, weekEndStamp, suffixIndex := 0) {
    suffix := (suffixIndex > 0) ? "-" . (suffixIndex + 1) : ""
    return baseDir . "\" . filePrefix . "-" . weekStartStamp . "-to-" . weekEndStamp . suffix . ".jsonl"
}

ResolveLogPathForAppend(baseDir, filePrefix, maxSize, pendingBytes, timestamp := "") {
    if (timestamp = "")
        timestamp := A_Now

    if !DirExist(baseDir)
        DirCreate(baseDir)

    weekStartStamp := GetWeekStartStamp(timestamp)
    weekEndStamp := GetWeekEndStamp(timestamp)
    suffixIndex := 0

    loop {
        logPath := BuildWeeklyLogPath(baseDir, filePrefix, weekStartStamp, weekEndStamp, suffixIndex)
        if !FileExist(logPath)
            return logPath

        if ((FileGetSize(logPath) + pendingBytes) <= maxSize)
            return logPath

        suffixIndex += 1
    }
}

; Build a JSON array of strings from an AHK array
BuildJsonStringArray(arr) {
    result := "["
    first := true
    for , item in arr {
        if (!first)
            result .= ","
        first := false
        result .= '"' . JsonEscape(String(item)) . '"'
    }
    result .= "]"
    return result
}

; Detailed logging function — writes one JSON object per line (JSONL format)
LogDetailed(data) {
    global enableLogging, logDir, logFilePrefix, maxLogSize

    if (!enableLogging)
        return

    try {
        duration := data.pasteTime - data.startTime
        status := data.error ? "ERROR" : "SUCCESS"

        ; Compute timing deltas (ms)
        t := data.timings
        tClip := (t.clipboardCaptured > 0) ? (t.clipboardCaptured - data.startTime) : 0
        tPayload := (t.payloadPrepared > 0 && t.clipboardCaptured > 0) ? (t.payloadPrepared - t.clipboardCaptured) : 0
        tReq := (t.requestSent > 0 && t.payloadPrepared > 0) ? (t.requestSent - t.payloadPrepared) : 0
        tApi := (t.responseReceived > 0 && t.requestSent > 0) ? (t.responseReceived - t.requestSent) : 0
        tParse := (t.textParsed > 0 && t.responseReceived > 0) ? (t.textParsed - t.responseReceived) : 0
        tReplace := (t.replacementsApplied > 0 && t.textParsed > 0) ? (t.replacementsApplied - t.textParsed) : 0
        tGuard := (t.promptGuardApplied > 0 && t.replacementsApplied > 0) ? (t.promptGuardApplied - t.replacementsApplied) : 0
        if (t.textPasted > 0) {
            if (t.promptGuardApplied > 0)
                prevT := t.promptGuardApplied
            else if (t.replacementsApplied > 0)
                prevT := t.replacementsApplied
            else
                prevT := t.textParsed
            tPaste := t.textPasted - prevT
        } else {
            tPaste := 0
        }

        diagWindow := 0
        diagClipboard := 0
        diagClipboardProbe := 0
        diagPayload := 0
        diagResponse := 0
        diagOutput := 0
        diagPaste := 0
        diagException := 0
        diagFinalize := 0
        if (data.HasOwnProp("diagnostics") && IsObject(data.diagnostics)) {
            d := data.diagnostics
            diagWindow := d.HasOwnProp("windowDebugMs") ? d.windowDebugMs : 0
            diagClipboard := d.HasOwnProp("clipboardDebugMs") ? d.clipboardDebugMs : 0
            diagClipboardProbe := d.HasOwnProp("clipboardProbeMs") ? d.clipboardProbeMs : 0
            diagPayload := d.HasOwnProp("payloadDebugMs") ? d.payloadDebugMs : 0
            diagResponse := d.HasOwnProp("responseDebugMs") ? d.responseDebugMs : 0
            diagOutput := d.HasOwnProp("outputDebugMs") ? d.outputDebugMs : 0
            diagPaste := d.HasOwnProp("pasteDebugMs") ? d.pasteDebugMs : 0
            diagException := d.HasOwnProp("exceptionDebugMs") ? d.exceptionDebugMs : 0
            diagFinalize := d.HasOwnProp("finalizeQueueMs") ? d.finalizeQueueMs : 0
        }
        diagTotal := diagWindow + diagClipboard + diagClipboardProbe + diagPayload + diagResponse + diagOutput + diagPaste + diagException + diagFinalize

        ; Replacements array
        repArr := "[]"
        repCount := 0
        if (data.HasOwnProp("replacementsApplied") && IsObject(data.replacementsApplied)) {
            repArr := BuildJsonStringArray(data.replacementsApplied)
            repCount := data.replacementsApplied.Length
        }

        ; Events array
        evtArr := "[]"
        if (data.HasOwnProp("events") && IsObject(data.events))
            evtArr := BuildJsonStringArray(data.events)

        ; URLs protected
        urlsProt := (data.HasOwnProp("urlsProtected")) ? data.urlsProtected : 0

        ; Prompt leak guard fields
        plTriggered := "false"
        plOcc := 0
        plTextRemoved := "false"
        plRmChars := 0
        plBefore := 0
        plAfter := 0
        if (data.HasOwnProp("promptLeakGuard") && IsObject(data.promptLeakGuard)) {
            g := data.promptLeakGuard
            if (g.HasOwnProp("triggered") && g.triggered)
                plTriggered := "true"
            if (g.HasOwnProp("occurrences"))
                plOcc := g.occurrences
            if (g.HasOwnProp("textInputRemoved") && g.textInputRemoved)
                plTextRemoved := "true"
            if (g.HasOwnProp("removedChars"))
                plRmChars := g.removedChars
            if (g.HasOwnProp("beforeLength"))
                plBefore := g.beforeLength
            if (g.HasOwnProp("afterLength"))
                plAfter := g.afterLength
        }

        ; New fields with safe defaults
        model := data.HasOwnProp("model") ? data.model : ""
        scriptVer := data.HasOwnProp("scriptVersion") ? data.scriptVersion : ""
        activeApp := data.HasOwnProp("activeApp") ? data.activeApp : ""
        activeExe := data.HasOwnProp("activeExe") ? data.activeExe : ""
        windowClass := data.HasOwnProp("windowClass") ? data.windowClass : ""
        focusedControl := data.HasOwnProp("focusedControl") ? data.focusedControl : ""
        windowHwnd := data.HasOwnProp("windowHwnd") ? data.windowHwnd : 0
        pasteMethod := data.HasOwnProp("pasteMethod") ? data.pasteMethod : ""
        clipboardSource := data.HasOwnProp("clipboardSource") ? data.clipboardSource : ""
        clipboardPreview := data.HasOwnProp("clipboardPreview") ? data.clipboardPreview : ""
        outputPreview := data.HasOwnProp("outputPreview") ? data.outputPreview : ""
        textChanged := (data.HasOwnProp("textChanged") && data.textChanged) ? "true" : "false"
        modelVer := data.HasOwnProp("modelVersion") ? data.modelVersion : ""
        tokIn := data.HasOwnProp("tokenInput") ? data.tokenInput : 0
        tokOut := data.HasOwnProp("tokenOutput") ? data.tokenOutput : 0
        tokTotal := data.HasOwnProp("tokenTotal") ? data.tokenTotal : 0
        tokCached := data.HasOwnProp("tokenCached") ? data.tokenCached : 0
        tokReason := data.HasOwnProp("tokenReasoning") ? data.tokenReasoning : 0

        ; Build single-line JSON object
        j := "{"
        j .= '"timestamp":"' . JsonEscape(data.timestamp) . '"'
        j .= ',"status":"' . JsonEscape(status) . '"'
        j .= ',"error":"' . JsonEscape(data.error) . '"'
        j .= ',"duration_ms":' . duration
        j .= ',"model":"' . JsonEscape(model) . '"'
        j .= ',"script_version":"' . JsonEscape(scriptVer) . '"'
        j .= ',"model_version":"' . JsonEscape(modelVer) . '"'
        j .= ',"active_app":"' . JsonEscape(activeApp) . '"'
        j .= ',"active_exe":"' . JsonEscape(activeExe) . '"'
        j .= ',"window_class":"' . JsonEscape(windowClass) . '"'
        j .= ',"focused_control":"' . JsonEscape(focusedControl) . '"'
        j .= ',"window_hwnd":' . windowHwnd
        j .= ',"paste_method":"' . JsonEscape(pasteMethod) . '"'
        j .= ',"clipboard_source":"' . JsonEscape(clipboardSource) . '"'
        j .= ',"clipboard_preview":"' . JsonEscape(clipboardPreview) . '"'
        j .= ',"output_preview":"' . JsonEscape(outputPreview) . '"'
        j .= ',"text_changed":' . textChanged
        j .= ',"input_text":"' . JsonEscape(data.original) . '"'
        j .= ',"input_chars":' . StrLen(data.original)
        j .= ',"output_text":"' . JsonEscape(data.pastedText) . '"'
        j .= ',"output_chars":' . StrLen(data.pastedText)
        j .= ',"raw_ai_output":"' . JsonEscape(data.rawAiOutput) . '"'
        j .= ',"tokens":{"input":' . tokIn . ',"output":' . tokOut . ',"total":' . tokTotal . ',"cached":' . tokCached . ',"reasoning":' . tokReason . '}'
        j .= ',"timings":{"clipboard_ms":' . tClip . ',"payload_ms":' . tPayload . ',"request_ms":' . tReq . ',"api_ms":' . tApi . ',"parse_ms":' . tParse . ',"replacements_ms":' . tReplace . ',"prompt_guard_ms":' . tGuard . ',"paste_ms":' . tPaste . '}'
        j .= ',"diagnostics":{"window_ms":' . diagWindow . ',"clipboard_ms":' . diagClipboard . ',"clipboard_probe_ms":' . diagClipboardProbe . ',"payload_ms":' . diagPayload . ',"response_ms":' . diagResponse . ',"output_ms":' . diagOutput . ',"paste_ms":' . diagPaste . ',"exception_ms":' . diagException . ',"finalize_queue_ms":' . diagFinalize . ',"total_ms":' . diagTotal . '}'
        j .= ',"replacements":{"count":' . repCount . ',"applied":' . repArr . ',"urls_protected":' . urlsProt . '}'
        j .= ',"prompt_leak":{"triggered":' . plTriggered . ',"occurrences":' . plOcc . ',"text_input_removed":' . plTextRemoved . ',"removed_chars":' . plRmChars . ',"before_length":' . plBefore . ',"after_length":' . plAfter . '}'
        j .= ',"events":' . evtArr
        j .= ',"raw_request":"' . JsonEscape(data.HasOwnProp("rawRequest") ? data.rawRequest : "") . '"'
        j .= ',"raw_response":"' . JsonEscape(data.rawResponse) . '"'
        j .= "}`n"

        logPath := ResolveLogPathForAppend(logDir, logFilePrefix, maxLogSize, StrPut(j, "UTF-8") - 1, data.timestamp)
        FileAppend(j, logPath)
    } catch {
        ; Silently fail if logging doesn't work - never break core functionality
    }
}

; JSON escape function for proper escaping (covers all JSON-required control chars)
JsonEscape(str) {
    str := StrReplace(str, "\", "\\")
    str := StrReplace(str, '"', '\"')
    str := StrReplace(str, "`n", "\n")
    str := StrReplace(str, "`r", "\r")
    str := StrReplace(str, "`t", "\t")
    str := StrReplace(str, Chr(8), "\b")
    str := StrReplace(str, Chr(12), "\f")
    ; Escape remaining control characters (0x00-0x1F) as \u00XX
    i := 0
    while (i <= 0x1F) {
        if (i != 8 && i != 9 && i != 10 && i != 12 && i != 13) {
            ch := Chr(i)
            if InStr(str, ch) {
                hex := Format("\u{:04x}", i)
                str := StrReplace(str, ch, hex)
            }
        }
        i++
    }
    return str
}

; Minimal JSON parser (enough for the Responses API payloads)
JsonLoad(json) {
    parser := {text: json, pos: 1, len: StrLen(json)}
    value := __JsonParseValue(parser)
    __JsonSkipWhitespace(parser)
    if (parser.pos <= parser.len)
        throw Error("JSON parse error: unexpected data at position " parser.pos)
    return value
}

__JsonSkipWhitespace(parser) {
    while (parser.pos <= parser.len) {
        char := SubStr(parser.text, parser.pos, 1)
        if (char != " " && char != "`t" && char != "`n" && char != "`r")
            break
        parser.pos += 1
    }
}

__JsonPeekChar(parser) {
    if (parser.pos > parser.len)
        return ""
    return SubStr(parser.text, parser.pos, 1)
}

__JsonConsumeChar(parser) {
    if (parser.pos > parser.len)
        throw Error("JSON parse error: unexpected end of input at position " parser.pos)
    char := SubStr(parser.text, parser.pos, 1)
    parser.pos += 1
    return char
}

__JsonParseValue(parser) {
    __JsonSkipWhitespace(parser)
    if (parser.pos > parser.len)
        throw Error("JSON parse error: incomplete value at position " parser.pos)
    char := __JsonPeekChar(parser)
    if (char = "{")
        return __JsonParseObject(parser)
    if (char = "[")
        return __JsonParseArray(parser)
    if (char = '"')
        return __JsonParseString(parser)
    if (char = "-" || (char >= "0" && char <= "9"))
        return __JsonParseNumber(parser)
    if (SubStr(parser.text, parser.pos, 4) = "true")
        return __JsonParseLiteral(parser, "true", true)
    if (SubStr(parser.text, parser.pos, 5) = "false")
        return __JsonParseLiteral(parser, "false", false)
    if (SubStr(parser.text, parser.pos, 4) = "null")
        return __JsonParseLiteral(parser, "null", "")
    throw Error("JSON parse error: unexpected character '" . char . "' at position " parser.pos)
}

__JsonParseLiteral(parser, literal, value) {
    if (SubStr(parser.text, parser.pos, StrLen(literal)) != literal)
        throw Error("JSON parse error: invalid literal at position " parser.pos)
    parser.pos += StrLen(literal)
    return value
}

__JsonParseObject(parser) {
    __JsonConsumeChar(parser) ; skip '{'
    obj := Map()
    __JsonSkipWhitespace(parser)
    if (__JsonPeekChar(parser) = "}") {
        parser.pos += 1
        return obj
    }
    while (true) {
        __JsonSkipWhitespace(parser)
        if (__JsonPeekChar(parser) != '"')
            throw Error("JSON parse error: expected string key at position " parser.pos)
        key := __JsonParseString(parser)
        __JsonSkipWhitespace(parser)
        if (__JsonConsumeChar(parser) != ":")
            throw Error("JSON parse error: expected ':' at position " parser.pos)
        value := __JsonParseValue(parser)
        obj[key] := value
        __JsonSkipWhitespace(parser)
        delimiter := __JsonConsumeChar(parser)
        if (delimiter = "}")
            break
        if (delimiter != ",")
            throw Error("JSON parse error: expected ',' at position " parser.pos)
    }
    return obj
}

__JsonParseArray(parser) {
    __JsonConsumeChar(parser) ; skip '['
    arr := []
    __JsonSkipWhitespace(parser)
    if (__JsonPeekChar(parser) = "]") {
        parser.pos += 1
        return arr
    }
    while (true) {
        value := __JsonParseValue(parser)
        arr.Push(value)
        __JsonSkipWhitespace(parser)
        delimiter := __JsonConsumeChar(parser)
        if (delimiter = "]")
            break
        if (delimiter != ",")
            throw Error("JSON parse error: expected ',' at position " parser.pos)
    }
    return arr
}

__JsonParseString(parser) {
    __JsonConsumeChar(parser) ; skip opening quote
    result := ""
    while (parser.pos <= parser.len) {
        char := __JsonConsumeChar(parser)
        if (char = '"')
            return result
        if (char = "\")
            result .= __JsonParseEscape(parser)
        else
            result .= char
    }
    throw Error("JSON parse error: unterminated string literal")
}

__JsonParseEscape(parser) {
    esc := __JsonConsumeChar(parser)
    if (esc = '"' || esc = "\" || esc = "/")
        return esc
    if (esc = "b")
        return Chr(8)
    if (esc = "f")
        return Chr(12)
    if (esc = "n")
        return "`n"
    if (esc = "r")
        return "`r"
    if (esc = "t")
        return "`t"
    if (esc = "u")
        return __JsonParseUnicodeEscape(parser)
    throw Error("JSON parse error: invalid escape sequence '\\" . esc . "' at position " parser.pos)
}

__JsonParseUnicodeEscape(parser) {
    if (parser.pos + 3 > parser.len)
        throw Error("JSON parse error: incomplete unicode escape at position " parser.pos)
    hex := SubStr(parser.text, parser.pos, 4)
    if !RegExMatch(hex, "^[0-9A-Fa-f]{4}$")
        throw Error("JSON parse error: invalid unicode escape at position " parser.pos)
    code := ("0x" . hex) + 0
    parser.pos += 4
    if (code >= 0xD800 && code <= 0xDBFF) {
        if (parser.pos + 5 <= parser.len && SubStr(parser.text, parser.pos, 2) = "\u") {
            parser.pos += 2
            hex2 := SubStr(parser.text, parser.pos, 4)
            if !RegExMatch(hex2, "^[0-9A-Fa-f]{4}$")
                throw Error("JSON parse error: invalid unicode escape at position " parser.pos)
            low := ("0x" . hex2) + 0
            parser.pos += 4
            if (low < 0xDC00 || low > 0xDFFF)
                throw Error("JSON parse error: invalid surrogate pair at position " parser.pos)
            code := 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00)
        }
    }
    return Chr(code)
}

__JsonParseNumber(parser) {
    start := parser.pos
    if (__JsonPeekChar(parser) = "-")
        parser.pos += 1
    __JsonParseDigits(parser)
    hasDecimal := false
    if (__JsonPeekChar(parser) = ".") {
        hasDecimal := true
        parser.pos += 1
        __JsonParseDigits(parser)
    }
    nextChar := __JsonPeekChar(parser)
    if (nextChar = "e" || nextChar = "E") {
        hasDecimal := true
        parser.pos += 1
        sign := __JsonPeekChar(parser)
        if (sign = "+" || sign = "-")
            parser.pos += 1
        __JsonParseDigits(parser)
    }
    numberText := SubStr(parser.text, start, parser.pos - start)
    ; Use Integer() or Float() for proper AHK v2 conversion
    if (hasDecimal)
        return Float(numberText)
    else
        return Integer(numberText)
}

__JsonParseDigits(parser) {
    start := parser.pos
    while (parser.pos <= parser.len) {
        char := __JsonPeekChar(parser)
        if (char < "0" || char > "9")
            break
        parser.pos += 1
    }
    if (start = parser.pos)
        throw Error("JSON parse error: expected digit at position " parser.pos)
}

; Read HTTP response body as UTF-8 text to avoid mojibake on smart quotes, etc.
GetUtf8Response(http) {
    stream := ComObject("ADODB.Stream")
    try {
        stream.Type := 1                      ; binary mode
        stream.Open()
        stream.Write(http.ResponseBody)
        stream.Position := 0
        stream.Type := 2                      ; text mode
        stream.Charset := "utf-8"
        return stream.ReadText()
    } finally {
        try {
            if (stream.State = 1)
                stream.Close()
        }
    }
}

; ALTERNATIVE PARSER 1: Regex-based (FASTEST - no object parsing overhead)
JsonUnescapeResponseText(text) {
    ; Decode surrogate pairs first so emoji and other non-BMP characters survive.
    while RegExMatch(text, "\\u([dD][89ABab][0-9A-Fa-f]{2})\\u([dD][c-fC-F][0-9A-Fa-f]{2})", &pairMatch) {
        high := Integer("0x" . pairMatch[1])
        low := Integer("0x" . pairMatch[2])
        codepoint := 0x10000 + ((high - 0xD800) << 10) + (low - 0xDC00)
        text := StrReplace(text, pairMatch[0], Chr(codepoint), , , 1)
    }

    while RegExMatch(text, "\\u([0-9A-Fa-f]{4})", &unicodeMatch) {
        codepoint := Integer("0x" . unicodeMatch[1])
        text := StrReplace(text, unicodeMatch[0], Chr(codepoint), , , 1)
    }

    text := StrReplace(text, '\"', '"')
    text := StrReplace(text, "\n", "`n")
    text := StrReplace(text, "\r", "`r")
    text := StrReplace(text, "\t", "`t")
    text := StrReplace(text, "\/", "/")
    text := StrReplace(text, "\\", "\")
    return text
}

; Extracts text directly from JSON response using regex
ExtractTextFromResponseRegex(jsonResponse) {
    ; Match any output_text block anywhere in the output array
    if RegExMatch(jsonResponse, 's)"type"\s*:\s*"output_text"[^}]*"text"\s*:\s*"((?:[^"\\]|\\.)*)"', &match) {
        return JsonUnescapeResponseText(match[1])
    }
    return ""
}

FinalizeRun(logData) {
    if (!logData.HasOwnProp("pasteTime") || logData.pasteTime = 0)
        logData.pasteTime := A_TickCount
    finalizeQueueStart := A_TickCount
    snapshot := logData.Clone()
    if (snapshot.HasOwnProp("diagnostics") && IsObject(snapshot.diagnostics))
        snapshot.diagnostics.finalizeQueueMs := A_TickCount - finalizeQueueStart
    SetTimer(() => LogDetailed(snapshot), -1)
}

^!u::                                  ; hotkey Ctrl+Alt+U
{
    ; Initialize timing and logging data
    startTime := A_TickCount
    logData := {
        original: "",
        rawAiOutput: "",
        result: "",
        rawRequest: "",
        rawResponse: "",
        pastedText: "",
        replacementsApplied: [],
        promptLeakGuard: {
            triggered: false,
            occurrences: 0,
            textInputRemoved: false,
            removedChars: 0,
            beforeLength: 0,
            afterLength: 0
        },
        error: "",
        startTime: startTime,
        pasteTime: 0,
        timestamp: FormatTime(, "yyyy-MM-dd HH:mm:ss"),
        pasteAttempted: false,
        events: [],
        timings: {
            clipboardCaptured: 0,
            payloadPrepared: 0,
            requestSent: 0,
            responseReceived: 0,
            textParsed: 0,
            replacementsApplied: 0,
            promptGuardApplied: 0,
            textPasted: 0
        },
        diagnostics: {
            windowDebugMs: 0,
            clipboardDebugMs: 0,
            clipboardProbeMs: 0,
            payloadDebugMs: 0,
            responseDebugMs: 0,
            outputDebugMs: 0,
            pasteDebugMs: 0,
            exceptionDebugMs: 0,
            finalizeQueueMs: 0
        },
        model: apiModel,
        scriptVersion: scriptVersion,
        activeApp: "",
        activeExe: "",
        windowClass: "",
        focusedControl: "",
        windowHwnd: 0,
        pasteMethod: "",
        clipboardSource: "",
        clipboardPreview: "",
        outputPreview: "",
        modelVersion: "",
        textChanged: false,
        tokenInput: 0,
        tokenOutput: 0,
        tokenTotal: 0,
        tokenCached: 0,
        tokenReasoning: 0
    }

    ; Capture active window before any clipboard operations
    windowDebugStart := A_TickCount
    windowInfo := GetActiveWindowDebugInfo()
    logData.activeApp := windowInfo.title
    logData.activeExe := windowInfo.exe
    logData.windowClass := windowInfo.class
    logData.focusedControl := windowInfo.focusedControl
    logData.windowHwnd := windowInfo.hwnd

    try {
        logData.events.Push("Active window: exe=" . logData.activeExe
            . ", class=" . (logData.windowClass != "" ? logData.windowClass : "<none>")
            . ", hwnd=" . logData.windowHwnd
            . ", control=" . (logData.focusedControl != "" ? logData.focusedControl : "<none>")
            . ", title=" . MakeLogPreview(logData.activeApp, 120))
        logData.diagnostics.windowDebugMs := A_TickCount - windowDebugStart

        replacementsStatus := ""
        replacementsDetails := ""
        deferredReplacementsReload := RefreshReplacementsIfChanged(&replacementsStatus, &replacementsDetails)
        if (replacementsStatus = "reloaded")
            logData.events.Push("Replacements metadata changed; immediate reload succeeded (" . replacementsDetails . ")")
        else if (replacementsStatus = "reload_failed") {
            logData.events.Push("Replacements metadata changed; immediate reload failed; keeping last known-good cache (" . replacementsDetails . ")")
            logData.events.Push("Deferred replacements reload scheduled after paste")
        } else if (replacementsStatus = "missing") {
            logData.events.Push("Replacements file missing; cleared replacements cache (" . replacementsDetails . ")")
        } else if (replacementsStatus = "metadata_error") {
            logData.events.Push("Replacements metadata check failed; keeping last known-good cache (" . replacementsDetails . ")")
            logData.events.Push("Deferred replacements reload scheduled after paste")
        } else
            logData.events.Push("Replacements metadata unchanged; using cached replacements (" . replacementsDetails . ")")

        originalText := ""
        clipboardDetails := ""
        if !CaptureSelectedText(logData.activeExe, &originalText, logData.events, &clipboardDetails) {
            logData.error := "Clipboard wait timeout"
            logData.pasteTime := A_TickCount
            logData.events.Push("Clipboard wait timed out after configured copy attempts for " . logData.activeExe)
            return
        }

        if (SetClipboardHistoryPolicy(false, false))
            logData.events.Push("Clipboard source marked as transient (history/cloud excluded)")
        else
            logData.events.Push("Clipboard source history-policy tag unavailable")
        logData.original := originalText
        clipboardDebugStart := A_TickCount
        if (IsObject(clipboardDetails))
            logData.clipboardSource := clipboardDetails.selectedFormat
        logData.clipboardPreview := MakeLogPreview(originalText, 220)
        logData.events.Push("Clipboard details: " . BuildClipboardDebugSummary(clipboardDetails))
        logData.events.Push("Clipboard preview: " . logData.clipboardPreview)
        logData.diagnostics.clipboardDebugMs := A_TickCount - clipboardDebugStart
        if (IsObject(clipboardDetails) && clipboardDetails.HasOwnProp("probeMs"))
            logData.diagnostics.clipboardProbeMs := clipboardDetails.probeMs
        logData.events.Push("Clipboard captured (" . (StrLen(originalText)) . " chars)")
        logData.timings.clipboardCaptured := A_TickCount

        ; OpenAI API call
        apiKey := "REDACTED"
        
        ; Create the prompt from shared instruction text
        prompt := "instructions: " . promptInstructionText . "`ntext input: " . originalText
       
        ; Create JSON payload for Responses API (store + text verbosity + model-specific controls)
        escapedPrompt := JsonEscape(prompt)
        jsonPayload := '{"model":"' . apiModel . '","input":[{"role":"user","content":[{"type":"input_text","text":"' . escapedPrompt . '"}]}],"store":true,"text":{"verbosity":"' . Verbosity . '"}'
        if (apiUsesReasoning) {
            jsonPayload .= ',"reasoning":{"effort":"' . reasoningEffort . '","summary":"' . reasoningSummary . '"}}'
            logData.events.Push("Payload prepared for " . apiModel . " (verbosity: " . Verbosity . ", reasoning: " . reasoningEffort . "/" . reasoningSummary . ")")
        } else {
            jsonPayload .= ',"temperature":' . Temperature . '}'
            logData.events.Push("Payload prepared for " . apiModel . " (verbosity: " . Verbosity . ", temperature: " . Temperature . ")")
        }
        payloadDebugStart := A_TickCount
        logData.events.Push("Prompt chars=" . StrLen(prompt) . ", payload chars=" . StrLen(jsonPayload))
        logData.diagnostics.payloadDebugMs := A_TickCount - payloadDebugStart
        logData.timings.payloadPrepared := A_TickCount
        logData.rawRequest := jsonPayload

        http := ComObject("WinHttp.WinHttpRequest.5.1")
        http.SetTimeouts(5000, 5000, 30000, 30000)  ; timeouts in milliseconds
        http.Open("POST", apiUrl, false)
        http.SetRequestHeader("Content-Type", "application/json; charset=utf-8")
        http.SetRequestHeader("Authorization", "Bearer " . apiKey)
        logData.timings.requestSent := A_TickCount
        http.Send(jsonPayload)
        logData.events.Push("Request sent")
       
        ; Check for successful response
        if (http.Status != 200) {
            logData.error := "API Error: " . http.Status . " - " . http.StatusText
            logData.pasteTime := A_TickCount
            logData.events.Push("API error encountered: " . http.Status)

            errPreview := ""
            try {
                errPreview := GetUtf8Response(http)
            } catch {
                try {
                    errPreview := http.ResponseText
                } catch {
                    errPreview := ""
                }
            }

            if (errPreview != "") {
                logData.rawResponse := SubStr(errPreview, 1, 1000)
                logData.events.Push("API error body captured (" . StrLen(logData.rawResponse) . " chars)")
            }

            ShowTemporaryToolTip("API Error: " . http.Status . " - " . http.StatusText . "`nResponse: " . SubStr(errPreview, 1, 200), 5000)
            return
        }
       
        ; Parse response and extract corrected text
        response := GetUtf8Response(http)
        logData.rawResponse := response
        responseDebugStart := A_TickCount
        logData.events.Push("Response received (" . StrLen(response) . " chars)")
        logData.timings.responseReceived := A_TickCount

        ; Extract model version and token counts from API response
        if RegExMatch(response, '"model"\s*:\s*"([^"]+)"', &m)
            logData.modelVersion := m[1]
        if RegExMatch(response, '"input_tokens"\s*:\s*(\d+)', &m)
            logData.tokenInput := Integer(m[1])
        if RegExMatch(response, '"output_tokens"\s*:\s*(\d+)', &m)
            logData.tokenOutput := Integer(m[1])
        if RegExMatch(response, '"total_tokens"\s*:\s*(\d+)', &m)
            logData.tokenTotal := Integer(m[1])
        if RegExMatch(response, '"cached_tokens"\s*:\s*(\d+)', &m)
            logData.tokenCached := Integer(m[1])
        if RegExMatch(response, '"reasoning_tokens"\s*:\s*(\d+)', &m)
            logData.tokenReasoning := Integer(m[1])
        logData.events.Push("API response metadata: model="
            . (logData.modelVersion != "" ? logData.modelVersion : "<missing>")
            . ", tokens in/out/total/cached/reasoning="
            . logData.tokenInput . "/" . logData.tokenOutput . "/" . logData.tokenTotal . "/" . logData.tokenCached . "/" . logData.tokenReasoning)
        logData.diagnostics.responseDebugMs := A_TickCount - responseDebugStart

        correctedText := ""

        ; TRY METHOD 1: Regex-based extraction (FASTEST, most reliable)
        try {
            logData.events.Push("DEBUG: Trying regex extraction")
            correctedText := ExtractTextFromResponseRegex(response)
            if (correctedText != "") {
                logData.events.Push("DEBUG: Regex extraction SUCCESS, length=" . StrLen(correctedText))
                logData.timings.textParsed := A_TickCount
            } else {
                logData.events.Push("DEBUG: Regex extraction returned empty")
            }
        } catch Error as regexErr {
            logData.events.Push("DEBUG: Regex extraction failed: " . regexErr.Message)
        }

        ; TRY METHOD 2: Map-based parsing (if regex failed)
        if (correctedText = "") {
            try {
                logData.events.Push("DEBUG: Trying Map-based parsing")
                responseObj := JsonLoad(response)
                logData.events.Push("DEBUG: JsonLoad complete, type=" . Type(responseObj))

                if (responseObj.Has("output")) {
                    logData.events.Push("DEBUG: Has output property")
                    outputChunks := responseObj["output"]
                    logData.events.Push("DEBUG: Got output, type=" . Type(outputChunks))

                    if (IsObject(outputChunks)) {
                        logData.events.Push("DEBUG: output is object, length=" . outputChunks.Length)
                        chunkIndex := 0
                        for , chunk in outputChunks {
                            chunkIndex++
                            logData.events.Push("DEBUG: Processing chunk #" . chunkIndex . ", type=" . Type(chunk))

                            if !(IsObject(chunk) && chunk.Has("content"))
                                continue

                            logData.events.Push("DEBUG: Chunk has content")
                            contentPieces := chunk["content"]
                            logData.events.Push("DEBUG: Got content, type=" . Type(contentPieces))

                            if !IsObject(contentPieces)
                                continue

                            pieceIndex := 0
                            for , piece in contentPieces {
                                pieceIndex++
                                logData.events.Push("DEBUG: Processing piece #" . pieceIndex . ", type=" . Type(piece))

                                if (IsObject(piece) && piece.Has("text")) {
                                    textValue := piece["text"]
                                    logData.events.Push("DEBUG: Found text: " . SubStr(textValue, 1, 50))
                                    correctedText .= textValue
                                }
                            }
                        }
                    }
                } else {
                    logData.events.Push("DEBUG: No output property found")
                }

                if (correctedText != "") {
                    logData.events.Push("DEBUG: Map-based parsing SUCCESS, length=" . StrLen(correctedText))
                    logData.timings.textParsed := A_TickCount
                } else {
                    logData.events.Push("DEBUG: Map-based parsing returned empty")
                }
            } catch Error as parseErr {
                logData.events.Push("JSON parse error: " . parseErr.Message . " at line " . parseErr.Line)
                logData.events.Push("DEBUG: Error.What=" . parseErr.What . ", Error.Extra=" . parseErr.Extra)
            }
        }
        
        ; Apply post-processing replacements + safeguard cleanup (instant string passes)
        if (correctedText != "") {
            logData.rawAiOutput := correctedText
            applied := []
            urlCount := 0
            correctedText := ApplyReplacements(correctedText, &applied, &urlCount)
            logData.replacementsApplied := applied
            logData.urlsProtected := urlCount
            logData.timings.replacementsApplied := A_TickCount
            if (applied.Length > 0)
                logData.events.Push("Post-processing: " . applied.Length . " replacement(s) applied: " . applied[1] . (applied.Length > 1 ? " (+" . (applied.Length - 1) . " more)" : "") . (urlCount > 0 ? " (" . urlCount . " URL(s) protected)" : ""))
            else
                logData.events.Push("Post-processing: no replacements matched" . (urlCount > 0 ? " (" . urlCount . " URL(s) protected)" : ""))

            guardDetails := {}
            guardInput := correctedText
            correctedText := StripPromptLeak(correctedText, promptInstructionText, &guardDetails)
            logData.promptLeakGuard := guardDetails
            logData.timings.promptGuardApplied := A_TickCount

            if (guardDetails.triggered) {
                beforePreview := StrReplace(StrReplace(SubStr(guardInput, 1, 120), "`r", " "), "`n", " ")
                afterPreview := StrReplace(StrReplace(SubStr(correctedText, 1, 120), "`r", " "), "`n", " ")
                logData.events.Push("Prompt-leak safeguard TRIGGERED (removed " . guardDetails.occurrences . " occurrence(s), text input label removed: " . (guardDetails.textInputRemoved ? "yes" : "no") . ", " . guardDetails.removedChars . " chars, " . guardDetails.beforeLength . " -> " . guardDetails.afterLength . ")")
                logData.events.Push("Prompt-leak safeguard before: " . beforePreview)
                logData.events.Push("Prompt-leak safeguard after: " . afterPreview)
            } else {
                logData.events.Push("Prompt-leak safeguard: no leak pattern matched")
            }
        }

        if (correctedText != "") {
            logData.textChanged := (correctedText != logData.original)
            outputDebugStart := A_TickCount
            logData.outputPreview := MakeLogPreview(correctedText, 220)
            logData.events.Push("Correction summary: changed="
                . (logData.textChanged ? "yes" : "no")
                . ", input chars=" . StrLen(logData.original)
                . ", output chars=" . StrLen(correctedText)
                . ", delta=" . (StrLen(correctedText) - StrLen(logData.original)))
            logData.events.Push("Output preview: " . logData.outputPreview)
            logData.diagnostics.outputDebugMs := A_TickCount - outputDebugStart

            if (UseSendTextForExe(logData.activeExe)) {
                logData.pasteMethod := "sendtext"
                ; Type the corrected text directly (replaces current selection)
                logData.events.Push("INSERTION METHOD: SendText (direct typing)")
                logData.pasteAttempted := true
                SendText(correctedText)
                ; Optionally mirror to clipboard for user convenience
                SetTransientClipboardText(correctedText)
                logData.events.Push("Text typed via SendText - COMPLETE")
            } else {
                logData.pasteMethod := "clipboard"
                ; Default: paste via clipboard
                logData.events.Push("INSERTION METHOD: Clipboard paste (Ctrl+V)")
                pasteDebugStart := A_TickCount
                logData.events.Push("Paste hotkey state before release wait: " . GetHotkeyPhysicalState())
                if WaitForHotkeyRelease(120)
                    logData.events.Push("Paste hotkey-release wait completed before Ctrl+V")
                else {
                    logData.events.Push("Paste hotkey-release wait timed out before Ctrl+V")
                    if (StrLower(logData.activeExe) = "notepad.exe") {
                        logData.events.Push("Paste aborted because hotkey keys never released before Ctrl+V")
                        AbortRun(logData, "Paste hotkey release timeout", "", "Release Ctrl+Alt+U fully, then retry")
                        return
                    }
                    logData.events.Push("Paste continuing despite hotkey-release timeout for standard app")
                }
                SetTransientClipboardText(correctedText)
                logData.events.Push("Clipboard updated for paste (" . StrLen(correctedText) . " chars)")
                logData.diagnostics.pasteDebugMs := A_TickCount - pasteDebugStart
                logData.pasteAttempted := true
                Send("^v")
                logData.events.Push("Text pasted via clipboard - COMPLETE")
            }
            
            ; Capture paste timing and log success
            logData.pasteTime := A_TickCount
            logData.timings.textPasted := A_TickCount
            logData.result := correctedText
            logData.pastedText := correctedText

            if (deferredReplacementsReload)
                RetryReplacementsReloadAfterPaste(logData.events)
        } else {
            logData.error := "Responses API returned no text"
            logData.pasteTime := A_TickCount
            logData.events.Push("Response parsing failed")
            logData.events.Push("Response preview: " . MakeLogPreview(response, 260))
            ShowTemporaryToolTip("Error: No text returned by Responses API")
        }
       
    } catch Error as e {
        logData.error := "Exception: " . e.Message
        if (!logData.pasteTime)
            logData.pasteTime := A_TickCount
        exceptionDebugStart := A_TickCount
        logData.events.Push("Exception thrown: " . e.Message)
        logData.events.Push("Exception context: line=" . e.Line . ", what=" . e.What . ", extra=" . e.Extra)
        logData.diagnostics.exceptionDebugMs := A_TickCount - exceptionDebugStart
        ShowTemporaryToolTip("Error: " . e.Message)
    } finally {
        FinalizeRun(logData)
    }
    return
}
