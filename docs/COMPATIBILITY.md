# 跨電腦相容性與 HaloBattery 研究

2026-10-22；版本 0.4.8，基於 PopBat 0.4.7 `f0747692edf0a9bd76962a7704144a9acab076ed`。HaloBattery 參考快照 `2251f0534a8e886c9692ead42056059b4f69d8c0`。只研究原始碼，未執行 HaloBattery，未將其程式碼納入 PopBat。

## 判斷重點

當程式已啟動且辨識到 HID 裝置，畫面的「尚未支援／沒有標準電池 Feature」表示未取得可安全解讀的電量來源，並不等於 Windows 完全找不到接收器，也無法單憑它判定是 SDK 或驅動問題。

「程式能在其他電腦啟動」與「每種裝置都能讀電量」是不同問題。2.4 GHz 只描述無線連線；USB 接收器不必提供共同電量協定。通用產品通常由標準介面加上多個已知廠商協定組成。

## HaloBattery 怎麼累積協定

| 做法 | 原始碼證據 | 對 PopBat 的意義 |
| --- | --- | --- |
| 一個協定一個 provider，統一回傳電量狀態 | `providers/base.py`、`providers/__init__.py` | 保留現有 C# provider 架構即可，不必因參考專案是 Python 就重寫 |
| 明確列 VID/PID、usage/interface、report id 與報告長度 | 例如 `providers/asus.py` 的 KNOWN、INTERFACE、REQUEST 等定義 | 支援 ASUS 不代表支援所有 ASUS 裝置；同廠不同接收器也可能需不同協定 |
| 使用已公開工具的協定事實、社群提供的對照與測試 | `docs/protocols.md`、`docs/credits.md`、`docs/devices.md` | 逐項標註來源、連線方式與實測程度，而非掃描所有可能指令 |
| 驗證回覆標頭、長度、範圍、待機值與充電狀態 | 各 provider 的 parser 與測試 | 能辨識裝置不代表已確認某個位元組就是百分比 |
| 用 Windows configuration manager 先取得介面路徑，快取 HID 列舉 | `providers/hidlist.py` | 減少重複開啟所有 HID 介面。PopBat 已手動查詢，後續仍可增加列舉快取；不能把 hid.enumerate 當成零接觸掃描 |
| 藍牙採 Windows 已知的電量，另確認當前連線 | `providers/bluetooth.py` | HaloBattery 能顯示某個耳機，不代表它有該耳機的私有查詢協定；有時只是 Windows 已提供電量 |

HaloBattery 的藍牙 provider 會保留已知電量來平滑系統暫時未回報的情況；PopBat 目前偏向本輪直接 GATT／Fast Pair 回覆，且明確拒絕已知不準確的來源。兩者顯示數量與更新時機可能不同，不能只比較畫面裝置數量就認定其中一方 HID 程式錯誤。

HaloBattery 的診斷會包含位址、序號與部分原始回覆；這部分不採用。PopBat 診斷只保留名稱、公開 VID/PID、電池能力、狀態與數值錯誤碼，不持久化位址／關聯鍵／原始封包。不使用它的自動更新、遊戲偵測、輸入喚醒或 USB 擷取流程。

直接參考：

