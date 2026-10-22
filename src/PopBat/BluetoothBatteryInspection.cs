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

internal sealed record BatteryInstanceInspection(int Service, int Characteristic, int? FirstPercent, int? SecondPercent, string Status);
internal sealed record BluetoothBatteryInspectionResult(string Name, int BatteryServices, bool MultipleOrTruncated,
    bool BatteryLevelStatusAvailable, IReadOnlyList<BatteryInstanceInspection> Instances);

// Explicit diagnostic only: two direct scalar battery reads per instance, no raw data/IDs logged.
internal static class BluetoothBatteryInspection
{
    private static Guid Uuid(ushort value) => new($"0000{value:x4}-0000-1000-8000-00805f9b34fb");
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
    internal static async Task<IReadOnlyList<BluetoothBatteryInspectionResult>> RunAsync(CancellationToken token)
    {
        var result = new List<BluetoothBatteryInspectionResult>();
        var candidates = await CompleteAsync(DeviceInformation.FindAllAsync(
            BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
            Array.Empty<string>(), DeviceInformationKind.AssociationEndpoint), token);
        foreach (var candidate in candidates.Where(d => d.Pairing.IsPaired).Take(4))
        {
            token.ThrowIfCancellationRequested();
            using var device = await CompleteAsync(BluetoothLEDevice.FromIdAsync(candidate.Id), token);
            if (device == null || device.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;
            var services = await CompleteAsync(device.GetGattServicesForUuidAsync(Uuid(0x180F), BluetoothCacheMode.Uncached), token);
            try
            {
                var instances = new List<BatteryInstanceInspection>();
                bool levelStatus = false, truncated = services.Services.Count > 4;
                int serviceIndex = 0;
                foreach (var service in services.Services.Take(4))
                {
                    token.ThrowIfCancellationRequested(); serviceIndex++;
                    var levels = await CompleteAsync(service.GetCharacteristicsForUuidAsync(Uuid(0x2A19), BluetoothCacheMode.Uncached), token);
                    var optionalStatus = await CompleteAsync(service.GetCharacteristicsForUuidAsync(Uuid(0x2BED), BluetoothCacheMode.Uncached), token);
                    levelStatus |= optionalStatus.Status == GattCommunicationStatus.Success && optionalStatus.Characteristics.Count > 0;
                    truncated |= levels.Characteristics.Count > 4;
                    int charIndex = 0;
                    foreach (var characteristic in levels.Characteristics.Take(4))
                    {
                        token.ThrowIfCancellationRequested(); charIndex++;
                        var first = await ReadPercentAsync(device, characteristic, token);
                        await Task.Delay(TimeSpan.FromSeconds(2), token);
                        var second = await ReadPercentAsync(device, characteristic, token);
                        instances.Add(new(serviceIndex, charIndex, first.Percent, second.Percent, first.Status + "/" + second.Status));
                    }
                }
                string name = BluetoothProtocol.IdentityText(System.Text.Encoding.UTF8.GetBytes(candidate.Name));
                result.Add(new(name.Length > 0 ? name : "已連線 BLE 裝置", services.Services.Count,
                    truncated || services.Services.Count > 1 || instances.Count > 1, levelStatus, instances));
            }
            finally { foreach (var service in services.Services) service.Dispose(); }
        }
        return result;
    }
    private static async Task<(int? Percent, string Status)> ReadPercentAsync(BluetoothLEDevice device, GattCharacteristic characteristic, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (device.ConnectionStatus != BluetoothConnectionStatus.Connected) return (null, "Disconnected");
        if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read)) return (null, "NotReadable");
        var read = await CompleteAsync(characteristic.ReadValueAsync(BluetoothCacheMode.Uncached), token);
        if (read.Status != GattCommunicationStatus.Success) return (null, read.Status.ToString());
        if (device.ConnectionStatus != BluetoothConnectionStatus.Connected) return (null, "Disconnected");
        if (read.Value == null || read.Value.Length != 1) return (null, "InvalidLength");
        using var reader = DataReader.FromBuffer(read.Value);
        int value = reader.ReadByte(); return value <= 100 ? (value, "Success") : (null, "OutOfRange");
    }
}
