# PopBat 0.4.4

Windows 無線裝置電量工具。優先透過標準 Bluetooth GATT／安全的 HID Battery Feature 直接讀取裝置名稱與電量，另提供已確認的 ROG Delta II、Logitech HID++ 與部分鍵盤接收器電量協定。

只在啟動及手動按「立即更新」時查詢，結果逐筆顯示。通知預設關閉，設定區僅保留低電量通知與清除紀錄。

## 分享給使用者

請到 [Releases 下載頁](https://github.com/DennisL2045/PopBat/releases/tag/v0.4.4)，在 **Assets** 選擇 [PopBat-0.4.4-win-x64-20261022-135107.zip](https://github.com/DennisL2045/PopBat/releases/download/v0.4.4/PopBat-0.4.4-win-x64-20261022-135107.zip)。目前儲存庫為私人，下載時需要登入有權限的 GitHub 帳號。

這份約 71 MB 的分享包已包含 .NET 執行環境，適用 Windows 10 2004 以上／Windows 11 x64。解壓縮後執行 `PopBat.exe` 即可，不需執行任何 `.ps1` 腳本或另外安裝 .NET、Python、廠商軟體。

GitHub 首頁 **Code → Download ZIP**，以及 Releases 的 **Source code (zip)**，下載的都是原始碼。解壓縮後通常名為 `PopBat-main`，其中沒有可直接使用的執行檔或分享 ZIP。`Export-Release.ps1` 是供開發者建立分享包的 PowerShell 腳本；Windows 可能預設用記事本開啟它。

分享包使用固定檔案清單：執行檔、使用說明、第三方授權及 SHA256 校驗碼。沒有原始碼、研究材料、個人設定、歷史紀錄、實機診斷、截圖或 Git 資料。

只分享這份 ZIP 即可。私人 GitHub 儲存庫供開發備份；收件人不需要儲存庫存取權。編譯過的 .NET 執行檔仍可能被反編譯，打包不提供防逆向保證。

## 開發與驗證

需要 .NET 8 SDK 以上與 PowerShell。

若要自行建立分享包，請在專案資料夾開啟 PowerShell，再輸入下面的命令；不是雙擊腳本。`./Export-Release.ps1` 完成後會產生 `artifacts/PopBat-0.4.4-win-x64-*.zip`。

```powershell
./build.ps1
./tests/Run-Tests.ps1
./tests/Check-ApplicationSurface.ps1
./tests/Check-ReleasePrivacy.ps1
./Export-Release.ps1
```

一般開發版本位於 `app/PopBat.exe`，需 .NET 8 Desktop Runtime x64。分享版獨立建置在 `.build`，不覆蓋正在使用的程式或資料。匯出前會檢查原始碼、編譯後應用程式組件與說明文件，並對分享版執行不開啟硬體的既有自動測試。

## 支援與限制

- 裝置必須已連線；不是每個品牌或接收器都公開通用電量或可驗證的型號。無有效回覆時顯示未知。
- 不讀取 Windows 電量快取、舊配對紀錄、序號或按鍵／滑鼠輸入。不安裝驅動或服務，不存取遊戲程序、不注入或掛鉤，應用程式無網路／遙測。
- 部分韌體的標準 Read 可能不準。已確認的 M75Pro 例外只接受本輪新的標準電池通知，且不修改裝置的通知設定；沒有新值就顯示未知。
- 充電狀態未知時不估算續航、不發低電量通知。遊戲與防外掛共存尚未取得認證。

使用方式見 [使用說明](docs/USER_GUIDE.md)，安全範圍見 [隱私檢查](docs/PRIVACY_REVIEW.md) 與 [遊戲相容性](docs/GAME_COMPATIBILITY.md)。
