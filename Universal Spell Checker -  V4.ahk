#Requires AutoHotkey v2.0

; Logging configuration
enableLogging := true
detailedLogPath := A_ScriptDir . "\logs\spellcheck-detailed.log"
maxLogSize := 1000000  ; 5MB max per log file

; API configuration
apiModel := "gpt-5-mini"
reasoningEffort := "minimal"   ; GPT-5 mini uses minimal/low/medium/high and GPT-5.1 uses none/low/medium/high
reasoningSummary := "auto"  ; let model decide summary behavior
Verbosity := "low"      ; concise output per Responses API text config
apiUrl := "https://api.openai.com/v1/responses"

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
            return Trim(doc.body.innerText, "`r`n")
    } catch {
        ; Fall through to caller so it can try Unicode/plain text representations
    }
    return ""
}

; Log rotation function
RotateLogIfNeeded(logPath, maxSize) {
    try {
        if (FileExist(logPath)) {
            fileInfo := FileGetSize(logPath)
            if (fileInfo > maxSize) {
                ; Create archive filename with timestamp
                timestamp := FormatTime(, "yyyy-MM-dd-HHmmss")
                SplitPath(logPath, &fileName, &dir, &ext, &nameNoExt)
                archivePath := dir . "\" . nameNoExt . "-" . timestamp . "." . ext
                
                ; Rename current log to archive
                FileMove(logPath, archivePath, true)
            }
        }
    } catch {
        ; Silently fail if rotation doesn't work
    }
}

; Detailed logging function
LogDetailed(data) {
    global enableLogging, detailedLogPath, maxLogSize
    
    if (!enableLogging)
        return
    
    try {
        ; Rotate log if needed
        RotateLogIfNeeded(detailedLogPath, maxLogSize)
        
        duration := data.pasteTime - data.startTime
        status := data.error ? "ERROR: " . data.error : "SUCCESS"
        errorMarker := data.error ? "[ERROR] " : ""
        
        ; Indent text content for readability
        inputText := "  " . StrReplace(data.original, "`n", "`n  ")
        outputText := "  " . StrReplace(data.result, "`n", "`n  ")
        pastedText := "  " . StrReplace(data.pastedText, "`n", "`n  ")
        
        ; Format API response with indentation
        rawResponse := data.rawResponse
        if (rawResponse != "") {
            rawResponse := "  " . StrReplace(rawResponse, "`n", "`n  ")
        }
        
        ; Build detailed log entry
        entry := ""
        entry .= "================================================================================`n"
        entry .= errorMarker . "RUN: " . data.timestamp . "`n"
        entry .= "================================================================================`n"
        entry .= "Status: " . status . "`n"
        entry .= "Duration: " . duration . "ms`n"
        entry .= "`n"
        entry .= "Input Text:`n"
        entry .= inputText . "`n"
        entry .= "`n"
        entry .= "Output Text:`n"
        entry .= outputText . "`n"
        entry .= "`n"
        
        if (rawResponse != "") {
            entry .= "API Response:`n"
            entry .= rawResponse . "`n"
            entry .= "`n"
        }

        if (data.HasOwnProp("events") && IsObject(data.events) && data.events.Length) {
            entry .= "Events:`n"
            for idx, event in data.events {
                entry .= "  " . event . "`n"
            }
            entry .= "`n"
        }
        
        entry .= "Pasted Text:`n"
        entry .= pastedText . "`n"
        entry .= "`n"
        entry .= "================================================================================`n"
        entry .= "`n"
        
        FileAppend(entry, detailedLogPath)
    } catch {
        ; Silently fail if logging doesn't work - never break core functionality
    }
}

