# ScannerGuard

Windows 11 條碼掃描器背景工具：辨識 USB HID Keyboard 條碼掃描器，
掃描開始時將目前輸入視窗的微軟注音切至英文模式，掃描後不切回中文。
不更換鍵盤語言配置，不更動掃描器 Prefix／Suffix。

## 使用

需要 Windows x64。使用已發佈的版本不需要安裝 .NET 或 AutoHotkey。

雙擊 `ScannerGuard.exe`。程式會在系統匣顯示圖示；右鍵選 `Exit` 可退出。

- 單一 EXE，背景執行，不開啟 Shell；另寫入少量診斷日誌。
- 程式直接呼叫 Windows 電源 API 防止閒置關閉螢幕及自動睡眠，不模擬按鍵。
- 掃描後微軟注音維持英文；需要中文時自行切換。
- 右鍵系統匣圖示選 `Exit`，程式會解除防休眠並卸載鍵盤處理。
- 不會阻止手動鎖定、強制睡眠或組織的鎖定原則。
- 重複啟動不會建立多份 ScannerGuard。

正常運作不顯示視窗；啟動失敗、輸入重送失敗或無法確認輸入法切換時會顯示錯誤／警告。
若目標系統以系統管理員執行，此工具也需要相同權限。

## 異常退出診斷

日誌位於 `%LOCALAPPDATA%\ScannerGuard\diagnostics.log`，也可從系統匣選單
「開啟診斷資料夾」開啟。記錄啟動、PID、執行時間、防休眠 API 結果、
每分鐘 UI 存活訊號、警告、完整例外與退出原因；不記錄按鍵、條碼或目標視窗內容。
單檔約達 1 MiB 後輪替，保留目前檔案與 `diagnostics.log.1`。

若再次消失，請保留這兩個檔案。`FATAL` 表示程式例外；
`EXIT_REQUEST source=tray` 表示從選單退出。
強制終止、斷電或原生崩潰不保證能寫入最後紀錄；突然中斷的 HEARTBEAT
只能指出最後存活時間，不能單憑它判定是誰終止程式。
可對照 Windows 事件檢視器的「Windows 記錄 → 應用程式」，
查看 .NET Runtime（1026）、Application Error（1000）及 Windows Error Reporting（1001）。
日誌不能取代企業端點防護產品的稽核紀錄，也不會自動重啟程式。

## 鍵盤與掃描器分類

- 自動將 ACPI 內建鍵盤視為一般鍵盤。
- 所有其他 Raw Input 鍵盤裝置都視為掃描器，不要求逐型號登記 VID/PID。
- 若狀態選單顯示「尚未辨識一般鍵盤」，右鍵圖示選「將下一個鍵盤設為一般鍵盤」，
  然後在實際打字的鍵盤按任一鍵；指定只在本次執行有效。
- 「重新自動偵測內建鍵盤」可清除手動指定並回到 ACPI 自動偵測。

**裝置分類的限制：** 所有非 ACPI 鍵盤（包含額外的外接一般鍵盤）都會被當作掃描器。
Windows Low-Level Keyboard Hook 不提供逐鍵裝置 Handle；掃描活動期間仍依 150 毫秒
輸入間隔攔截按鍵，所以與掃描同時操作其他鍵盤可能被誤攔。若要求精準分離每個鍵盤來源，
需改用非鍵盤掃描介面或裝置層級驅動。

## 建置與發佈

開發需要 .NET 8 SDK：

```powershell
dotnet publish -c Release -o .\publish
```

`publish` 資料夾只需分發 `ScannerGuard.exe`。
EXE 內含 .NET 執行環境；首次啟動可能解壓原生元件至使用者暫存目錄，
因此「單檔發佈」不代表執行期間完全不產生執行環境暫存檔。

### GitHub Actions 發布

在 GitHub 的 **Actions → Publish Windows release → Run workflow**，
選擇要發布的分支，輸入新的版本標籤（例如 `v2026.11.21-diagnostics`）。
流程在 GitHub Windows runner 建置、執行回歸測試（明確跳過需要前景焦點的 IME 測試），
再發布壓縮單檔 `ScannerGuard.exe` 與 `SHA256SUMS.txt`。
使用內建 `GITHUB_TOKEN`，不需要額外 PAT 或本機上傳 EXE。
已有標籤不會覆寫，Release 會記錄本次建置的原始碼 commit。
此流程須符合組織核准的原始碼與二進位檔發布政策。

## 實作與限制

使用 `RegisterRawInputDevices`、`GetRawInputData`、
`GetRawInputDeviceInfoW` 及 `WM_INPUT` 依裝置 Handle 辨識掃描器。
Low-Level Keyboard Hook 沒有裝置 ID，因此並非逐鍵、可靠的裝置專屬攔截。

這是保留 HID Keyboard 模式的折衷方案：

1. 第一個按鍵先放行，收到 Raw Input 裝置來源後才知道掃描開始。
2. 非修飾鍵會用 Backspace 嘗試移除首字，再以 Unicode 重送。
3. 透過目標輸入控制項的預設 IME 視窗，
   使用 `WM_IME_CONTROL / IMC_SETOPENSTATUS(FALSE)` 切至英文，不安排恢復。
4. 掃描期間以 150 毫秒的按鍵間隔判定同一批輸入，攔截後以 Unicode 重送。
   Enter、Tab 仍是一般按鍵。

### Shift 處理

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
- 回歸測試驗證未登記 VID/PID 的 USB HID 鍵盤分類、系統匣 Exit 與 Shift 按鍵重播。
- 隔離 WinForms 輸入控制項測試：IME 由開啟切至關閉、未自動恢復，
  且鍵盤配置維持不變。
- 實際多型號掃描器、一般外接鍵盤與 MES 系統的行為仍需實機驗收。

回歸測試不安裝鍵盤 Hook、不向使用者視窗送按鍵。輸入法測試只操作自己建立的測試視窗：

```powershell
dotnet build -c Release
dotnet run --project .\tests\ScannerGuard.RegressionTests.csproj -- .\bin\Release\net8.0-windows\win-x64\ScannerGuard.dll
```

可在最後加上本機鍵盤網站匯出的 JSON 路徑，重播原始按鍵紀錄；測試不將該檔案加入儲存庫。
無法取得前景焦點的環境可加上 `--no-ime`，明確跳過需要焦點的 IME 測試，其餘測試仍執行。
診斷測試以子程序驗證正常退出、未處理例外、事件配對失敗、強制終止及日誌輪替。

本儲存庫不包含掃描紀錄、條碼資料、個人路徑或編譯產物。
