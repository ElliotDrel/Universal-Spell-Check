#Requires AutoHotkey v2.0

; Logging configuration
enableLogging := true
logDir := A_ScriptDir . "\logs"
logFilePrefix := "spellcheck"
maxLogSize := 5 * 1024 * 1024  ; 5 MiB max per weekly log file before suffix rollover

; Post-processing replacements
replacementsPath := A_ScriptDir . "\replacements.json"
postReplacements := []    ; Array of [variant, canonical] pairs, sorted longest-first

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

UseSendText() {
    global sendTextApps
    for exe in sendTextApps {
        if WinActive("ahk_exe " exe)
            return true
    }
    return false
}

; Load post-processing replacements from replacements.json.
; JSON format: { "canonical": ["variant1", "variant2", ...], ... }
; Builds a flat list of [variant, canonical] sorted longest-first so longer
; phrases are replaced before any shorter substring could interfere.
LoadReplacements() {
    global replacementsPath, postReplacements
    postReplacements := []

    if (!FileExist(replacementsPath))
        return

    try {
        pairs := []
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
        postReplacements := pairs


    } catch {
        ; Silently fail - replacements are optional
    }
}

; Apply post-processing replacements to AI output. Runs in microseconds.
; &applied receives a list of "variant->canonical" strings for every replacement that fired.
; URLs are protected: extracted before replacements, restored after.
ApplyReplacements(text, &applied, &urlCount) {
    global postReplacements
    applied := []
    urlCount := 0

    ; Extract URLs into placeholders so replacements don't break them
    ; Use A_TickCount prefix so placeholders can't collide with real text
    urls := []
    urlTag := "__URL_" . A_TickCount . "_"
    pos := 1
    while (pos := RegExMatch(text, "https?://\S+", &m, pos)) {
        urls.Push(m[0])
        placeholder := urlTag . urls.Length . "__"
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
        text := StrReplace(text, urlTag . i . "__", urls[i])
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
GetClipboardText() {
    ; Default to AutoHotkey's view in case the clipboard can't be opened
    text := A_Clipboard
    if !DllCall("OpenClipboard", "ptr", 0)
        return text

    static CF_TEXT := 1
    static CF_UNICODETEXT := 13
    static CF_HTML := DllCall("RegisterClipboardFormat", "str", "HTML Format", "uint")
    result := ""

    try {
        ; Prefer HTML when available so we can strip formatting noise (empty paragraphs, etc.)
        if (CF_HTML && DllCall("IsClipboardFormatAvailable", "uint", CF_HTML)) {
            htmlBlob := __ReadClipboardString(CF_HTML, "UTF-8")
            if (htmlBlob != "") {
                fragment := __ExtractHtmlFragment(htmlBlob)
                if (fragment != "") {
                    htmlText := __HtmlFragmentToPlainText(fragment)
                    if (htmlText != "")
                        result := htmlText
                }
            }
        }

        if (result = "" && DllCall("IsClipboardFormatAvailable", "uint", CF_UNICODETEXT)) {
            unicodeText := __ReadClipboardString(CF_UNICODETEXT, "UTF-16")
            if (unicodeText != "")
                result := unicodeText
        }

        if (result = "" && DllCall("IsClipboardFormatAvailable", "uint", CF_TEXT)) {
            ansiText := __ReadClipboardString(CF_TEXT, "CP1252")
            if (ansiText != "")
                result := ansiText
        }
    } finally {
        DllCall("CloseClipboard")
    }

    return result != "" ? result : text
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
        activeApp := data.HasOwnProp("activeApp") ? data.activeApp : ""
        activeExe := data.HasOwnProp("activeExe") ? data.activeExe : ""
        pasteMethod := data.HasOwnProp("pasteMethod") ? data.pasteMethod : ""
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
        j .= ',"model_version":"' . JsonEscape(modelVer) . '"'
        j .= ',"active_app":"' . JsonEscape(activeApp) . '"'
        j .= ',"active_exe":"' . JsonEscape(activeExe) . '"'
        j .= ',"paste_method":"' . JsonEscape(pasteMethod) . '"'
        j .= ',"text_changed":' . textChanged
        j .= ',"input_text":"' . JsonEscape(data.original) . '"'
        j .= ',"input_chars":' . StrLen(data.original)
        j .= ',"output_text":"' . JsonEscape(data.pastedText) . '"'
        j .= ',"output_chars":' . StrLen(data.pastedText)
        j .= ',"raw_ai_output":"' . JsonEscape(data.rawAiOutput) . '"'
        j .= ',"tokens":{"input":' . tokIn . ',"output":' . tokOut . ',"total":' . tokTotal . ',"cached":' . tokCached . ',"reasoning":' . tokReason . '}'
        j .= ',"timings":{"clipboard_ms":' . tClip . ',"payload_ms":' . tPayload . ',"request_ms":' . tReq . ',"api_ms":' . tApi . ',"parse_ms":' . tParse . ',"replacements_ms":' . tReplace . ',"prompt_guard_ms":' . tGuard . ',"paste_ms":' . tPaste . '}'
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
; Extracts text directly from JSON response using regex
ExtractTextFromResponseRegex(jsonResponse) {
    ; Match any output_text block anywhere in the output array
    if RegExMatch(jsonResponse, 's)"type"\s*:\s*"output_text"[^}]*"text"\s*:\s*"((?:[^"\\]|\\.)*)"', &match) {
        ; Unescape JSON string (in correct order to avoid double-unescaping)
        text := match[1]

        ; Handle Unicode escapes FIRST (before unescaping backslashes)
        while RegExMatch(text, "\\u([0-9A-Fa-f]{4})", &unicodeMatch) {
            codepoint := Integer("0x" . unicodeMatch[1])
            text := StrReplace(text, unicodeMatch[0], Chr(codepoint), , , 1)
        }

        ; Then handle standard JSON escapes
        text := StrReplace(text, '\"', '"')
        text := StrReplace(text, "\n", "`n")
        text := StrReplace(text, "\r", "`r")
        text := StrReplace(text, "\t", "`t")
        text := StrReplace(text, "\/", "/")
        text := StrReplace(text, "\\", "\")  ; Do backslash LAST

        return text
    }
    return ""
}

FinalizeRun(logData) {
    if (!logData.HasOwnProp("pasteTime") || logData.pasteTime = 0)
        logData.pasteTime := A_TickCount
    snapshot := logData.Clone()
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
        model: apiModel,
        activeApp: "",
        activeExe: "",
        pasteMethod: "",
        modelVersion: "",
        textChanged: false,
        tokenInput: 0,
        tokenOutput: 0,
        tokenTotal: 0,
        tokenCached: 0,
        tokenReasoning: 0
    }

    ; Capture active window before any clipboard operations
    logData.activeApp := WinGetTitle("A")
    try logData.activeExe := WinGetProcessName("A")
    catch
        logData.activeExe := "unknown"

    try {
        LoadReplacements()                 ; reload replacements.json on every run
        A_Clipboard := ""                  ; clear clipboard
        Send("^c")                         ; copy selection
        if !ClipWait(1) {
            logData.error := "Clipboard wait timeout"
            logData.pasteTime := A_TickCount
            logData.events.Push("Clipboard wait timed out")
            return
        }

        if (SetClipboardHistoryPolicy(false, false))
            logData.events.Push("Clipboard source marked as transient (history/cloud excluded)")
        else
            logData.events.Push("Clipboard source history-policy tag unavailable")

        originalText := GetClipboardText()  ; store original text before processing
        logData.original := originalText
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

            ToolTip("API Error: " . http.Status . " - " . http.StatusText . "`nResponse: " . SubStr(errPreview, 1, 200))
            SetTimer(() => ToolTip(), -5000)
            return
        }
       
        ; Parse response and extract corrected text
        response := GetUtf8Response(http)
        logData.rawResponse := response
        logData.events.Push("Response received")
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

            if (UseSendText()) {
                logData.pasteMethod := "sendtext"
                ; Type the corrected text directly (replaces current selection)
                logData.events.Push("INSERTION METHOD: SendText (direct typing)")
                logData.pasteAttempted := true
                SendText(correctedText)
                ; Optionally mirror to clipboard for user convenience
                A_Clipboard := correctedText
                logData.events.Push("Text typed via SendText - COMPLETE")
            } else {
                logData.pasteMethod := "clipboard"
                ; Default: paste via clipboard
                logData.events.Push("INSERTION METHOD: Clipboard paste (Ctrl+V)")
                A_Clipboard := correctedText
                logData.pasteAttempted := true
                Send("^v")
                logData.events.Push("Text pasted via clipboard - COMPLETE")
            }
            
            ; Capture paste timing and log success
            logData.pasteTime := A_TickCount
            logData.timings.textPasted := A_TickCount
            logData.result := correctedText
            logData.pastedText := correctedText
        } else {
            logData.error := "Responses API returned no text"
            logData.pasteTime := A_TickCount
            logData.events.Push("Response parsing failed")
            ToolTip("Error: No text returned by Responses API")
            SetTimer(() => ToolTip(), -3000)
        }
       
    } catch Error as e {
        logData.error := "Exception: " . e.Message
        if (!logData.pasteTime)
            logData.pasteTime := A_TickCount
        logData.events.Push("Exception thrown: " . e.Message)
        ToolTip("Error: " . e.Message)
        SetTimer(() => ToolTip(), -3000)
    } finally {
        FinalizeRun(logData)
    }
    return
}
