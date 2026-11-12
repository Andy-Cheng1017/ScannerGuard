# ScannerGuard

Windows 11 條碼掃描器背景工具：辨識 Honeywell USB HID Keyboard，
掃描開始時將目前輸入視窗的微軟注音切至英文模式，掃描後不切回中文。
不更換鍵盤語言配置，不更動掃描器 Prefix／Suffix。

## 使用

需要 Windows x64 與 AutoHotkey v2。使用已發佈的版本不需要安裝 .NET。

將下列兩個檔案放在同一資料夾，雙擊 `No_sleep.ahk`：

```text
No_sleep.ahk
ScannerGuard.exe
```

- 全程背景執行，不開啟 Shell、不產生日誌。
- AHK 透過 Windows 電源 API 防止閒置關閉螢幕及自動睡眠，不模擬 F24。
- 掃描後微軟注音維持英文；需要中文時自行切換。
- 右下角 AHK 圖示選擇 Exit，同時結束掃描器程式並解除防休眠。
- 不會阻止手動鎖定、強制睡眠或組織的鎖定原則。
- 重複啟動不會建立多份 ScannerGuard。

預設掃描器為 `VID_0C2E`、`PID_0901`。若裝置不同，修改 AHK 的 Run 行，
在執行檔後加入 VID、PID，例如：

```ahk
Run('"' scannerExe '" 0C2E 0B61', A_ScriptDir, "Hide", &scannerPid)
```

正常運作不顯示視窗；啟動失敗、輸入重送失敗或無法確認輸入法切換時會顯示錯誤／警告。
若目標系統以系統管理員執行，此工具也需要相同權限。

## 建置與發佈

開發需要 .NET 8 SDK：

```powershell
dotnet publish -c Release -o .\publish
Copy-Item .\No_sleep.ahk .\publish\
```

`publish` 資料夾內只需要 `ScannerGuard.exe` 和 `No_sleep.ahk` 兩個檔案。
EXE 內含 .NET 執行環境；首次啟動可能解壓原生元件至使用者暫存目錄，
因此「單檔發佈」不代表執行期間完全不產生執行環境暫存檔。

## 實作與限制

使用 `RegisterRawInputDevices`、`GetRawInputData`、
`GetRawInputDeviceInfoW` 及 `WM_INPUT` 依裝置 Handle 辨識掃描器。
Low-Level Keyboard Hook 沒有裝置 ID，因此並非逐鍵、可靠的裝置專屬攔截。

這是保留 HID Keyboard 模式的折衷方案：

1. 第一個按鍵先放行，收到掃描器 Raw Input 後才知道掃描開始。
2. 非修飾鍵會用 Backspace 嘗試移除首字，再以 Unicode 重送。
3. 透過目標輸入控制項的預設 IME 視窗，
   使用 `WM_IME_CONTROL / IMC_SETOPENSTATUS(FALSE)` 切至英文，不安排恢復。
4. 掃描期間以 150 毫秒的按鍵間隔判定同一批輸入，攔截後以 Unicode 重送。
   Enter、Tab 仍是一般按鍵。

### Shift 修補

- 若掃描器第一鍵是 Shift，先補送一次 Shift 放開，避免後續 Unicode 仍受到
  Windows 的 Shift 按住狀態影響；該鍵後來真正放開時會被攔截，避免重複放開。
- 左、右 Shift 分別追蹤。已放行按鍵保留按下當時的 Shift／Caps Lock 快照，
  即使 Raw Input 延後到達，也不會使用放開後的狀態重建前幾個字。
- 在掃描最後一次按鍵約 225 毫秒後，對同一前景視窗／輸入控制項再確認一次英文模式，
  避免首個 Shift 的延後處理重新切回中文。這是一次性確認，不會持續鎖定英文。
- 不使用 Ctrl+A、剪貼簿或強制全大寫；原有大小寫與 Enter／Tab 行為保留。

**注意：**

- 首字修復取決於目標程式處理訊息的時序；Backspace 可能刪到既有文字或組字。
  請先在記事本測試，不建議直接用於不可回復的輸入或提交操作。
- 掃描期間同時操作其他鍵盤也可能被當作掃描資料，無法保證不受影響。
- 輸入間隔超過 150 毫秒可能被判定為下一次掃描。
- 不同應用程式、TSF／新版微軟注音可能不接受 IMM 控制；英文模式作用於目前目標，
  不保證其他視窗或其他執行緒也同步切換。
- 不攔截系統安全桌面；管理員視窗、特殊輸入控制項可能限制模擬輸入。

## 驗證範圍

- Release 單檔發佈通過。
- 隔離 WinForms 輸入控制項測試：IME 由開啟切至關閉、未自動恢復，
  且鍵盤配置維持不變。
- 實際 Honeywell 掃描器與 MES 系統的行為仍需實機驗收。

回歸測試不安裝鍵盤 Hook、不向使用者視窗送按鍵。輸入法測試只操作自己建立的測試視窗：

```powershell
dotnet build -c Release
dotnet run --project .\tests\ScannerGuard.RegressionTests.csproj -- .\bin\Release\net8.0-windows\win-x64\ScannerGuard.dll
```

可在最後加上本機鍵盤網站匯出的 JSON 路徑，重播原始按鍵紀錄；測試不將該檔案加入儲存庫。

本儲存庫不包含掃描紀錄、條碼資料、個人路徑或編譯產物。
