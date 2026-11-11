#Requires AutoHotkey v2.0
#SingleInstance Force
Persistent()

scannerExe := A_ScriptDir "\ScannerGuard.exe"
if !FileExist(scannerExe) {
    MsgBox("找不到 ScannerGuard.exe，請放在 No_sleep.ahk 同一個資料夾。")
    ExitApp()
}

scannerPid := 0
OnExit(Cleanup)
try {
    Run('"' scannerExe '"', A_ScriptDir, "Hide", &scannerPid)
} catch as err {
    MsgBox("無法啟動 ScannerGuard：" err.Message)
    ExitApp()
}

; 防止系統與螢幕因閒置休眠，不模擬按鍵、不顯示提示。
if !DllCall("Kernel32\SetThreadExecutionState", "UInt", 0x80000003, "UInt") {
    MsgBox("無法設定螢幕不休眠。")
    ExitApp()
}

Cleanup(*) {
    global scannerPid
    DllCall("Kernel32\SetThreadExecutionState", "UInt", 0x80000000, "UInt")
    if scannerPid && ProcessExist(scannerPid) {
        ProcessClose(scannerPid)
    }
}
