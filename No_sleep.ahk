#Requires AutoHotkey v2.0
#SingleInstance Force

scannerExe := A_ScriptDir "\ScannerGuard.exe"
if !FileExist(scannerExe) {
    MsgBox("找不到 ScannerGuard.exe，請放在 No_sleep.ahk 同一個資料夾。")
    ExitApp()
}

try {
    Run('"' scannerExe '"', A_ScriptDir)
} catch as err {
    MsgBox("無法啟動 ScannerGuard：" err.Message)
}

; Compatibility launcher. ScannerGuard now owns its tray icon and keep-awake state.
