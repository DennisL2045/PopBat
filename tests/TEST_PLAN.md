# PopBat 0.4.4 測試清單

2026-10-22。通用電量優先、品牌協定補充。測試與實機證據分開記錄，不保存序號、位址、容器 ID、原生路徑、按鍵或原始報告。

## 自動驗證

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Run-Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Check-ApplicationSurface.ps1
```

預期 Self-test: True; checks: 167、Device probe: NotRequested。既有 158 項保留，新增 9 項涵蓋跳過不準 Read、部分結果及晚到回報、最終結果完整、保留卡片與減少綁定通知，以及臨時通用／專用接收器的中間顯示。測試不開硬體、不寫歷史；輸出 tests/results/test-*.json 與 exe SHA256。能力檢查通過 22 個標準 Windows native 宣告；禁止程序入侵／鍵鼠鉤子／模擬輸入／網際網路，以及 GATT 寫入、配對、掃描、HID GET_INPUT_REPORT／SET。只允許接收已開啟的標準 BAS 更新，不寫通知設定。這不是外部安全稽核或防外掛認證。

新增測試涵蓋：跨品牌無型號對照、韌體預設名稱／型號字串剔除、直接名稱優先、連線名稱來源註明、標準 battery-only Feature 白名單、報告混合 vendor／鍵盤欄位／按鈕時拒讀、NULL／範圍／比例換算、provider 排序與後備、避免重複讀取、同名不同裝置保留、接收器多槽位不被折疊、未完成通用作業暫緩專用通訊、取消與容量上限。

## 實機與資源

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Inspect-AllDevices.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Inspect-AllDevices.ps1 -HidCapabilities
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Inspect-AllDevices.ps1 -Preview
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Inspect-AllDevices.ps1 -ResourceCheck
```

先結束其他 PopBat 實例。鍵盤單次查詢用 -KeyboardProbe；-BatteryDetails 只比較直接標準欄位，並非準確度認證；-HidCollections 僅看控制集合描述元。HidCapabilities 只讀描述元，不取任何 Input／Feature 報告。Preview 只渲染自己的畫面，不截桌面。0.4.4 ResourceCheck 等初次讀取完成、暖機 10 秒後閒置量測 125 秒，驗證請求數仍為 1；再明確呼叫一次手動更新，驗證請求數為 2、卡片沿用，以及首筆／完整結果時間。診斷專用 UI Dispatcher 探針不觸發裝置查詢；記錄最大延遲，不代表實際渲染 FPS。歷史與通知停用、只讀自己的程序，完成自動退出。每個報告含 exe SHA256，測量期間不可更換執行檔。沙箱被 Windows 拒絕藍牙時使用獲准的沙箱外診斷，程式不自行升權。

| 編號 | 情境 | 預期 | 目前證據 |
| --- | --- | --- | --- |
| B01 | 跨品牌標準 BLE | 不新增型號協定即可直接讀名稱與電量 | MX Master 3S 最新 15%；M75Pro Read94% 仍拒用，新標準電池更新已獨立實測，見 K04 |
| B02 | 假的 Manufacturer Name／Model Number | 忽略預設字串，使用有效直接名稱；必要時標示連線名稱來源 | M75Pro 韌體預設字串已剔除；名稱從裝置直接回覆 |
| B03 | 已配對但未連線的舊裝置 | 不顯示舊 M240 或 Windows 電量 | 目前只顯示在線 BLE 與插上的 USB 介面；不讀舊配對；斷線模擬測試通過 |
| B04 | 睡眠、關閉、恢復、充電、拔插 | 不沿用舊百分比；未知充電不推定耗電 | 模擬通過，實機狀態切換待測 |
| U01 | USB／2.4G 接收器提供標準 Battery Feature | 全報告安全才 GET_FEATURE，禁止輸入報告 | 描述元檢查與模擬通過；無安全 Battery Feature 實機，GET 實機待測 |
| U02 | 電池只在 Input Report 或與私有資料混合 | 不讀按鍵／滑鼠資料，不嘗試未知指令，顯示不支援 | 模擬通過 |
| V01 | 通用讀到電量 | 專用 provider 不重複查該實體裝置 | 模擬通過；實機鍵盤接收器不重複顯示通用不支援卡片 |
| V02 | 通用失敗、已知 ROG／HID++ | 使用後備，保留接收器多槽位 | 模擬通過；ROG 另有舊版歷史實測，當前硬體未接 |
| R01 | MX 與 M75Pro BLE 啟動後閒置，再手動更新 | 低 CPU、有限記憶體，閒置不新增作業 | 見版本 0.4.4 最新 resource JSON，僅代表短測 |
| K01 | M75Pro 主動 Read 與鍵盤不符 | 沒有新的有效通知時維持未知，不校正、不回退舊值 | 不同 Read 值及識別快取均拒用；錯誤來源再診斷 94%／94% |
| K02 | 鍵盤未在接收器旁 | 接收器存在，但百分比未知；不冒稱型號 | 接收器實測 NoReply；使用者確認鍵盤不在身邊 |
| K03 | 鍵盤開機、實際使用 2.4G 接收器 | GET 20/01 直接電量與鍵盤指示比對，不先提供答案 | 使用者確認拔掉停止輸入、插回恢復後，兩次有效回覆 16%；使用者事後確認與螢幕相符或僅有正常小幅差異，整合預覽亦為 16%。報告 devices-20261015-052459-449／052622-857／052623-630 |
| K04 | M75Pro 標準 BAS 已啟用更新 | 本輪新值直接來自 GATT，不讀 Windows 電量、不改 CCCD，完成即移除事件 | Python 五次28%，使用者確認相符；最終 C#25%，MX15%。158 項測試涵蓋時間／格式／斷線／無新值保護，見 PROGRESS |
| M01 | 啟動、閒置 125 秒、明確手動更新 | 初次只 1 輪，閒置不增加，再手動後為 2；重複啟動不重讀 | 0.4.4 ResourceCheck 自動驗證；以同版 resource 報告為準 |
| M02 | 更新期間操作視窗／取消、重開通知區視窗 | UI 不等待硬體 I/O，卡片沿用，開視窗不新增讀取，取消後不採晚到結果 | 模擬進度／綁定測試通過；Dispatcher 延遲與卡片實例見 resource，實際滑鼠滾動手感待使用者確認 |
| G01 | 正常使用 Apex／Valorant／戰地風雲／英雄聯盟 | 輸入正常，無防外掛提示 | 未測、未認證 |
| G02 | G HUB／Options+ 共存 | 不改設定、不獨占輸入 | 待測 |

ROG 原始讀取可用 Run-Tests.ps1 -IncludeDeviceProbe；此開關才進行單次硬體 GET。ROG 未插時 ReceiverMissing／exit 2 為預期；UI 不為不存在的接收器保留卡片。

其餘手動驗收：取消本次更新、通知區隱藏／顯示不發查詢、退出、ROG 儲存偏好跨重啟、清除紀錄、歷史最多 7 天／2048 筆、已知未充電且自然下降 >=3%／>=1 小時後的估算、通知真實 Windows 提示。未知充電狀態不估算、不發低電量通知，勿為測通知亂送設定指令。

問題回報只需版本／exe SHA256、測試編號、時間、操作、顯示名稱／狀態／電量／充電旗標及是否重現。可附相關診斷 JSON，不附 app/data、原始封包或原生識別資訊。防外掛有提示就停用 PopBat，不規避或修改防外掛。
