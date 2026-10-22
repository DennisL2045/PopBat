# 公開前檢查（2026-10-22）

本次經作者確認，PopBat 原始程式碼採 MIT 授權，完整條款見根目錄 LICENSE。
第三方元件及文件保留其各自授權，MIT 不替代 Microsoft 的二進位散布條款。

## 機敏資訊與公開範圍

檢查公開前既有的 5 筆 Git 提交、75 份不重複歷史檔案，以及所有分支、
標籤、提交作者／提交者與訊息。未發現符合規則的 API key、密碼、存取權杖、
私鑰、個人信箱、使用者目錄、裝置序號或藍牙位址。提交使用 GitHub noreply。
GitHub 帳號名稱是作者自行用於專案的公開身份，並保留於著作權聲明。

三份既有 Release ZIP 均逐一核對 GitHub SHA256、封裝檔案清單及內容校驗。
另外解析 .NET 單檔程式，檢查實際內嵌的第一方組件、相依設定與執行環境設定，
沒有僅以原始碼掃描代替執行檔檢查。第三方聲明中的公開作者信箱與原始套件
授權文件比對，不誤刪應保留的署名。GitHub SSH clone 網址亦不屬於私人信箱。

GitHub 目前沒有 Actions 執行紀錄／附件、Issue／PR 或提交留言可隨公開而洩漏。
開發對話指示、研究工具、原廠研究檔案、診斷、截圖、app/data、個人設定、
建置快取與其他交接 ZIP 都未追蹤於 Git，也不放入對外分享包。

## 第三方元件及修正

| 元件 | 用途與授權處理 |
| --- | --- |
| .NET / Windows Desktop 8.0.32 | 0.4.7 分享版包含官方安全修補執行環境及 apphost；保留套件原始聲明與適用 .NET Library License、Windows SDK 條款 |
| Microsoft.Windows.SDK.NET.Ref 10.0.19041.57 | 使用標準 WinRT 投影；Microsoft.Windows.SDK.NET.dll、WinRT.Runtime.dll 按 SDK 散布條款處理 |
| Microsoft.NET.ILLink.Tasks 8.0.32 | SDK 自動加入的建置工具，不是應用程式常駐服務或另行散布的程式 |
| Windows 系統 DLL | 使用作業系統既有的標準 HID／裝置 API，不複製 DLL 或安裝自訂驅動 |
| Google Fast Pair 文件 | 依公開格式獨立實作，只接收狀態與固定唯讀 GET；保留文件 CC BY 4.0 來源說明，未加入 Google SDK 或線上 API |

0.4.4 舊 ZIP 缺少部分 Microsoft 散布條款，已將舊版下載包的完整授權文件補齊。
既有程式二進位保持原樣；新版 0.4.7 另更新執行環境。舊版仍屬歷史版本，
不代表其內含的舊執行環境有後續安全修補，使用時應下載最新版本。

所有 ZIP 加入 PopBat 的 LICENSE.txt，重算 SHA256SUMS。分享包固定只包含
PopBat.exe、README.txt、LICENSE.txt、THIRD-PARTY-NOTICES.txt、SHA256SUMS.txt。
沒有原廠執行檔、研究用 Python／套件或額外加殼、混淆與反偵錯工具。

重新執行 NuGet 線上已知弱點查詢，另對實際散布的 runtime／apphost 套件核對
NuGet 公開弱點資料及 Microsoft 安全修補版本。NuGet 的套件查詢不等於
原生元件或完整產品安全稽核；依檢查當時可取得的官方資料判斷。

## 行為與限制

本版不新增雲端 API、金鑰、登入、遙測、付費服務或用量計費功能。
裝置查詢仍只發生於啟動與手動更新，無驅動、程序注入、遊戲程序讀取、鍵鼠鉤子、
配對或裝置設定變更。原廠格式研究所得的通訊常數不是廠商程式碼或軟體授權。

本次為程式、歷史、附件與授權文件的實作檢查；未發現的問題不等於不存在。
沒有進行第三方法律意見、專利審查或遊戲防外掛認證，亦不保證零誤判。

## 官方參考

- [Microsoft .NET Windows licensing map](https://github.com/dotnet/core/blob/main/license-information-windows.md)
- [Windows SDK redistribution list](https://learn.microsoft.com/en-us/legal/windows-sdk/redist#microsoftwindowssdknetref)
- [Microsoft .NET 8 release metadata](https://raw.githubusercontent.com/dotnet/core/main/release-notes/8.0/releases.json)
- [NuGet vulnerability information](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages)
