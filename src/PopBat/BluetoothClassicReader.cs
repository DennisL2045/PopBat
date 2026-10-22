using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace PopBat;

internal sealed class BluetoothClassicReader
{
    internal sealed record InspectionResult(string Name, string Stage, string? Error = null, int? ErrorCode = null);
    private readonly bool _diagnostics;
    internal List<InspectionResult> Inspection { get; } = new();
    internal BluetoothClassicReader(bool diagnostics = false) { _diagnostics = diagnostics; }
    private void Inspect(string name, string stage, Exception? error = null)
    { if (_diagnostics && Inspection.Count < 16) Inspection.Add(new(CleanName(name), stage, error?.GetType().Name, error?.HResult)); }
    private sealed class Entry { internal int Failures; internal DateTimeOffset Next; }
    private readonly Dictionary<string, Entry> _entries = new();
    private Task<IReadOnlyList<DeviceBattery>>? _pending;
    private IReadOnlyList<DeviceBattery> _progress = Array.Empty<DeviceBattery>();
    private int _rotation;
    internal bool IsPending => _pending is { IsCompleted: false };
    internal void RetryNow() { if (!IsPending) foreach (var entry in _entries.Values) entry.Next = default; }

    internal async Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken token,
        Action<IReadOnlyList<DeviceBattery>>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        if (IsPending) return Array.Empty<DeviceBattery>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        _progress = Array.Empty<DeviceBattery>();
        Inspection.Clear();
        _pending = ReadCoreAsync(deadline.Token, progress);
        try { return await _pending.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return _progress; }
    }

    private async Task<IReadOnlyList<DeviceBattery>> ReadCoreAsync(CancellationToken token,
        Action<IReadOnlyList<DeviceBattery>>? progress)
    {
        var output = new List<DeviceBattery>();
        try
        {
            // Metadata only locates an existing paired Classic link; never request OS battery properties.
            var found = await CompleteAsync(DeviceInformation.FindAllAsync(
                BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
                new[] { "System.Devices.Aep.ContainerId" }, DeviceInformationKind.AssociationEndpoint), token);
            var candidates = found.Take(256).Where(d => d.Pairing.IsPaired).Take(32).ToArray();
            Inspect("藍牙裝置", "ConnectedPairedCandidates:" + candidates.Length);
            var present = candidates.Select(d => d.Id).ToHashSet();
            foreach (string stale in _entries.Keys.Where(k => !present.Contains(k)).ToArray()) _entries.Remove(stale);
            int start = candidates.Length == 0 ? 0 : _rotation % candidates.Length;
            _rotation = candidates.Length == 0 ? 0 : (start + 1) % candidates.Length;
            foreach (var candidate in Enumerable.Range(0, candidates.Length).Select(i => candidates[(start + i) % candidates.Length]).Take(4))
            {
                token.ThrowIfCancellationRequested();
                if (!_entries.TryGetValue(candidate.Id, out var entry)) _entries[candidate.Id] = entry = new();
                if (DateTimeOffset.UtcNow < entry.Next) continue;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                string stage = "OpenConnectedDevice";
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                DeviceBattery? unavailable = null;
                void Publish(DeviceBattery reading)
                { output.Add(reading); _progress = output.ToArray(); progress?.Invoke(_progress); }
                try
                {
                    using var device = await CompleteAsync(BluetoothDevice.FromIdAsync(candidate.Id), deadline.Token);
                    if (device == null || device.ConnectionStatus != BluetoothConnectionStatus.Connected
                        || device.ClassOfDevice.MajorClass != BluetoothMajorClass.AudioVideo)
                    { Inspect(candidate.Name, "NotConnectedClassicAudio"); continue; }
                    string? group = candidate.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var container)
                        && container is Guid id && id != Guid.Empty ? id.ToString() : null;
                    unavailable = new("fastpair-" + candidate.Id, CleanName(device.Name), "藍牙 · 耳機",
                        BatteryReading.Empty(DeviceState.NoReply), PhysicalKey: group,
                        Note: "名稱來自目前藍牙連線資訊；本輪未取得有效電量，不沿用先前數值。");
                    stage = "DiscoverFastPairService";
                    var services = await CompleteAsync(device.GetRfcommServicesForIdAsync(
                        RfcommServiceId.FromUuid(FastPairProtocol.ServiceUuid), BluetoothCacheMode.Uncached), deadline.Token);
                    try
                    {
                        var service = services.Services.FirstOrDefault(s => s.ServiceId.Uuid == FastPairProtocol.ServiceUuid
                            && s.ConnectionHostName.Type == HostNameType.Bluetooth);
                        FastPairSnapshot? snapshot = null;
                        if (service != null && device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                        {
                            Inspect(device.Name, "DiscoveryMs:" + elapsed.ElapsedMilliseconds);
                            stage = "ConnectAuthenticatedBluetooth";
                            using var socket = new StreamSocket();
                            // Restrict StreamSocket to the verified Bluetooth service, never an IP host or audio profile.
                            await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName,
                                SocketProtectionLevel.BluetoothEncryptionWithAuthentication).AsTask(deadline.Token);
                            Inspect(device.Name, "ConnectedMs:" + elapsed.ElapsedMilliseconds);
                            deadline.Token.ThrowIfCancellationRequested();
                            using var channel = new StatusChannel(device, socket);
                            stage = "ReceiveFastPairStatus";
                            snapshot = await FastPairProtocol.ReadAsync(channel, deadline.Token);
                        }
                        deadline.Token.ThrowIfCancellationRequested();
                        if (device.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;
                        string key = "fastpair-" + candidate.Id;
                        Publish(snapshot != null ? FastPairProtocol.ToDevice(key, device.Name, group, snapshot)
                            : new(key, CleanName(device.Name), "藍牙 · 耳機", BatteryReading.Empty(DeviceState.UnsupportedInterface),
                                PhysicalKey: group, Note: "名稱來自目前藍牙連線資訊；本次沒有可直接驗證的耳機電量。"));
                        if (snapshot != null) { entry.Failures = 0; entry.Next = default; }
                        else Backoff(entry);
                        Inspect(device.Name, snapshot != null ? "BatteryReceived:Active=" + snapshot.ActiveComponents : "NoDirectBattery");
                    }
                    finally { foreach (var service in services.Services) service.Dispose(); }
                }
                catch (Exception error) when (deadline.IsCancellationRequested)
                {
                    Inspect(candidate.Name, stage + ":TimeoutOrCancelled", error); Backoff(entry); token.ThrowIfCancellationRequested();
                    if (unavailable != null && !output.Any(d => d.Key == unavailable.Key)) Publish(unavailable);
                }
                catch (Exception error)
                {
                    Inspect(candidate.Name, stage, error); Backoff(entry);
                    if (unavailable != null && !output.Any(d => d.Key == unavailable.Key)) Publish(unavailable);
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception error) { Inspect("藍牙裝置", "Enumeration", error); }
        return output.ToArray();
    }

    private static string CleanName(string name)
    {
        string value = BluetoothProtocol.IdentityText(System.Text.Encoding.UTF8.GetBytes(name));
        return value.Length > 0 ? value : "藍牙耳機";
    }
    private static void Backoff(Entry entry)
    {
        entry.Failures = Math.Min(4, entry.Failures + 1);
        entry.Next = DateTimeOffset.UtcNow.AddMinutes(1 << (entry.Failures - 1));
    }

    // Keep one native round pending until the actual operation completes, even if the UI deadline expires.
    private static async Task<T> CompleteAsync<T>(IAsyncOperation<T> operation, CancellationToken token)
    {
        try
        {
            T result = await operation.AsTask();
            if (token.IsCancellationRequested)
            {
                if (result is BluetoothDevice device) device.Dispose();
                if (result is RfcommDeviceServicesResult services) foreach (var service in services.Services) service.Dispose();
                token.ThrowIfCancellationRequested();
            }
            return result;
        }
        finally { operation.Close(); }
    }

    private sealed class StatusChannel : IFastPairStatusChannel, IDisposable
    {
        private readonly BluetoothDevice _device;
        private readonly StreamSocket _socket;
        private readonly DataReader _reader;
        internal StatusChannel(BluetoothDevice device, StreamSocket socket)
        { _device = device; _socket = socket; _reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial }; }
        public bool Connected => _device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        public async Task RequestActiveComponentsAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var close = token.Register(_socket.Dispose);
            using var writer = new DataWriter(_socket.OutputStream);
            try
            {
                writer.WriteBytes(FastPairProtocol.ActiveComponentsRequest());
                await CompleteAsync(writer.StoreAsync(), token);
            }
            finally { writer.DetachStream(); }
        }
        public async Task<int> ReceiveAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var close = token.Register(_socket.Dispose);
            uint received = await CompleteAsync(_reader.LoadAsync((uint)count), token);
            for (int i = 0; i < received; i++) buffer[offset + i] = _reader.ReadByte();
            return (int)received;
        }
        public void Dispose() { _reader.Dispose(); }
    }
}
