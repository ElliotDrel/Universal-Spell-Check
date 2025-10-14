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
        timestamp: FormatTime(, "yyyy-MM-dd HH:mm:ss")
    }
    
    A_Clipboard := ""                  ; clear clipboard
    Send("^c")                         ; copy selection
    if !ClipWait(1) {
        logData.error := "Clipboard wait timeout"
        logData.pasteTime := A_TickCount
        SetTimer(() => LogDetailed(logData), -1)
        return
    }

    originalText := A_Clipboard         ; store original text before processing
    logData.original := originalText
   
    ; OpenAI API call
    apiKey := "REDACTED"
   
    ; Create the prompt (same as Python file)
    prompt := "instructions: Fix the grammar and spelling of the text below. Preserve all formatting, line breaks, and special characters. Do not add or remove any content. Return only the corrected text. `ntext input: " . originalText
   
    ; Create JSON payload with proper escaping
    escapedPrompt := JsonEscape(prompt)
    jsonPayload := '{"model":"gpt-4.1","messages":[{"role":"user","content":"' . escapedPrompt . '"}],"temperature":0.3}'
   
    try {
        ; Make HTTP request with timeouts
        http := ComObject("WinHttp.WinHttpRequest.5.1")
        http.SetTimeouts(5000, 5000, 30000, 30000)  ; timeouts in milliseconds
        http.Open("POST", "https://api.openai.com/v1/chat/completions", false)
        http.SetRequestHeader("Content-Type", "application/json")
        http.SetRequestHeader("Authorization", "Bearer " . apiKey)
        http.Send(jsonPayload)
       
        ; Check for successful response
        if (http.Status != 200) {
            logData.error := "API Error: " . http.Status . " - " . http.StatusText
            logData.pasteTime := A_TickCount
            SetTimer(() => LogDetailed(logData), -1)
            ToolTip("API Error: " . http.Status . " - " . http.StatusText . "`nResponse: " . SubStr(http.ResponseText, 1, 200))
            SetTimer(() => ToolTip(), -5000)
            return
        }
       
        ; Parse response and extract corrected text
        response := http.ResponseText
        logData.rawResponse := response
       
        ; Optimized JSON parsing - capture the last "content" field (assistant's response)
        lastPos := 0
        while (RegExMatch(response, '"content"\s*:\s*"((?:[^"\\]|\\.)*)"', &match, lastPos + 1)) {
            correctedText := match[1]
            lastPos := match.Pos
        }
        
        if (lastPos > 0) {
           
            ; Unescape JSON - fix for single backslash sequences
            correctedText := StrReplace(correctedText, "\n", "`n")
            correctedText := StrReplace(correctedText, "\r", "`r")
            correctedText := StrReplace(correctedText, "\t", "`t")
            correctedText := StrReplace(correctedText, '\"', '"')
           
            ; Replace with corrected text
            if (correctedText != "") {
                if (UseSendText()) {
                    ; Type the corrected text directly (replaces current selection)
                    SendText(correctedText)
                    ; Optionally mirror to clipboard for user convenience
                    A_Clipboard := correctedText
                } else {
                    ; Default: paste via clipboard
                    A_Clipboard := correctedText
                    Send("^v")
                }
                
                ; Capture paste timing and log success
                logData.pasteTime := A_TickCount
                logData.result := correctedText
                logData.pastedText := correctedText
                SetTimer(() => LogDetailed(logData), -1)
            }
        } else {
            logData.error := "Could not parse API response"
            logData.pasteTime := A_TickCount
            SetTimer(() => LogDetailed(logData), -1)
            ToolTip("Error: Could not parse API response")
            SetTimer(() => ToolTip(), -3000)
        }
       
    } catch Error as e {
        logData.error := "Exception: " . e.Message
        logData.pasteTime := A_TickCount
        SetTimer(() => LogDetailed(logData), -1)
        ToolTip("Error: " . e.Message)
        SetTimer(() => ToolTip(), -3000)
    }
} 