; JSON escape function for proper escaping
JsonEscape(str) {
    str := StrReplace(str, "\", "\\")
    str := StrReplace(str, '"', '\"')
    str := StrReplace(str, "`n", "\n")
    str := StrReplace(str, "`r", "\r")
    str := StrReplace(str, "`t", "\t")
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

; ALTERNATIVE PARSER 2: Object-based with dynamic properties (COMPATIBLE)
; Uses basic Object instead of Map for better AHK v2 compatibility
ExtractTextFromResponseObject(jsonResponse) {
    try {
        ; This uses the existing JsonLoad but expects Object not Map
        responseObj := JsonLoad(jsonResponse)

        ; Try multiple access patterns for compatibility
        try {
            if (responseObj.HasOwnProp("output")) {
                output := responseObj.output
                if (IsObject(output) && output.Length > 0) {
                    chunk := output[1]  ; 1-indexed
                    if (IsObject(chunk) && chunk.HasOwnProp("content")) {
                        content := chunk.content
                        if (IsObject(content) && content.Length > 0) {
                            piece := content[1]
                            if (IsObject(piece) && piece.HasOwnProp("text"))
                                return piece.text
                        }
                    }
                }
            }
        }
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
        result: "",
        rawResponse: "",
        pastedText: "",
        error: "",
        startTime: startTime,
        pasteTime: 0,
        timestamp: FormatTime(, "yyyy-MM-dd HH:mm:ss"),
        pasteAttempted: false,
        events: []
    }

    try {
        A_Clipboard := ""                  ; clear clipboard
        Send("^c")                         ; copy selection
        if !ClipWait(1) {
            logData.error := "Clipboard wait timeout"
            logData.pasteTime := A_TickCount
            logData.events.Push("Clipboard wait timed out")
            return
        }

        originalText := GetClipboardText()  ; store original text before processing
        logData.original := originalText
        logData.events.Push("Clipboard captured (" . (StrLen(originalText)) . " chars)")
       
        ; OpenAI API call
        apiKey := "REDACTED"
        
        ; Create the prompt (same as Python file)
        prompt := "instructions: Fix the grammar and spelling of the text below. Preserve all formatting, line breaks, and special characters. Do not add or remove any content. Return only the corrected text. `ntext input: " . originalText
       
        ; Create JSON payload for Responses API (store + text verbosity + reasoning)
        escapedPrompt := JsonEscape(prompt)
        jsonPayload := '{"model":"' . apiModel . '","input":[{"role":"user","content":[{"type":"input_text","text":"' . escapedPrompt . '"}]}],"store":true,"text":{"verbosity":"' . Verbosity . '"},"reasoning":{"effort":"' . reasoningEffort . '","summary":"' . reasoningSummary . '"}}'
        logData.events.Push("Payload prepared for " . apiModel . " (verbosity: " . Verbosity . ", reasoning: " . reasoningEffort . "/" . reasoningSummary . ")")

        http := ComObject("WinHttp.WinHttpRequest.5.1")
        http.SetTimeouts(5000, 5000, 30000, 30000)  ; timeouts in milliseconds
        http.Open("POST", apiUrl, false)
        http.SetRequestHeader("Content-Type", "application/json; charset=utf-8")
        http.SetRequestHeader("Authorization", "Bearer " . apiKey)
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
       
        correctedText := ""

        ; TRY METHOD 1: Regex-based extraction (FASTEST, most reliable)
        try {
            logData.events.Push("DEBUG: Trying regex extraction")
            correctedText := ExtractTextFromResponseRegex(response)
            if (correctedText != "") {
                logData.events.Push("DEBUG: Regex extraction SUCCESS, length=" . StrLen(correctedText))
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
                } else {
                    logData.events.Push("DEBUG: Map-based parsing returned empty")
                }
            } catch Error as parseErr {
                logData.events.Push("JSON parse error: " . parseErr.Message . " at line " . parseErr.Line)
                logData.events.Push("DEBUG: Error.What=" . parseErr.What . ", Error.Extra=" . parseErr.Extra)
            }
        }
        
        if (correctedText != "") {
           
            if (UseSendText()) {
                ; Type the corrected text directly (replaces current selection)
                logData.pasteAttempted := true
                SendText(correctedText)
                ; Optionally mirror to clipboard for user convenience
                A_Clipboard := correctedText
                logData.events.Push("Text typed via SendText")
            } else {
                ; Default: paste via clipboard
                A_Clipboard := correctedText
                logData.pasteAttempted := true
                Send("^v")
                logData.events.Push("Text pasted via clipboard")
            }
            
            ; Capture paste timing and log success
            logData.pasteTime := A_TickCount
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