- [HaloBattery provider 基底](https://github.com/HeyOkay/HaloBattery/blob/2251f0534a8e886c9692ead42056059b4f69d8c0/providers/base.py)
- [HID 列舉方式](https://github.com/HeyOkay/HaloBattery/blob/2251f0534a8e886c9692ead42056059b4f69d8c0/providers/hidlist.py)
- [ASUS 協定與型號表](https://github.com/HeyOkay/HaloBattery/blob/2251f0534a8e886c9692ead42056059b4f69d8c0/providers/asus.py)
- [Windows 藍牙電量來源](https://github.com/HeyOkay/HaloBattery/blob/2251f0534a8e886c9692ead42056059b4f69d8c0/providers/bluetooth.py)
- [協定來源說明](https://github.com/HeyOkay/HaloBattery/blob/2251f0534a8e886c9692ead42056059b4f69d8c0/docs/protocols.md)

## 這次修正的實際風險

0.4.7 原本將所有 provider 排成同一條鏈，通用查詢 pending 時用全域旗標延後專用查詢。因此藍牙較慢或 Windows GATT 未及時結束，可能連帶跳過 USB 接收器的專用流程。這是從原始碼找到的問題；還沒有另一台電腦的診斷資料，不能斷言它就是那台電腦唯一的原因。

0.4.8 將 USB/HID 與藍牙分成獨立查詢組，各自維持 pending／去重狀態，任何一組已完成的裝置可先顯示。單一 provider 發生例外不會讓其他協定全部中止。更新被取消或逾時時，本輪已完成的新值保留，未完成的舊值不當作新讀數。診斷逐個列出 Completed／Deferred／Failed／Cancelled、耗時與成功數量；Completed 本身不代表裝置回覆成功。

同一個 USB 查詢組內，仍保留標準電池 Feature 優先與必要的 pending 防護，避免無限制累積卡住的 I/O。Delta II 已知控制集合可獨立於通用 pending 查詢，仍只允許原有三個 GET。這是其中一個受支援協定，整體產品沒有縮回 Delta 專用版。

沒有更換現有 BLE、Fast Pair、HID++、鍵盤或 Delta II 的電量格式，沒有臆測未知接收器協定。

## .NET、SDK 與 Windows

- SDK 是建置工具；使用者電腦不必安裝 SDK。Framework-dependent exe 需要相容的 .NET Desktop Runtime；self-contained exe 已包含 runtime。
- 本機 `build.ps1` 仍輸出較小的 framework-dependent 開發版；`Export-Release.ps1` 產生包含 runtime 的 x64 分享版，保留 0.4.7 的 runtime 修補版本釘選。
- 分享版需要 Windows 10 2004（build 19041）以上或 Windows 11 x64。它不會因包含 runtime 就變成 macOS／Linux／所有 CPU 架構通用。
- 若另一台已能開 GUI，優先看裝置能力、協定、連線狀態、存取拒絕與逾時。驅動版本、USB Hub、競爭軟體或 Windows 藍牙堆疊差異仍可能影響硬體讀取；尚未取得證據前不任意換驅動或要求管理員權限。

官方說明：[.NET 發佈模式](https://learn.microsoft.com/dotnet/core/deploying/)、[單一檔案仍受 OS／架構限制](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview)。

## Python 是否比較簡單

Python 加 hidapi 很適合小型已知協定實驗、回覆解析與測試資料；但 VID/PID、集合選擇、Windows API、回覆格式、韌體差異與權限問題仍然存在。HaloBattery 也需要每個 provider 的型號與協定，且它的 Windows 發佈必須連同 `_internal` 一起解壓，並不是只搬一個 exe 就會自動支援所有裝置。

本次保留 C# / WPF 成品，先修正排程與部署並取得另一台的最小診斷。之後若針對新協定做 Python 對照實驗，必須使用同一個已知裝置／同一組只讀查詢，不能以大量探測來找電量。

## 在有問題的電腦執行

1. 使用本次包含 runtime 的 ZIP，完整解壓，先結束所有 PopBat 實例。
2. 保持原本接收器／藍牙連線狀態，雙擊 `Run-Diagnostics.cmd`。它只執行一次電池查詢，不啟動 GUI、不寫電量歷史、不上傳。
3. 將 `diagnostics/compatibility-*.json` 帶回。內含 Windows build、CPU 架構、已安裝 SDK／Desktop Runtime 版本、exe SHA256、自動測試結果、provider 階段與安全 HID 能力摘要。
4. 需要只檢查環境、不接觸裝置時，在 PowerShell 執行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Diagnose-Compatibility.ps1 -EnvironmentOnly`。
5. 開 GUI 觀察結果，記錄出問題裝置的品牌／型號與連線方式；不需要序號、位址、封包或私人檔案。

判讀：SDK 列表空白不代表失敗；self-contained 版本可照常執行。SafeFeatureBattery=false 只表示沒有允許的標準 Feature，仍可能透過專用協定支援。InputBattery=true 也不授權讀取可能夾帶輸入資料的報告。Provider=Deferred 表示延後，Failed 表示程式層作業失敗；NoReply／UnsupportedInterface 等裝置狀態需一起判讀。

另一台電腦的實際原因，必須取得該台報告才能確認。本機成功不等於跨機驗收完成。
## 本機建置差異補記

發佈時實際發現 0.4.7 的 Export-Release.ps1 固定要求 Windows SDK projection 10.0.19041.57；本機 SDK 8.0.202 解析的是 10.0.19041.31，造成授權檔收集階段找不到路徑。已改為讀取 project.assets.json 的 framework downloadDependencies，必要時才取 libraries，不假設它一定是一般 NuGet library。這是封裝流程的確定問題，不能解釋一個已經能開啟 GUI 的 exe 為何沒有電量回覆。