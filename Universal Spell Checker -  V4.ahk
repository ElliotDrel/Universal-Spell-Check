#Requires AutoHotkey v2.0

; Logging configuration
enableLogging := true
detailedLogPath := A_ScriptDir . "\logs\spellcheck-detailed.log"
maxLogSize := 5000000  ; 5MB max per log file

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

    try {
        if DllCall("IsClipboardFormatAvailable", "uint", CF_UNICODETEXT) {
            if (hData := DllCall("GetClipboardData", "uint", CF_UNICODETEXT, "ptr")) {
                if (pData := DllCall("GlobalLock", "ptr", hData, "ptr")) {
                    text := StrGet(pData, , "UTF-16")
                    DllCall("GlobalUnlock", "ptr", hData)
                    return text
                }
            }
        }
        if DllCall("IsClipboardFormatAvailable", "uint", CF_TEXT) {
            if (hData := DllCall("GetClipboardData", "uint", CF_TEXT, "ptr")) {
                if (pData := DllCall("GlobalLock", "ptr", hData, "ptr")) {
                    ; Decode legacy ANSI bytes with Windows-1252 so smart quotes survive UTF-8 locale
                    text := StrGet(pData, , "CP1252")
                    DllCall("GlobalUnlock", "ptr", hData)
                    return text
                }
            }
        }
    } finally {
        DllCall("CloseClipboard")
    }

    return text
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
       
        ; Create JSON payload for Responses API
        escapedPrompt := JsonEscape(prompt)
        jsonPayload := '{"model":"gpt-4.1","input":[{"role":"user","content":[{"type":"input_text","text":"' . escapedPrompt . '"}]}],"temperature":0.3}'
        logData.events.Push("Payload prepared")

        http := ComObject("WinHttp.WinHttpRequest.5.1")
        http.SetTimeouts(5000, 5000, 30000, 30000)  ; timeouts in milliseconds
        http.Open("POST", "https://api.openai.com/v1/responses", false)
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
                errPreview := SubStr(GetUtf8Response(http), 1, 200)
            } catch {
                try {
                    errPreview := SubStr(http.ResponseText, 1, 200)
                } catch {
                    errPreview := ""
                }
            }
            ToolTip("API Error: " . http.Status . " - " . http.StatusText . "`nResponse: " . errPreview)
            SetTimer(() => ToolTip(), -5000)
            return
        }
       
        ; Parse response and extract corrected text
        response := GetUtf8Response(http)
        logData.rawResponse := response
        logData.events.Push("Response received")
       
        correctedText := ""
        if (RegExMatch(response, '"output_text"\s*:\s*\[\s*"((?:[^"\\]|\\.)*)"', &match)) {
            correctedText := match[1]
        } else {
            lastPos := 0
            while (RegExMatch(response, '"text"\s*:\s*"((?:[^"\\]|\\.)*)"', &match, lastPos + 1)) {
                correctedText := match[1]
                lastPos := match.Pos
            }
        }
        
        if (correctedText != "") {
           
            ; Unescape JSON - fix for single backslash sequences
            correctedText := StrReplace(correctedText, "\n", "`n")
            correctedText := StrReplace(correctedText, "\r", "`r")
            correctedText := StrReplace(correctedText, "\t", "`t")
            correctedText := StrReplace(correctedText, '\"', '"')
           
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
