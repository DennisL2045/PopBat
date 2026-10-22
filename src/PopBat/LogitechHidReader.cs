using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PopBat;

public sealed record DeviceBattery(string Key, string Name, string Connection, BatteryReading Reading, string? Level = null, string? PhysicalKey = null, string? Note = null);

internal interface ILogitechChannel
{
    Task<LogitechReply> QueryAsync(LogitechQuery query, CancellationToken token);
}

internal sealed class LogitechDeviceClient
{
    internal string? Name { get; private set; }
    private LogitechFeature? _battery;
    private LogitechOperation _operation;
    private int _version;
    private DateTimeOffset _initializedAt;
    internal int Generation { get; private set; }
    internal void Reset() { Name = null; _battery = null; _version = 0; }
    internal async Task<LogitechBattery?> ReadAsync(ILogitechChannel channel, byte slot, CancellationToken token)
    {
        if (_version != 0 && DateTimeOffset.UtcNow - _initializedAt >= TimeSpan.FromMinutes(5)) Reset();
        if (_version == 0)
        {
            var ping = await channel.QueryAsync(new(slot, LogitechOperation.Ping), token);
            if (ping.Error == 1) _version = 1;
            else if (ping.Data is { Length: >= 3 } p && p[0] >= 2) _version = p[0];
            else return null;
            Generation++; _initializedAt = DateTimeOffset.UtcNow;
            if (_version >= 2)
            {
                var nameFeature = await LookupAsync(channel, slot, LogitechProtocol.NameFeature, token);
                if (nameFeature != null)
                {
                    var length = await channel.QueryAsync(new(slot, LogitechOperation.NameLength, nameFeature.Value), token);
                    if (length.Data is { Length: > 0 } n && n[0] > 0 && n[0] <= LogitechProtocol.MaxNameBytes)
                    {
                        var bytes = new List<byte>(n[0]);
                        while (bytes.Count < n[0])
                        {
                            var chunk = await channel.QueryAsync(new(slot, LogitechOperation.NameChunk, nameFeature.Value, (byte)bytes.Count), token);
                            if (chunk.Data == null || chunk.Data.Length == 0) break;
                            bytes.AddRange(chunk.Data.Take(Math.Min(n[0] - bytes.Count, chunk.Data.Length)));
                        }
                        if (bytes.Count == n[0]) { string name = LogitechProtocol.CleanName(bytes.ToArray()); if (name.Length > 0) Name = name; }
                    }
                }
                foreach (ushort id in new[] { LogitechProtocol.UnifiedFeature, LogitechProtocol.StatusFeature, LogitechProtocol.VoltageFeature })
                {
                    _battery = await LookupAsync(channel, slot, id, token);
                    if (_battery != null)
                    {
                        _operation = id switch { LogitechProtocol.UnifiedFeature => LogitechOperation.UnifiedBattery,
                            LogitechProtocol.StatusFeature => LogitechOperation.BatteryStatus, _ => LogitechOperation.BatteryVoltage };
                        break;
                    }
                }
            }
        }
        if (_version >= 2)
        {
            if (_battery == null) return null;
            var reply = await channel.QueryAsync(new(slot, _operation, _battery.Value), token);
            return reply.Data == null ? null : LogitechProtocol.Decode(_operation, reply.Data);
        }
        var legacy = await channel.QueryAsync(new(slot, LogitechOperation.LegacyCharge), token);
        if (legacy.Data != null) return LogitechProtocol.Decode(LogitechOperation.LegacyCharge, legacy.Data);
        legacy = await channel.QueryAsync(new(slot, LogitechOperation.LegacyStatus), token);
        return legacy.Data == null ? null : LogitechProtocol.Decode(LogitechOperation.LegacyStatus, legacy.Data);
    }
    private static async Task<LogitechFeature?> LookupAsync(ILogitechChannel channel, byte slot, ushort id, CancellationToken token)
    {
        var reply = await channel.QueryAsync(new(slot, LogitechOperation.Lookup, new(id, 0)), token);
        return reply.Data is { Length: > 0 } p && p[0] != 0 ? new(id, p[0]) : null;
    }
}

