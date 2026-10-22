using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PopBat;

public sealed class DeltaInterfaceDiagnostic
{
    public int Index { get; set; }
    public string Result { get; set; } = "Discovered";
    public ushort? UsagePage { get; set; }
    public ushort? Usage { get; set; }
    public ushort? InputLength { get; set; }
    public ushort? OutputLength { get; set; }
    public bool OutputReportAllowed { get; set; }
    public int? Win32Error { get; set; }
}
public sealed class DeltaQueryDiagnostic
{
    public byte GetKey { get; set; }
    public string Result { get; set; } = "Sending";
}
public sealed class DeltaReadDiagnostics
{
    public string Stage { get; set; } = "NotStarted";
    public string Outcome { get; set; } = "NotStarted";
    public int? Win32Error { get; set; }
    public List<DeltaInterfaceDiagnostic> Interfaces { get; } = new();
    public List<DeltaQueryDiagnostic> Queries { get; } = new();
}
internal static class DeltaDiagnosticReport
{
    internal static string Advice(DeviceState state) => state switch {
        DeviceState.Connected => "電量讀取成功；此結果證明本次 runtime 與目標 HID 路徑可運作。",
        DeviceState.ReceiverMissing => "沒有找到 VID 0B05/PID 1AFA 的 HID 接收器。確認是同一個 2.4G 接收器、USB 已插入，並直接接電腦重試；本診斷不列出其他裝置。",
        DeviceState.UnsupportedInterface => "找到目標 ID，但沒有符合 FF00/01 與 CC 輸出報告的控制集合。可能是接收器介面或韌體差異；不要放寬白名單或掃描未知指令。",
        DeviceState.AccessDenied => "目標接收器存取被拒。先關閉其他正在查詢此耳機的控制軟體再試；不自動提高權限或更換驅動。",
        DeviceState.HeadsetOffline => "接收器已回覆，但表示耳機未開機或未與接收器連線。確認耳機使用 2.4G 模式並喚醒後重試。",
        DeviceState.NotReady => "回覆尚未足以確認真實電量。待機零值不當成 0%；喚醒耳機後重試。",
        DeviceState.NoReply => "已找到控制介面，但已知 GET 未取得有效回覆。依 Queries 確認停在哪一個查詢；檢查耳機連線及控制軟體競爭，勿改送未知指令。",
        _ => "目標 HID 操作失敗。比較階段及數值錯誤碼，確認 USB 連線與裝置狀態；報告不含原始封包或裝置路徑。"
    };
    internal static async Task<string> CreateAsync(CancellationToken token)
    {
        var reader = new DeltaHidReader();
        var reading = await reader.ReadAsync(token);
        var assembly = typeof(App).Assembly;
        string? bundled = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "SelfContained")?.Value;
        return JsonSerializer.Serialize(new {
            Version = assembly.GetName().Version?.ToString(), UpstreamCommit = "f0747692edf0a9bd76962a7704144a9acab076ed",
            Scope = DeviceAccessPolicy.Scope, CheckedAtUtc = DateTimeOffset.UtcNow,
            Environment = new { WindowsVersion = System.Environment.OSVersion.Version.ToString(),
                OSArchitecture = RuntimeInformation.OSArchitecture.ToString(), ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Runtime = RuntimeInformation.FrameworkDescription, RuntimeBundled = string.Equals(bundled, "true", StringComparison.OrdinalIgnoreCase),
                MeetsWindowsMinimum = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041), MinimumWindowsBuild = 19041 },
            Target = new { VendorId = "0B05", ProductId = "1AFA", UsagePage = "FF00", Usage = 1, OutputReportId = "CC", AllowedGetKeys = new[] { 0, 7, 8 } },
            Reading = reading, Diagnostics = reader.Diagnostics, Advice = Advice(reading.State)
        }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
    }
}