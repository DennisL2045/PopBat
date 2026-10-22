Windows 無線裝置電量工具。目前僅提供電量查詢，有機會再做調整裝置設定等等。

請到 [Releases下載頁面](https://github.com/DennisL2045/PopBat/releases/latest)，在 **Assets** 下載 **PopBat開頭的ZIP**。

## 支援與限制

- 裝置必須已連線；不是每個品牌或接收器都公開通用電量或可驗證的型號。無有效回覆時顯示未知。
- 不讀取 Windows 藍芽顯示電量。
- 是否會被防外掛系統判定為外掛尚未驗證，請留意使用。

## 0.4.8 相容性修正

保留通用裝置支援；藍牙與 USB 查詢分組執行，單一失敗不使全部停止，逾時保留本輪新結果。跨電腦問題與 HaloBattery 協定研究見 [相容性說明](docs/COMPATIBILITY.md)。開發版用 build.ps1；含 runtime 的分享包用 Export-Release.ps1，並附 Run-Diagnostics.cmd。


Windows SDK projection 套件版本依實際建置解析，不再要求固定版本的套件目錄。分享版內附 .NET 8.0.32，使用者無需安裝 SDK 或 runtime；適用 Windows 10 2004 以上／Windows 11 x64。SDK 封裝修正不等於所有接收器都已通用，未知狀態仍需診斷。