internal sealed class LogitechHidReader
{
    private const int MaxEndpoints = 16, MaxDevices = 32;
    private sealed record Endpoint(string Path, string Group, string Product, ushort Pid, ushort InputLength, ushort OutputLength, bool LongReport, bool Supported,
        string? ShortPath = null, ushort ShortInput = 0, ushort ShortOutput = 0);
    private sealed class SlotState
    {
        internal readonly LogitechDeviceClient Client = new();
        internal string? Name;
        internal int Failures;
        internal DateTimeOffset NextAttempt;
        internal int Generation;
    }
    private sealed class EndpointState
    {
        internal readonly int Id;
        internal readonly Dictionary<byte, SlotState> Slots = new();
        internal byte Cursor = 1;
        internal EndpointState(int id) { Id = id; }
    }
    private readonly Dictionary<string, EndpointState> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;
    private int _endpointCursor;
    internal void RetryNow() { foreach (var endpoint in _cache.Values) foreach (var slot in endpoint.Slots.Values) slot.NextAttempt = default; }
    internal Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken token, IReadOnlySet<string>? covered = null) => Task.Run(async () =>
    {
        CancellationToken callerToken = token;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(12)); token = budget.Token;
        var output = new List<DeviceBattery>();
        List<Endpoint> endpoints;
        try { endpoints = Discover(token); }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        { return (IReadOnlyList<DeviceBattery>)Array.Empty<DeviceBattery>(); }
        catch (Exception e) when (e is IOException or Win32Exception or UnauthorizedAccessException)
        { return (IReadOnlyList<DeviceBattery>)new[] { new DeviceBattery("logitech-status", "羅技裝置", "裝置偵測", BatteryReading.Empty(DeviceState.ReadError)) }; }
        var groups = endpoints.Select(e => e.Group).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string stale in _cache.Keys.Where(k => !groups.Contains(k)).ToArray()) _cache.Remove(stale);
        try
        {
        foreach (var endpoint in endpoints.Skip(endpoints.Count == 0 ? 0 : _endpointCursor % endpoints.Count).Concat(endpoints.Take(endpoints.Count == 0 ? 0 : _endpointCursor % endpoints.Count)))
        {
            token.ThrowIfCancellationRequested();
            if (covered?.Contains(endpoint.Group) == true) continue;
            if (!_cache.TryGetValue(endpoint.Group, out var state)) _cache[endpoint.Group] = state = new(++_nextId);
            string key = "logitech-" + state.Id;
            if (!endpoint.Supported)
            { output.Add(new(key, endpoint.Product, "USB / 藍牙", BatteryReading.Empty(DeviceState.UnsupportedInterface))); continue; }
            using var handle = Native.CreateFile(endpoint.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid)
            { output.Add(new(key, endpoint.Product, "HID++", BatteryReading.Empty(Marshal.GetLastWin32Error() == 5 ? DeviceState.AccessDenied : DeviceState.ReadError))); continue; }
            using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, true);
            using var shortHandle = endpoint.ShortPath == null ? null : Native.CreateFile(endpoint.ShortPath, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            using var shortStream = shortHandle == null || shortHandle.IsInvalid ? null : new FileStream(shortHandle, FileAccess.ReadWrite, 1, true);
            var channel = new Channel(stream, endpoint.InputLength, endpoint.OutputLength, endpoint.LongReport, shortStream, endpoint.ShortInput, endpoint.ShortOutput);
            bool receiver = endpoint.Pid >= 0xC500 && endpoint.Pid <= 0xC5FF;
            // Fixed receiver slots are protocol addresses, never arbitrary command probing.
            var slots = receiver ? Enumerable.Range(0, 6).Select(i => (byte)(1 + (state.Cursor - 1 + i) % 6)).ToArray() : new byte[] { 0xFF };
            state.Cursor = (byte)(state.Cursor % 6 + 1); // Fair ordering across receivers with several slots.
            foreach (byte slot in slots)
            {
                if (output.Count >= MaxDevices) break;
                if (!state.Slots.TryGetValue(slot, out var slotState)) state.Slots[slot] = slotState = new();
                string deviceKey = key + "-" + slot;
                if (DateTimeOffset.UtcNow < slotState.NextAttempt)
                {
                    if (slotState.Name != null) output.Add(new(deviceKey, slotState.Name, receiver ? "2.4 GHz · 接收器" : "USB / 藍牙 · HID++", BatteryReading.Empty(DeviceState.NoReply)));
                    continue;
                }
                try
                {
                    var battery = await slotState.Client.ReadAsync(channel, slot, token);
                    if (slotState.Generation != slotState.Client.Generation)
                    { slotState.Name = slotState.Client.Name; slotState.Generation = slotState.Client.Generation; }
                    if (battery != null && receiver && slotState.Name == null && endpoint.LongReport)
                    {
                        var name = await channel.QueryAsync(new(0xFF, LogitechOperation.ReceiverName, Parameter: slot, Bolt: endpoint.Pid == 0xC548), token);
                        slotState.Name = LogitechProtocol.ReceiverName(name.Data, endpoint.Pid == 0xC548);
                    }
                    if (battery != null)
                    {
                        slotState.Failures = 0; slotState.NextAttempt = default;
                        string name = slotState.Name ?? (receiver ? $"羅技裝置（槽位 {slot}，名稱未回覆）" : endpoint.Product);
                        output.Add(new(deviceKey, name, receiver ? "2.4 GHz · 接收器" : "USB / 藍牙 · HID++",
                            new(DeviceState.Connected, battery.Percent, battery.Charging, DateTimeOffset.UtcNow), battery.Level));
                    }
                    else
                    {
                        slotState.Client.Reset();
                        slotState.Failures = Math.Min(slotState.Failures + 1, 4);
                        slotState.NextAttempt = DateTimeOffset.UtcNow.AddSeconds(60 * (1 << (slotState.Failures - 1)));
                        if (slotState.Name != null) output.Add(new(deviceKey, slotState.Name, "HID++", BatteryReading.Empty(DeviceState.NoReply)));
                    }
                }
                catch (OperationCanceledException)
                { slotState.Client.Reset(); throw; }
                catch (IOException)
                {
                    slotState.Client.Reset(); slotState.NextAttempt = DateTimeOffset.UtcNow.AddMinutes(2);
                    if (slotState.Name != null) output.Add(new(deviceKey, slotState.Name, "HID++", BatteryReading.Empty(DeviceState.ReadError)));
                    break;
                }
            }
            if (!output.Any(d => d.Key.StartsWith(key + "-", StringComparison.Ordinal)))
                output.Add(new(key, endpoint.Product, receiver ? "2.4 GHz · 接收器" : "USB / 藍牙", BatteryReading.Empty(DeviceState.NoReply)));
        }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        { if (output.Count < MaxDevices) output.Add(new("logitech-budget", "羅技裝置查詢", "本輪查詢時間已達上限；下輪繼續", BatteryReading.Empty(DeviceState.NoReply))); }
        _endpointCursor++;
        foreach (var endpoint in endpoints)
        {
            if (!_cache.TryGetValue(endpoint.Group, out var state)) continue;
            string prefix = "logitech-" + state.Id;
            for (int index = 0; index < output.Count; index++)
                if (output[index].Key == prefix || output[index].Key.StartsWith(prefix + "-", StringComparison.Ordinal))
                    output[index] = output[index] with { PhysicalKey = endpoint.Group };
        }
        return (IReadOnlyList<DeviceBattery>)output.Take(MaxDevices).ToArray();
    }, token);

    private sealed class Channel : ILogitechChannel
    {
        private readonly FileStream _stream;
        private readonly int _inputLength, _outputLength;
        private readonly bool _long;
        private readonly FileStream? _short;
        private readonly int _shortInput, _shortOutput;
        internal Channel(FileStream stream, int inputLength, int outputLength, bool longReport, FileStream? shortStream, int shortInput, int shortOutput)
        { _stream = stream; _inputLength = inputLength; _outputLength = outputLength; _long = longReport; _short = shortStream; _shortInput = shortInput; _shortOutput = shortOutput; }
        public async Task<LogitechReply> QueryAsync(LogitechQuery query, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(650);
            bool shortRequest = _short != null && query.Operation is LogitechOperation.LegacyCharge or LogitechOperation.LegacyStatus or LogitechOperation.ReceiverName or LogitechOperation.Ping;
            byte[] request = LogitechProtocol.Build(query, (byte)Random.Shared.Next(1, 16), (byte)Random.Shared.Next(1, 256), shortRequest ? _shortOutput : _outputLength, !shortRequest && _long);
            try
            {
                await (shortRequest ? _short! : _stream).WriteAsync(request, deadline.Token);
                var main = ReadMatchAsync(_stream, _inputLength, request, query.Operation, deadline.Token);
                if (_short == null) return await main;
                var other = ReadMatchAsync(_short, _shortInput, request, query.Operation, deadline.Token);
                try { return await await Task.WhenAny(main, other); }
                finally
                {
                    deadline.Cancel();
                    // Drain cancellations before the next transaction; no abandoned reads steal replies.
                    try { await Task.WhenAll(main, other); } catch (OperationCanceledException) { }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            return LogitechReply.Missing;
        }
        private static async Task<LogitechReply> ReadMatchAsync(FileStream stream, int length, byte[] request, LogitechOperation operation, CancellationToken token)
        {
            var buffer = new byte[length];
            for (int i = 0; i < 32; i++)
            {
                int received = await stream.ReadAsync(buffer, token);
                if (received == 0) return LogitechReply.Missing;
                var reply = LogitechProtocol.Match(buffer.AsSpan(0, received), request, operation);
                if (reply != null) return reply;
            }
            return LogitechReply.Missing;
        }
    }

    private static List<Endpoint> Discover(CancellationToken token)
    {
        var found = new List<Endpoint>();
        Native.HidD_GetHidGuid(out Guid guid);
        IntPtr list = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (list == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            int targets = 0;
            for (uint i = 0; i < 1024 && targets < 64; i++)
            {
                token.ThrowIfCancellationRequested();
                var info = new Native.InterfaceData { Size = Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(list, IntPtr.Zero, ref guid, i, ref info))
                { if (Marshal.GetLastWin32Error() == 259) break; throw new Win32Exception(Marshal.GetLastWin32Error()); }
                Native.SetupDiGetDeviceInterfaceDetail(list, ref info, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
                if (size < 8 || size > 65536) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)size);
                var deviceInfo = new Native.DeviceInfo { Size = Marshal.SizeOf<Native.DeviceInfo>() };
                string? path;
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetailWithInfo(list, ref info, detail, size, out _, ref deviceInfo)) continue;
                    path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                }
                finally { Marshal.FreeHGlobal(detail); }
                if (path == null || !LogitechProtocol.IsTargetPath(path)) continue;
                targets++;
                using var metadata = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (metadata.IsInvalid) continue;
                var attributes = new Native.Attributes { Size = Marshal.SizeOf<Native.Attributes>() };
                if (!Native.HidD_GetAttributes(metadata, ref attributes) || attributes.VendorId != LogitechProtocol.VendorId) continue;
                if (!Native.HidD_GetPreparsedData(metadata, out IntPtr preparsed)) continue;
                try
                {
                    if (Native.HidP_GetCaps(preparsed, out var caps) != 0x00110000) continue;
                    string group = System.Text.RegularExpressions.Regex.Replace(path, "&col[0-9a-f]{2}", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    // This grouping property stays in memory and is never shown, hashed or saved.
                    var containerKey = new Native.PropertyKey { Format = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), Id = 2 };
                    var container = new byte[16];
                    if (Native.SetupDiGetDeviceProperty(list, ref deviceInfo, ref containerKey, out uint propertyType, container, 16, out uint required, 0)
                        && propertyType == 13 && required == 16 && new Guid(container) != Guid.Empty) group = new Guid(container).ToString();
                    bool control = LogitechProtocol.IsControlCollection(caps.UsagePage, caps.Usage);
                    bool longReport = control && HasReport(preparsed, caps.InputValues, caps.InputButtons, 0, 0x11) && HasReport(preparsed, caps.OutputValues, caps.OutputButtons, 1, 0x11);
                    bool shortReport = control && HasReport(preparsed, caps.InputValues, caps.InputButtons, 0, 0x10) && HasReport(preparsed, caps.OutputValues, caps.OutputButtons, 1, 0x10);
                    bool supported = LogitechProtocol.IsControlCollection(caps.UsagePage, caps.Usage) && caps.InputLength >= (longReport ? 20 : 7)
                        && caps.InputLength <= 64 && caps.OutputLength >= (longReport ? 20 : 7) && caps.OutputLength <= 64 && (longReport || shortReport);
                    var product = new StringBuilder(126);
                    string name = Native.HidD_GetProductString(metadata, product, 252) ? product.ToString() : "羅技裝置";
                    name = new string(name.Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
                    if (name.Length == 0) name = "羅技裝置";
                    var endpoint = new Endpoint(path, group, name, attributes.ProductId, caps.InputLength, caps.OutputLength, longReport, supported);
                    int existing = found.FindIndex(e => string.Equals(e.Group, group, StringComparison.OrdinalIgnoreCase));
                    if (existing >= 0)
                    {
                        var old = found[existing];
                        if (supported && !old.Supported) found[existing] = endpoint;
                        else if (supported && longReport && !old.LongReport)
                            found[existing] = endpoint with { ShortPath = old.Path, ShortInput = old.InputLength, ShortOutput = old.OutputLength };
                        else if (supported && !longReport && old.LongReport)
                            found[existing] = old with { ShortPath = path, ShortInput = caps.InputLength, ShortOutput = caps.OutputLength };
                    }
                    else if (found.Count < MaxEndpoints) found.Add(endpoint);
                }
                finally { Native.HidD_FreePreparsedData(preparsed); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(list); }
        return found;
    }
    private static bool HasReport(IntPtr preparsed, ushort values, ushort buttons, int type, byte id)
    {
        foreach (bool button in new[] { false, true })
        {
            ushort count = button ? buttons : values;
            if (count == 0 || count > 128) continue;
            IntPtr buffer = Marshal.AllocHGlobal(count * 72);
            try
            {
                int status = button ? Native.HidP_GetButtonCaps(type, buffer, ref count, preparsed) : Native.HidP_GetValueCaps(type, buffer, ref count, preparsed);
                if (status == 0x00110000) for (int i = 0; i < count; i++) if (Marshal.ReadByte(buffer, i * 72 + 2) == id) return true;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return false;
    }
}
