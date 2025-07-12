#Requires AutoHotkey v2.0

; Logging configuration
enableLogging := true
logFilePath := A_ScriptDir . "\spellcheck.log"

; Logging function
LogSpellCheck(data) {
    if (!enableLogging) 
        return
    
    duration := data.pasteTime - data.startTime
    status := data.error ? "ERROR: " . data.error : "SUCCESS"
    inputText := StrReplace(data.original, "`n", "\\n")
    outputText := StrReplace(data.result, "`n", "\\n")
    
    entry := Format("{1} | {2}ms | {3} | Input: {4} | Output: {5}`n", 
        data.timestamp, duration, status, inputText, outputText)
    
    try {
        FileAppend(entry, logFilePath)
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
        error: "",
        startTime: startTime,
        pasteTime: 0,
        timestamp: FormatTime(, "yyyy-MM-dd HH:mm:ss")
    }
    
    A_Clipboard := ""                  ; clear clipboard
    Send("^c")                         ; copy selection
    if !ClipWait(1) {
        logData.error := "Clipboard wait timeout"
        SetTimer(() => LogSpellCheck(logData), -1)
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
            SetTimer(() => LogSpellCheck(logData), -1)
            ToolTip("API Error: " . http.Status . " - " . http.StatusText . "`nResponse: " . SubStr(http.ResponseText, 1, 200))
            SetTimer(() => ToolTip(), -5000)
            return
        }
       
        ; Parse response and extract corrected text
        response := http.ResponseText
       
        ; Better JSON parsing - look for the last "content" field (assistant's response)
        contentMatches := []
        pos := 1
        while (pos := RegExMatch(response, '"content"\s*:\s*"((?:[^"\\]|\\.)*)"', &match, pos)) {
            contentMatches.Push(match[1])
            pos += match.Len
        }
       
        if (contentMatches.Length > 0) {
            ; Get the last content match (should be assistant's response)
            correctedText := contentMatches[-1]
           
            ; Unescape JSON - fix for single backslash sequences
            correctedText := StrReplace(correctedText, "\n", "`n")
            correctedText := StrReplace(correctedText, "\r", "`r")
            correctedText := StrReplace(correctedText, "\t", "`t")
            correctedText := StrReplace(correctedText, '\"', '"')
           
            ; Replace with corrected text
            if (correctedText != "") {
                A_Clipboard := correctedText
                Send("^v")
                
                ; Capture paste timing and log success
                logData.pasteTime := A_TickCount
                logData.result := correctedText
                SetTimer(() => LogSpellCheck(logData), -1)
            }
        } else {
            logData.error := "Could not parse API response"
            SetTimer(() => LogSpellCheck(logData), -1)
            ToolTip("Error: Could not parse API response")
            SetTimer(() => ToolTip(), -3000)
        }
       
    } catch Error as e {
        logData.error := "Exception: " . e.Message
        SetTimer(() => LogSpellCheck(logData), -1)
        ToolTip("Error: " . e.Message)
        SetTimer(() => ToolTip(), -3000)
    }
} 