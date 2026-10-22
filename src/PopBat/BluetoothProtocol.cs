using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PopBat;

internal interface IBluetoothChannel
{
    bool Connected { get; }
    string ConnectionName => "";
    Task<byte[]?> ReadAsync(ushort service, ushort characteristic, CancellationToken token);
    Task<BluetoothBatteryUpdate?> ObserveBatteryAsync(CancellationToken token) => Task.FromResult<BluetoothBatteryUpdate?>(null);
}

internal sealed record BluetoothBatteryUpdate(byte[] Payload, DateTimeOffset ReceivedAt);
internal sealed record BluetoothIdentity(string Name, string? Note = null);
internal sealed record BluetoothDeviceResult(BluetoothIdentity Identity, BatteryReading Reading);

// Standard Bluetooth SIG read-only characteristics; no HID input, serial number or write operation.
internal static class BluetoothProtocol
{
    internal static bool Allowed(ushort service, ushort characteristic) => (service, characteristic) is
        (0x1800, 0x2A00) or (0x180A, 0x2A29) or (0x180A, 0x2A24) or (0x180A, 0x2A50) or (0x180F, 0x2A19);
    internal static void Validate(ushort service, ushort characteristic)
    { if (!Allowed(service, characteristic)) throw new ArgumentOutOfRangeException(nameof(characteristic)); }
    internal static string Text(byte[]? data) => data is { Length: > 0 and <= 80 }
        ? LogitechProtocol.CleanName(data) : "";
    internal static string IdentityText(byte[]? data)
    {
        string text = Text(data);
        return text.ToUpperInvariant() is "MANUFACTURER NAME" or "MANUFACTURER" or "MODEL NUMBER" or "MODEL" or "DEVICE NAME"
            or "DEFAULT STRING" or "UNKNOWN" or "N/A" or "NOT AVAILABLE" ? "" : text;
    }
    internal static int? Percent(byte[]? data) => data is { Length: 1 } && data[0] <= 100 ? data[0] : null;
    // Field-confirmed READ mismatch for this firmware identity. Reject that source at ANY value;
    // never rescale a reading using the user's approximate reference percentage.
    internal static bool UnreliableBatterySource(string name) => name.Equals("M75PRO-5.0-BT1", StringComparison.OrdinalIgnoreCase);
    internal static int? NotifiedPercent(BluetoothBatteryUpdate? update, DateTimeOffset started, DateTimeOffset now) =>
        update != null && update.ReceivedAt >= started && update.ReceivedAt <= now ? Percent(update.Payload) : null;
    internal static async Task<BluetoothDeviceResult?> ReadAsync(IBluetoothChannel channel, BluetoothIdentity? known, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!channel.Connected) return null;
        var identity = known;
        if (identity == null)
        {
            string manufacturer = IdentityText(await channel.ReadAsync(0x180A, 0x2A29, token));
            string name = IdentityText(await channel.ReadAsync(0x1800, 0x2A00, token));
            if (!channel.Connected) return null;
            string model = IdentityText(await channel.ReadAsync(0x180A, 0x2A24, token));
            if (model.Length > 0) name = model;
            string? note = null;
            if (name.Length == 0)
            {
                name = IdentityText(Encoding.UTF8.GetBytes(channel.ConnectionName));
                if (name.Length == 0) name = "藍牙裝置（未提供名稱）";
                else note = "名稱來自目前連線名稱；韌體未回覆有效型號。電量仍直接向裝置讀取。";
            }
            if (manufacturer.Length > 0 && !name.Contains(manufacturer, StringComparison.OrdinalIgnoreCase)) name = manufacturer + " · " + name;
            identity = new(name, note);
        }
        token.ThrowIfCancellationRequested();
        if (!channel.Connected) return null;
        // Battery Level does not report charging. Never infer discharge or a runtime estimate.
        if (UnreliableBatterySource(identity.Name))
        {
            var started = DateTimeOffset.UtcNow;
            var update = await channel.ObserveBatteryAsync(token);
            token.ThrowIfCancellationRequested();
            if (!channel.Connected) return null;
            int? notified = NotifiedPercent(update, started, DateTimeOffset.UtcNow);
            if (notified != null)
                return new(identity with { Note = "電量直接來自裝置於本輪送出的標準藍牙電池更新；未採用不準的主動查詢欄位，也未讀取 Windows 電量。充電狀態未知。" },
                    new(DeviceState.Connected, notified, null, update!.ReceivedAt));
            return new(identity with { Note = "此韌體的主動電量查詢已確認不準；本輪未收到新的電池更新，電量保持未知，不沿用舊值或修改通知設定。" },
                BatteryReading.Empty(DeviceState.UnverifiedReading));
        }
        int? percent = Percent(await channel.ReadAsync(0x180F, 0x2A19, token));
        token.ThrowIfCancellationRequested();
        if (!channel.Connected) return null;
        return new(identity, new(percent != null ? DeviceState.Connected : DeviceState.UnsupportedInterface,
            percent, null, DateTimeOffset.UtcNow));
    }
}
