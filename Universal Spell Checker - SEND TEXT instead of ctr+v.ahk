#Requires AutoHotkey v2.0

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
    A_Clipboard := ""                  ; clear clipboard
    Send("^c")                         ; copy selection
    if !ClipWait(1) {
        return
    }

    originalText := A_Clipboard         ; store original text before processing
   
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
            ToolTip("API Error: " . http.Status . " - " . http.StatusText . "`nResponse: " . SubStr(http.ResponseText, 1, 200))
            SetTimer(() => ToolTip(), -5000)
            return
        }
       
        ; Parse response and extract corrected text
        response := http.ResponseText
       
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
           
            ; Replace with corrected text using keystroke simulation
            if (correctedText != "") {
                ; Type the corrected text first
                SendText(correctedText)
                ; Then place corrected text onto the clipboard for optional Ctrl+V
                A_Clipboard := correctedText
            }
        } else {
            ToolTip("Error: Could not parse API response")
            SetTimer(() => ToolTip(), -3000)
        }
       
    } catch Error as e {
        ToolTip("Error: " . e.Message)
        SetTimer(() => ToolTip(), -3000)
    }
}
