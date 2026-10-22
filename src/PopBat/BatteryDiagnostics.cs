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

internal sealed record BatteryProviderDiagnostic(string Provider, string Outcome, long Milliseconds, int DeviceCount, int ConnectedCount);
internal static class BatteryDiagnosticReport
{
    internal static async Task<string> CreateAsync(CancellationToken token)
    {
        var monitor = new DeviceMonitor(diagnostics: true); var sync = new object();
        var stages = new List<BatteryProviderDiagnostic>();
        IReadOnlyList<DeviceBattery> current = Array.Empty<DeviceBattery>();
        bool interrupted = false;
        try {
            current = await monitor.ReadAsync(token, true, batch => { lock (sync) current = batch; }, stage => { lock (sync) stages.Add(stage); });
        } catch (OperationCanceledException) { interrupted = true; }
        var assembly = typeof(App).Assembly;
        string? bundled = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "SelfContained")?.Value;
        lock (sync) return JsonSerializer.Serialize(new {
            Version = assembly.GetName().Version?.ToString(), UpstreamCommit = "f0747692edf0a9bd76962a7704144a9acab076ed",
            Scope = DeviceAccessPolicy.Scope, CheckedAtUtc = DateTimeOffset.UtcNow, Interrupted = interrupted,
            Environment = new { WindowsVersion = System.Environment.OSVersion.Version.ToString(),
                OSArchitecture = RuntimeInformation.OSArchitecture.ToString(), ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Runtime = RuntimeInformation.FrameworkDescription, RuntimeBundled = string.Equals(bundled, "true", StringComparison.OrdinalIgnoreCase),
                MeetsWindowsMinimum = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041), MinimumWindowsBuild = 19041 },
            Providers = stages.ToArray(),
            HidCapabilities = monitor.LastHidInventory.Select(d => new { d.Name, d.Manufacturer, VendorId = d.Vendor.ToString("X4"),
                ProductId = d.Product.ToString("X4"), d.Usb, d.Peripheral, d.InputBattery, d.SafeFeatureBattery }).ToArray(),
            HeadsetQueries = monitor.HeadsetQueries,
            Devices = current.Select(d => new { d.Name, d.Connection, d.Reading.State, d.Reading.Percent,
                d.Reading.Charging, d.Level, d.Note, d.Reading.TargetInterfaces, d.Reading.VendorInterfaces }).ToArray(),
            Notes = new[] { "僅列電池能力與讀數；不含裝置路徑、MAC、序號、原始報告或例外內容。",
                "Completed 表示 provider 已結束，不代表裝置有回覆；請一起看 State 與 ConnectedCount。",
                "UnsupportedInterface 表示標準電池 Feature 不可用，需已知廠商協定；不代表 USB 或藍牙完全未偵測。",
                "SDK 僅用於開發建置；若這份報告能產生，runtime 已成功載入。" }
        }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
    }
}