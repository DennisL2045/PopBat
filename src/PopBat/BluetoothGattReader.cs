using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace PopBat;

internal sealed class BluetoothGattReader
{
    private sealed class Entry
    {
        internal BluetoothIdentity? Identity;
        internal DateTimeOffset Verified, Next;
        internal int Failures;
    }
    private readonly Dictionary<string, Entry> _entries = new();
    private Task<IReadOnlyList<DeviceBattery>>? _pending;
    internal bool IsPending => _pending is { IsCompleted: false };
    private IReadOnlyList<DeviceBattery> _progress = Array.Empty<DeviceBattery>();
    private int _rotation;
    internal void RetryNow() { if (IsPending) return; foreach (var entry in _entries.Values) entry.Next = default; }
    private static IReadOnlyList<DeviceBattery> Status(DeviceState state, string detail) => new[] {
        new DeviceBattery("bluetooth-status", "藍牙裝置", "藍牙 · 裝置直接讀取", BatteryReading.Empty(state), detail) };

    internal async Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken token,
        Action<IReadOnlyList<DeviceBattery>>? progress = null, IReadOnlySet<string>? covered = null)
    {
        token.ThrowIfCancellationRequested();
        // A Windows GATT request can outlive cancellation. Retain ONE operation until completion;
        // do not start another connection/read while it is pending, or publish a late old reading.
        if (_pending is { IsCompleted: false })
            return Status(DeviceState.NoReply, "前一次藍牙通訊仍在結束；暫緩新查詢，不沿用舊電量。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        _progress = Array.Empty<DeviceBattery>();
        _pending = ReadCoreAsync(deadline.Token, progress, covered?.ToHashSet(StringComparer.OrdinalIgnoreCase));
        try { return await _pending.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return _progress.Concat(Status(DeviceState.NoReply, "部分藍牙直接通訊逾時；保留本輪已完成讀數，不採用舊電量。")).ToArray(); }
    }

    private async Task<IReadOnlyList<DeviceBattery>> ReadCoreAsync(CancellationToken token,
        Action<IReadOnlyList<DeviceBattery>>? progress, IReadOnlySet<string>? covered)
    {
        try
        {
            // Connection status is used solely to locate an existing link, never as a battery value.
            var devices = await CompleteAsync(DeviceInformation.FindAllAsync(
                BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
                new[] { "System.Devices.Aep.ContainerId" }, DeviceInformationKind.AssociationEndpoint), token);
            var candidates = devices.Take(256).Where(d => d.Pairing.IsPaired &&
                !(d.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var container) && container is Guid id
                    && covered?.Contains(id.ToString()) == true)).Take(32).ToArray();
            var keys = candidates.Select(d => d.Id).ToHashSet();
            foreach (string stale in _entries.Keys.Where(k => !keys.Contains(k)).ToArray()) _entries.Remove(stale);
            var output = new List<DeviceBattery>();
            int start = candidates.Length == 0 ? 0 : _rotation % candidates.Length;
            _rotation = candidates.Length == 0 ? 0 : (start + 1) % candidates.Length;
            // Complete quick standard reads before notification-only devices. The connection
            // name is a scheduling hint only; protocol decisions still use verified identity.
            var ordered = Enumerable.Range(0, candidates.Length).Select(i => candidates[(start + i) % candidates.Length])
                .OrderBy(d => BluetoothProtocol.UnreliableBatterySource(
                    _entries.TryGetValue(d.Id, out var cached) && cached.Identity != null ? cached.Identity.Name : d.Name)).Take(4);
            foreach (var candidate in ordered)
            {
                token.ThrowIfCancellationRequested();
                if (!_entries.TryGetValue(candidate.Id, out var entry)) _entries[candidate.Id] = entry = new();
                if (DateTimeOffset.UtcNow < entry.Next) continue;
                if (DateTimeOffset.UtcNow - entry.Verified > TimeSpan.FromMinutes(5)) entry.Identity = null;
                try
                {
                using var device = await CompleteAsync(BluetoothLEDevice.FromIdAsync(candidate.Id), token);
                if (device == null || device.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;
                using var channel = new GattChannel(device, candidate.Name);
                var result = await BluetoothProtocol.ReadAsync(channel, entry.Identity, token);
                token.ThrowIfCancellationRequested();
                if (result != null)
                {
                    bool newIdentity = entry.Identity == null;
                    entry.Identity = result.Identity;
                    if (newIdentity) entry.Verified = DateTimeOffset.UtcNow;
                    string? group = candidate.Properties.TryGetValue("System.Devices.Aep.ContainerId", out object? container) && container is Guid id && id != Guid.Empty ? id.ToString() : null;
                    output.Add(new("ble-" + candidate.Id, result.Identity.Name, "藍牙 LE · 通用電量", result.Reading,
                        result.Reading.State == DeviceState.UnsupportedInterface ? "裝置未提供可讀取的標準 Battery Level；未使用 Windows 電量。" : null, group, result.Identity.Note));
                    _progress = output.ToArray();
                    progress?.Invoke(_progress);
                }
                if (result?.Reading.Percent != null) { entry.Failures = 0; entry.Next = default; }
                else
                {
                    entry.Failures = Math.Min(4, entry.Failures + 1);
                    entry.Next = DateTimeOffset.UtcNow.AddMinutes(1 << (entry.Failures - 1));
                }
                }
                catch (OperationCanceledException)
                {
                    entry.Failures = Math.Min(4, entry.Failures + 1);
                    entry.Next = DateTimeOffset.UtcNow.AddMinutes(1 << (entry.Failures - 1)); throw;
                }
                catch (Exception)
                {
                    // A blocked/unavailable peripheral must not discard the other devices' readings.
                    entry.Identity = null; entry.Next = DateTimeOffset.UtcNow.AddMinutes(2);
                }
            }
            return output.Count > 0 ? output : Status(DeviceState.NoReply,
                "尚未找到已連線且可直接讀取標準電量的藍牙裝置。請確認連線並喚醒裝置；舊配對紀錄不會顯示。");
        }
        catch (OperationCanceledException) { return _progress.Concat(Status(DeviceState.NoReply, "藍牙讀取已停止；只保留本輪完成的讀數。")).ToArray(); }
        catch (UnauthorizedAccessException) { return Status(DeviceState.AccessDenied, "藍牙直接通訊存取受限；不提升權限或讀取快取。"); }
        catch (Exception) { return Status(DeviceState.NoReply, "本次藍牙直接通訊沒有有效回覆；沒有讀取 Windows 電量。"); }
    }

    // Await the actual OS operation before releasing its device/service. The caller has its own
    // UI deadline, and ReadAsync keeps this entire round pending until this operation finishes.
    private static async Task<T> CompleteAsync<T>(IAsyncOperation<T> operation, CancellationToken token)
    {
        try
        {
            T result = await operation.AsTask();
            if (token.IsCancellationRequested)
            {
                if (result is BluetoothLEDevice device) device.Dispose();
                if (result is GattDeviceServicesResult services) foreach (var service in services.Services) service.Dispose();
                token.ThrowIfCancellationRequested();
            }
            return result;
        }
        finally { operation.Close(); }
    }

    private sealed class GattChannel : IBluetoothChannel, IDisposable
    {
        private readonly BluetoothLEDevice _device;
        private readonly Dictionary<ushort, GattDeviceService?> _services = new();
        private GattCharacteristic? _battery;
        public string ConnectionName { get; }
        internal GattChannel(BluetoothLEDevice device, string connectionName) { _device = device; ConnectionName = connectionName; }
        public bool Connected => _device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        private static Guid Uuid(ushort value) => new($"0000{value:x4}-0000-1000-8000-00805f9b34fb");
        private async Task<GattCharacteristic?> CharacteristicAsync(ushort serviceId, ushort characteristicId, CancellationToken token)
        {
            BluetoothProtocol.Validate(serviceId, characteristicId);
            token.ThrowIfCancellationRequested();
            if (!Connected) return null;
            if (!_services.TryGetValue(serviceId, out var service))
            {
                var found = await CompleteAsync(_device.GetGattServicesForUuidAsync(Uuid(serviceId), BluetoothCacheMode.Uncached), token);
                service = found.Status == GattCommunicationStatus.Success ? found.Services.FirstOrDefault() : null;
                foreach (var extra in found.Services) if (!ReferenceEquals(extra, service)) extra.Dispose();
                _services[serviceId] = service;
            }
            if (service == null || !Connected) return null;
            var characteristics = await CompleteAsync(service.GetCharacteristicsForUuidAsync(Uuid(characteristicId), BluetoothCacheMode.Uncached), token);
            var characteristic = characteristics.Status == GattCommunicationStatus.Success ? characteristics.Characteristics.FirstOrDefault() : null;
            if (characteristic == null || !Connected) return null;
            if (serviceId == 0x180F && characteristicId == 0x2A19) _battery = characteristic;
            return characteristic;
        }
        public async Task<byte[]?> ReadAsync(ushort serviceId, ushort characteristicId, CancellationToken token)
        {
            var characteristic = await CharacteristicAsync(serviceId, characteristicId, token);
            if (characteristic == null || !characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read)) return null;
            var value = await CompleteAsync(characteristic.ReadValueAsync(BluetoothCacheMode.Uncached), token);
            int maximum = characteristicId == 0x2A19 ? 1 : characteristicId == 0x2A50 ? 7 : 80;
            if (value.Status != GattCommunicationStatus.Success || !Connected || value.Value == null || value.Value.Length == 0 || value.Value.Length > maximum) return null;
            using var reader = DataReader.FromBuffer(value.Value);
            var bytes = new byte[(int)value.Value.Length]; reader.ReadBytes(bytes); return bytes;
        }
        public async Task<BluetoothBatteryUpdate?> ObserveBatteryAsync(CancellationToken token)
        {
            var characteristic = _battery ?? await CharacteristicAsync(0x180F, 0x2A19, token);
            if (characteristic == null || !Connected ||
                !characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)) return null;
            // Read the existing configuration only. Windows' HID client already enables BAS
            // notifications for this device; never write CCCD, start a session, or read HID input.
            var descriptors = await CompleteAsync(characteristic.GetDescriptorsForUuidAsync(
                new Guid("00002902-0000-1000-8000-00805f9b34fb"), BluetoothCacheMode.Uncached), token);
            var descriptor = descriptors.Status == GattCommunicationStatus.Success ? descriptors.Descriptors.FirstOrDefault() : null;
            if (descriptor == null || !Connected) return null;
            var configuration = await CompleteAsync(descriptor.ReadValueAsync(BluetoothCacheMode.Uncached), token);
            if (configuration.Status != GattCommunicationStatus.Success || configuration.Value?.Length != 2 || !Connected) return null;
            using (var configurationReader = DataReader.FromBuffer(configuration.Value))
                if ((configurationReader.ReadByte() & 1) == 0) return null;
            var received = new TaskCompletionSource<BluetoothBatteryUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(GattCharacteristic sender, GattValueChangedEventArgs args)
            {
                try
                {
                    // Use this callback's local receipt time. No value is stored between rounds,
                    // and the handler is attached only after verifying the current CCCD.
                    var receivedAt = DateTimeOffset.UtcNow;
                    if (!Connected || args.CharacteristicValue?.Length != 1) return;
                    using var reader = DataReader.FromBuffer(args.CharacteristicValue);
                    byte value = reader.ReadByte();
                    if (value <= 100) received.TrySetResult(new(new[] { value }, receivedAt));
                }
                catch (Exception) { } // A callback already queued during disconnect/disposal is discarded.
            }
            characteristic.ValueChanged += Changed;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(6));
            try { return await received.Task.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
            finally { characteristic.ValueChanged -= Changed; }
        }
        public void Dispose() { foreach (var service in _services.Values) service?.Dispose(); _services.Clear(); }
    }
}
