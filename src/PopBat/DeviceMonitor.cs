using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PopBat;

internal sealed class DeviceMonitor
{
    private readonly DeltaHidReader _delta = new();
    private readonly LogitechHidReader _logitech = new();
    private readonly BluetoothGattReader _bluetooth = new();
    private readonly StandardHidReader _standardHid = new();
    private readonly LegacyKeyboardReader _keyboard = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IReadOnlyList<BatteryProvider> _providers;
    internal DeviceMonitor()
    {
        _providers = new[] {
            new BatteryProvider("bluetooth-battery-service", BatteryProviderKind.Standard, 0, async (context, token) => {
                var readings = await _bluetooth.ReadAsync(token, context.ReportProgress); context.DeferVendorReads |= _bluetooth.IsPending; return readings;
            }),
            new BatteryProvider("hid-battery-feature", BatteryProviderKind.Standard, 1, async (context, token) => {
                var batch = await _standardHid.ReadAsync(context.CoveredPhysicalKeys, token);
                context.HidInventory = batch.Inventory; context.DeferVendorReads |= _standardHid.IsPending || _keyboard.IsPending; return batch.Devices;
            }),
            new BatteryProvider("rog-delta-ii", BatteryProviderKind.VendorFallback, 0, async (context, token) => {
                var device = context.HidInventory.FirstOrDefault(d => d.Vendor == DeltaProtocol.VendorId && d.Product == DeltaProtocol.ProductId);
                if (device != null && context.CoveredPhysicalKeys.Contains(device.Group)) return Array.Empty<DeviceBattery>();
                var reading = await _delta.ReadAsync(token);
                return reading.State == DeviceState.ReceiverMissing ? Array.Empty<DeviceBattery>()
                    : new[] { new DeviceBattery("delta", "ROG Delta II", "2.4 GHz · USB 接收器", reading, PhysicalKey: device?.Group) };
            }),
            new BatteryProvider("logitech-hidpp", BatteryProviderKind.VendorFallback, 1, (context, token) => _logitech.ReadAsync(token, context.CoveredPhysicalKeys)),
            new BatteryProvider("legacy-keyboard-battery", BatteryProviderKind.VendorFallback, 2, (context, token) =>
                context.HidInventory.Any(d => d.Usb && d.Vendor == 0x05AC && d.Product == 0x024F && !context.CoveredPhysicalKeys.Contains(d.Group))
                    ? _keyboard.ReadAsync(token, context.CoveredPhysicalKeys) : Task.FromResult<IReadOnlyList<DeviceBattery>>(Array.Empty<DeviceBattery>()))
        };
    }
    internal async Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken token, bool retry = false,
        Action<IReadOnlyList<DeviceBattery>>? progress = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            return await Task.Run(async () => {
                if (retry) { _logitech.RetryNow(); _bluetooth.RetryNow(); _standardHid.RetryNow(); _keyboard.RetryNow(); }
                return await BatteryProviderPipeline.ReadAsync(_providers, token, progress);
            }, token);
        }
        finally { _gate.Release(); }
    }
}
