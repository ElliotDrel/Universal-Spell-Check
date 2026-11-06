#Requires AutoHotkey v2.0

^!u::                                  ; hotkey Ctrl+Alt+U
{
    A_Clipboard := ""                  ; clear clipboard
    Send("^c")                         ; copy selection
    if !ClipWait(1)
        return

    originalText := A_Clipboard         ; store original text before processing
    
    ; Skip very short text (likely doesn't need correction)
    if (StrLen(Trim(originalText)) < 3)
        return
    
    ; Create temp file paths
    tmpIn := A_Temp "\sc_in.txt"
    tmpOut := A_Temp "\sc_out.txt"
    
    ; Write selection to temp file and run spell check
    FileAppend(originalText, tmpIn, "UTF-8")
    RunWait(A_ComSpec ' /c type "' tmpIn '" | "C:\Program Files\nodejs\node.exe" "C:\Users\2supe\All Coding\Universal Spell Check\spellcheck.js" > "' tmpOut '"', , "Hide")

    ; Read the corrected text (spellcheck-fast.js outputs clean text directly)
    corrected := Trim(FileRead(tmpOut, "UTF-8"))
      
    ; Only replace text if we got a valid correction and it's different
    if (corrected != "" && corrected != originalText) {
        Send("{Text}" corrected)
    }
    
    ; Clean up temp files for speed (no debugging overhead)
    try FileDelete(tmpIn)
    try FileDelete(tmpOut)
}