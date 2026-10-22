using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PopBat;

internal sealed record HidDeviceInventory(string Group, string Name, string Manufacturer, ushort Vendor, ushort Product,
    bool Peripheral, bool InputBattery, bool SafeFeatureBattery, bool Usb);
internal sealed record HidProbeBatch(IReadOnlyList<DeviceBattery> Devices, IReadOnlyList<HidDeviceInventory> Inventory);

internal sealed class StandardHidReader
{
    internal sealed record Endpoint(string Path, string Group, string Name, string Manufacturer, ushort Vendor, ushort Product,
        bool Peripheral, bool InputBattery, ushort FeatureLength, IReadOnlyList<HidValueCapability> Features, bool Usb,
        ushort Page, ushort Usage, ushort InputLength, ushort OutputLength, byte[]? InputReports, byte[]? OutputReports);
    private Task<HidProbeBatch>? _pending;
    internal bool IsPending => _pending is { IsCompleted: false };
    private readonly Dictionary<string, (int Failures, DateTimeOffset Next)> _failures = new();
    private int _cursor;
    internal void RetryNow() { lock (_failures) _failures.Clear(); }
    private static HidProbeBatch Status(string text) => new(new[] {
        new DeviceBattery("standard-hid-status", "標準 HID 電量檢查", "通用電量", BatteryReading.Empty(DeviceState.NoReply), Note: text) }, Array.Empty<HidDeviceInventory>());
    internal async Task<HidProbeBatch> ReadAsync(IReadOnlySet<string> covered, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_pending is { IsCompleted: false }) return Status("前一次 HID 標準電量查詢仍在結束，暫緩新查詢。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var snapshot = covered.ToHashSet(StringComparer.OrdinalIgnoreCase);
        CancellationToken operationToken = deadline.Token;
        _pending = Task.Run(() => ReadCore(snapshot, operationToken), CancellationToken.None);
        try { return await _pending.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Status("HID 標準電量查詢逾時；不累積新查詢或使用舊值。"); }
    }
    internal static Task<IReadOnlyList<HidDeviceInventory>> InspectAsync(CancellationToken token) => Task.Run(() =>
        (IReadOnlyList<HidDeviceInventory>)Inventory(Discover(token)), token);
    internal static Task<object[]> InspectCollectionsAsync(CancellationToken token) => Task.Run(() =>
        Discover(token, true).Where(e => e.Usb && e.Page >= 0xFF00).Select(e => (object)new {
            e.Name, e.Manufacturer, VendorId = e.Vendor.ToString("X4"), ProductId = e.Product.ToString("X4"),
            Interface = System.Text.RegularExpressions.Regex.Match(e.Path, @"&mi_([0-9a-f]{2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value,
            UsagePage = e.Page.ToString("X4"), Usage = e.Usage.ToString("X4"), e.InputLength, e.OutputLength, e.FeatureLength,
            InputReportIds = e.InputReports?.Select(id => (int)id).ToArray(), OutputReportIds = e.OutputReports?.Select(id => (int)id).ToArray() }).ToArray(), token);
    private HidProbeBatch ReadCore(IReadOnlySet<string> covered, CancellationToken token)
    {
        try
        {
            var endpoints = Discover(token);
            var inventory = Inventory(endpoints);
            lock (_failures)
            {
                var present = inventory.Select(i => i.Group).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string old in _failures.Keys.Where(k => !present.Contains(k)).ToArray()) _failures.Remove(old);
            }
            var output = new List<DeviceBattery>();
            // Bluetooth HID nodes can remain enumerated after disconnect. Generic Bluetooth reads
            // use the live-link GATT provider instead; Feature requests here are restricted to USB.
            var groups = endpoints.Where(e => e.Usb).GroupBy(e => e.Group).ToArray();
            int start = groups.Length == 0 ? 0 : _cursor % groups.Length;
            _cursor = groups.Length == 0 ? 0 : (start + 1) % groups.Length;
            int featureQueries = 0;
            foreach (var group in groups.Skip(start).Concat(groups.Take(start)))
            {
                token.ThrowIfCancellationRequested();
                if (covered.Contains(group.Key)) continue;
                var first = group.First();
                BatteryReading reading = BatteryReading.Empty(DeviceState.UnsupportedInterface);
                string note = group.Any(e => e.InputBattery)
                    ? "偵測到標準電池欄位，但僅位於輸入報告；本程式不讀鍵鼠輸入，電量暫不支援。"
                    : "已辨識 HID 裝置；沒有可安全讀取的標準電池 Feature，需裝置提供通用電量或已知專用協定。";
                bool retry;
                lock (_failures) retry = !_failures.TryGetValue(group.Key, out var failure) || DateTimeOffset.UtcNow >= failure.Next;
                if (retry)
                {
                    foreach (var endpoint in group.Where(e => e.Features.Count > 0))
                    {
                        if (featureQueries >= 8) break;
                        using var handle = Native.CreateFile(endpoint.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                        if (handle.IsInvalid) { reading = BatteryReading.Empty(DeviceState.AccessDenied); continue; }
                        if (!Native.HidD_GetPreparsedData(handle, out IntPtr preparsed)) continue;
                        try
                        {
                            foreach (var feature in endpoint.Features)
                            {
                                token.ThrowIfCancellationRequested();
                                if (featureQueries++ >= 8) break;
                                var bytes = new byte[endpoint.FeatureLength]; bytes[0] = feature.ReportId;
                                if (!Native.HidD_GetFeature(handle, bytes, bytes.Length)) { reading = BatteryReading.Empty(DeviceState.NoReply); continue; }
                                token.ThrowIfCancellationRequested();
                                if (bytes[0] != feature.ReportId || Native.HidP_GetUsageValue(2, feature.Page, feature.Link, feature.Usage, out uint value,
                                    preparsed, bytes, bytes.Length) != 0x00110000) continue;
                                int? percent = StandardHidProtocol.Percent(feature, value);
                                if (percent == null) continue;
                                reading = new(DeviceState.Connected, percent, null, DateTimeOffset.UtcNow);
                                note = "電量來自描述元明確宣告的標準電池 Feature；不讀輸入報告。名稱為裝置的 HID 產品描述，不保證是周邊型號。";
                                if (feature.Minimum != 0 || feature.Maximum != 100) note += " 百分比依 HID 標準電池範圍換算。";
                                break;
                            }
                        }
                        finally { Native.HidD_FreePreparsedData(preparsed); }
                        if (reading.State == DeviceState.Connected) break;
                    }
                    lock (_failures)
                    {
                        if (reading.State == DeviceState.Connected) _failures.Remove(group.Key);
                        else if (group.Any(e => e.Features.Count > 0))
                        {
                            int failures = _failures.TryGetValue(group.Key, out var previous) ? Math.Min(4, previous.Failures + 1) : 1;
                            _failures[group.Key] = (failures, DateTimeOffset.UtcNow.AddMinutes(1 << (failures - 1)));
                        }
                    }
                }
                if (reading.State == DeviceState.Connected || group.Any(e => e.Peripheral))
                    output.Add(new("hid-" + group.Key, first.Name, "HID · 通用電量", reading, PhysicalKey: group.Key, Note: note));
            }
            return new(output, inventory);
        }
        catch (OperationCanceledException) { return Status("標準 HID 查詢已停止；不沿用舊電量。"); }
        catch (Exception) { return Status("無法完成標準 HID 能力檢查；未讀取輸入或嘗試未知指令。"); }
    }
    private static HidDeviceInventory[] Inventory(List<Endpoint> endpoints) => endpoints.GroupBy(e => e.Group).Select(g => {
        var first = g.First(); return new HidDeviceInventory(g.Key, first.Name, first.Manufacturer, first.Vendor, first.Product,
            g.Any(e => e.Peripheral), g.Any(e => e.InputBattery), g.Any(e => e.Features.Count > 0), first.Usb); }).ToArray();
    internal static List<Endpoint> Discover(CancellationToken token, bool detailed = false)
    {
        var output = new List<Endpoint>();
        Native.HidD_GetHidGuid(out Guid guid);
        IntPtr list = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (list == new IntPtr(-1)) return output;
        try
        {
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index < 512 && output.Count < 128; index++)
            {
                token.ThrowIfCancellationRequested();
                var info = new Native.InterfaceData { Size = Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(list, IntPtr.Zero, ref guid, index, ref info)) break;
                Native.SetupDiGetDeviceInterfaceDetail(list, ref info, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
                if (size < 8 || size > 65536) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)size);
                string? path;
                var device = new Native.DeviceInfo { Size = Marshal.SizeOf<Native.DeviceInfo>() };
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetailWithInfo(list, ref info, detail, size, out _, ref device)) continue;
                    path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                }
                finally { Marshal.FreeHGlobal(detail); }
                if (path == null) continue;
                using var metadata = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (metadata.IsInvalid) continue;
                var attributes = new Native.Attributes { Size = Marshal.SizeOf<Native.Attributes>() };
                if (!Native.HidD_GetAttributes(metadata, ref attributes) || attributes.VendorId == 0) continue;
                string group = System.Text.RegularExpressions.Regex.Replace(path, "&col[0-9a-f]{2}", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var property = new Native.PropertyKey { Format = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), Id = 2 };
                var container = new byte[16];
                if (Native.SetupDiGetDeviceProperty(list, ref device, ref property, out uint type, container, 16, out uint required, 0)
                    && type == 13 && required == 16 && new Guid(container) != Guid.Empty) group = new Guid(container).ToString();
                if (!groups.Contains(group) && groups.Count >= 32) continue;
                groups.Add(group);
                bool usb = IsUsb(device.DevInst);
                if (!Native.HidD_GetPreparsedData(metadata, out IntPtr preparsed)) continue;
                try
                {
                    if (Native.HidP_GetCaps(preparsed, out var caps) != 0x00110000) continue;
                    var product = new StringBuilder(126); var manufacturer = new StringBuilder(126);
                    string name = Native.HidD_GetProductString(metadata, product, 252) ? BluetoothProtocol.Text(Encoding.UTF8.GetBytes(product.ToString())) : "";
                    string maker = Native.HidD_GetManufacturerString(metadata, manufacturer, 252) ? BluetoothProtocol.IdentityText(Encoding.UTF8.GetBytes(manufacturer.ToString())) : "";
                    if (name.Length == 0) name = $"HID 裝置 {attributes.VendorId:X4}:{attributes.ProductId:X4}";
                    bool peripheral = caps.UsagePage == 1 && caps.Usage is 2 or 4 or 5 or 6 or 7 || caps.UsagePage == 0x0D;
                    var features = Values(preparsed, 2, caps.FeatureValues);
                    bool inputBattery = Values(preparsed, 0, caps.InputValues).Any(StandardHidProtocol.BatteryUsage);
                    var safe = usb && caps.FeatureLength is > 1 and <= 256
                        ? StandardHidProtocol.SafeFeatures(features, Buttons(preparsed, caps.FeatureButtons)) : Array.Empty<HidValueCapability>();
                    byte[]? inputReports = detailed ? ReportIds(preparsed, 0, caps.InputValues, caps.InputButtons) : null;
                    byte[]? outputReports = detailed ? ReportIds(preparsed, 1, caps.OutputValues, caps.OutputButtons) : null;
                    output.Add(new(path, group, name, maker, attributes.VendorId, attributes.ProductId, peripheral, inputBattery, caps.FeatureLength, safe, usb,
                        caps.UsagePage, caps.Usage, caps.InputLength, caps.OutputLength, inputReports, outputReports));
                }
                finally { Native.HidD_FreePreparsedData(preparsed); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(list); }
        return output;
    }
    private static HidValueCapability[] Values(IntPtr preparsed, int reportType, ushort count)
    {
        if (count == 0 || count > 256) return Array.Empty<HidValueCapability>();
        IntPtr buffer = Marshal.AllocHGlobal(count * 72);
        try
        {
            if (Native.HidP_GetValueCaps(reportType, buffer, ref count, preparsed) != 0x00110000) return Array.Empty<HidValueCapability>();
            return Enumerable.Range(0, count).Select(i => Marshal.PtrToStructure<HidValueCapability>(IntPtr.Add(buffer, i * 72))).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static bool IsUsb(uint node)
    {
        // A HID child can report a HID bus GUID. Walk transport ancestors instead, stopping at
        // Bluetooth before reaching its USB radio so disconnected Bluetooth nodes cannot pass.
        var key = new Native.PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 24 };
        for (int depth = 0; depth < 8; depth++)
        {
            var data = new byte[256]; uint length = (uint)data.Length;
            if (Native.CM_Get_DevNode_Property(node, ref key, out uint type, data, ref length, 0) != 0 || type != 18 || length > data.Length) return false;
            string enumerator = Encoding.Unicode.GetString(data, 0, (int)length).TrimEnd('\0');
            if (enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return false;
            if (enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase)) return true;
            if (Native.CM_Get_Parent(out uint parent, node, 0) != 0) return false;
            node = parent;
        }
        return false;
    }
    private static byte[] ReportIds(IntPtr preparsed, int type, ushort values, ushort buttons) =>
        Values(preparsed, type, values).Select(v => v.ReportId).Concat(Buttons(preparsed, buttons, type) ?? new HashSet<byte>()).Distinct().OrderBy(v => v).ToArray();
    private static IReadOnlySet<byte>? Buttons(IntPtr preparsed, ushort count, int type = 2)
    {
        if (count == 0) return new HashSet<byte>();
        if (count > 256) return null;
        IntPtr buffer = Marshal.AllocHGlobal(count * 72);
        try
        {
            if (Native.HidP_GetButtonCaps(type, buffer, ref count, preparsed) != 0x00110000) return null;
            return Enumerable.Range(0, count).Select(i => Marshal.ReadByte(buffer, i * 72 + 2)).ToHashSet();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
