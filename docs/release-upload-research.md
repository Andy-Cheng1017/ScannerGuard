# gh release create / upload 回傳 403 的官方來源研究

範圍：`https://uploads.github.com/repos/Andy-Cheng1017/ScannerGuard/releases/{id}/assets` 回傳 HTTP 403。
本文只整理官方文件與 cli/cli 原始碼，**未執行任何本機診斷**。「事實」= 來源明載；「可能性」= 推論，須由本機證據確認。

## 一、事實（已對照來源）

1. **檔案大小不是原因。** 每個 release asset 須小於 2 GiB，單一 release 最多 1000 個 asset，總大小與頻寬無上限。161,725,925 B（約 154 MiB）與 67,703,832 B 都遠低於上限。
   來源：<https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases>（Storage and bandwidth quotas）
2. **權限要求：** 只有對 repo 具 write 權限者可管理 release。
   來源：同上（About releases）。
3. **上傳端點規格：** `POST https://uploads.github.com/repos/{owner}/{repo}/releases/{release_id}/assets?name=...`；需使用支援 SNI 的 HTTP client；URL 應取自建立 release 回傳的 `upload_url`；文件列出的狀態碼只有 201、422（同名 asset），上游失敗為 502。**文件未將 403 列為此端點的狀態碼，也未說明 403 原因。**
   來源：<https://docs.github.com/en/rest/releases/assets#upload-a-release-asset>
4. **fine-grained PAT 的權限對照**：官方文件說明可由回應標頭 `X-Accepted-GitHub-Permissions` 得知端點需要的權限；「Resource not accessible」類錯誤見疑難排解文件。
   來源：<https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens>、<https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api#resource-not-accessible>
   （本次未逐列核對 uploads 端點對應到哪一項權限，**未驗證**；請以實際回應標頭為準。）
5. **gh 的 release create 行為（解釋為何事後看不到 release）：** 有附檔且未指定 `--draft` 時，gh 先以 `draft: true` 建立 release，上傳全部成功後才發佈；若上傳失敗，`cleanupDraftRelease` 會呼叫 `deleteRelease` 刪除該草稿並回傳原錯誤。因此上傳 403 後 `gh release view/list` 看不到 release 與此原始碼行為一致（清理失敗時錯誤訊息會附 `cleaning up draft failed`）。
   來源：<https://github.com/cli/cli/blob/trunk/pkg/cmd/release/create/create.go>（`draftWhileUploading`、`cleanupDraftRelease`、`ConcurrentUpload` 段落）
6. **gh 的上傳重試：** 僅網路錯誤與 HTTP >= 500 會重試（最多 3 次）；403 屬永久錯誤，不重試，直接失敗。上傳 URL 取自 API 回傳的 `upload_url`（去掉 `{?name,label}`），以檔案串流送出，`Content-Length` 為檔案大小。
   來源：<https://github.com/cli/cli/blob/trunk/pkg/cmd/release/shared/upload.go>（`shouldRetry`、`uploadWithDelete`、`uploadAsset`）
7. **workflow scope 特例：** 若建立 release 時缺 `workflow` scope，gh 會顯示明確提示 `gh auth refresh -h <host> -s workflow`。本案是在上傳階段 403，且 release 已建立成功（才有 id），不符合該分支，但僅屬比對，非結論。
   來源：create.go 同上。

## 二、可能性（未證實，須本機證據）

- **Token 權限不足或 token 類型限制**：建立 release（api.github.com）成功但上傳（uploads.github.com）403，可能是 token 對 uploads 主機的授權與 API 不一致（例如 fine-grained PAT 未授予 Contents: write、token 未涵蓋該 repo、或 SSO／組織政策）。需檢查回應標頭 `X-Accepted-GitHub-Permissions`、`X-OAuth-Scopes`、`X-GitHub-Request-Id` 與回應本文。
- **網路代理／防火牆／資安閘道**：代理可能對 `uploads.github.com` 主機或大型 POST 單獨封鎖而回 403（回應本文不是 GitHub JSON 而是代理頁面即為徵兆）。官方有主機／IP 清單（`GET /meta`），但本次未擷取細節，**未驗證**。api.github.com 與 git push（github.com）成功，並不保證 uploads.github.com 也被放行。
- **帳號／repo 層級政策**：文件未載明，僅為猜測。
- 由事實 1 可排除「超過官方大小上限」；但不能排除中間設備對請求大小的限制。

## 三、（已由第四節實測取代）

原列出的區分測試已完成，結果見第四節；不再建議其他上傳路徑。

## 四、家長代理的即時（live）發現

本機實測日期：2026-11-11（gh 2.102.0）。以下為**直接觀察到的證據**（主要證據）：

- 以 `gh api` POST 建立草稿 release 成功（id 441267591，僅為診斷用草稿，由家長代理刪除）。
- 驗證用 token 標頭顯示 `repo` scope，且對該 repo 具 admin 權限（`admin: true`）。
- 對 `uploads.github.com` 以 `gh api --include` POST 一個小於 100 bytes 的無害文字檔：
  - HTTP 403
  - `Content-Type: text/html`（非 GitHub 的 JSON 錯誤）
  - `Server: Zscaler/6.2`
  - HTML 內的原因文字：`Not allowed to upload files to this site`（類別：Github）
- 對照：`HEAD` uploads 首頁回 302、API 讀取回 200 → 讀取存取被允許，上傳被阻擋。
- 完整封鎖頁 HTML 含組織內部聯絡／政策資訊，**刻意不收錄於 repo**。

### 結論

403 由企業 Zscaler 代理／安全閘道的上傳政策所回傳（回應來自 Zscaler，而非 GitHub 的授權檢查）。與 token 權限、檔案大小（遠低於 2 GiB 上限）無關：token 具 repo scope 與 admin，且 <100 B 小檔同樣被擋。第二節中「token 權限不足」與「大小限制」的可能性已被此證據排除；「網路代理」已被證實。

### 建議

- 僅向組織 IT／資安部門申請核准對 `uploads.github.com`（GitHub release 上傳）的上傳授權或例外。
- 取得核准前，不進行其他上傳嘗試。

### 關於 release 消失的說明

`gh release create` 附檔時先建立草稿，上傳失敗即由 `cleanupDraftRelease` 刪除草稿（見第一節事實 5；原始碼：<https://github.com/cli/cli/blob/trunk/pkg/cmd/release/create/create.go>）。因此原先失敗的指令後 `gh release view/list` 顯示無 release，符合預期行為。

## 五、缺口

- 未核對 fine-grained PAT 權限表中 uploads 端點所屬的確切權限列。
- 未擷取官方 hostname／IP allowlist（`/meta`）內容。
- 官方文件沒有解釋此端點 403 的成因；以上成因皆不得視為定論。
