using System;
using System.Threading;
using System.Threading.Tasks;

namespace PopBat;

internal interface IFastPairStatusChannel
{
    bool Connected { get; }
    Task RequestActiveComponentsAsync(CancellationToken token);
    Task<int> ReceiveAsync(byte[] buffer, int offset, int count, CancellationToken token);
}

internal sealed record FastPairBattery(int? Percent, bool? Charging);
internal sealed record FastPairSnapshot(FastPairBattery Left, FastPairBattery Right, FastPairBattery Case,
    byte? ActiveComponents, DateTimeOffset ReceivedAt);

// Independent implementation of the public Google Fast Pair Message Stream wire format.
// References and attribution: docs/BLUETOOTH_BATTERY.md. No Google SDK or web API is used.
internal static class FastPairProtocol
{
    internal static readonly Guid ServiceUuid = new("df21fe2c-2515-4fdb-8886-f12c4d67927c");
    internal static byte[] ActiveComponentsRequest() => new byte[] { 0x03, 0x05, 0x00, 0x00 };

    private static FastPairBattery Battery(byte value)
    {
        int percent = value & 0x7f;
        return percent <= 100 ? new(percent, (value & 0x80) != 0) : new(null, null);
    }

    internal static async Task<FastPairSnapshot?> ReadAsync(IFastPairStatusChannel channel, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!channel.Connected) return null;
        FastPairSnapshot? battery = null;
        byte? active = null;
        byte[] header = new byte[4], body = new byte[512];
        // The documented active-components reply should arrive within one second.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(1200));
        try
        {
            await channel.RequestActiveComponentsAsync(deadline.Token);
            int total = 0;
            for (int frame = 0; frame < 16 && total < 2048; frame++)
            {
                if (!await ExactAsync(channel, header, 4, deadline.Token)) break;
                int length = (header[2] << 8) | header[3];
                if (length > body.Length || total + length + 4 > 2048) break;
                if (!await ExactAsync(channel, body, length, deadline.Token)) break;
                total += length + 4;
                if (header[0] == 3 && header[1] == 3 && length == 3)
                    battery = new(Battery(body[0]), Battery(body[1]), Battery(body[2]), null, DateTimeOffset.UtcNow);
                else if (header[0] == 3 && header[1] == 6 && length == 1)
                    active = body[0] <= 3 ? body[0] : null;
                // Ignore addresses, model IDs and every other payload; never retain/log them.
                Array.Clear(body);
                if (battery != null && active != null) break;
            }
        }
        catch (Exception) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        { /* A fresh component report may still be useful with explicitly unknown active state. */ }
        finally { Array.Clear(body); }
        token.ThrowIfCancellationRequested();
        return channel.Connected && battery != null ? battery with { ActiveComponents = active } : null;
    }

    private static async Task<bool> ExactAsync(IFastPairStatusChannel channel, byte[] buffer, int count, CancellationToken token)
    {
        int offset = 0;
        while (offset < count)
        {
            token.ThrowIfCancellationRequested();
            if (!channel.Connected) return false;
            int read = await channel.ReceiveAsync(buffer, offset, count - offset, token);
            if (read <= 0 || read > count - offset) return false;
            offset += read;
        }
        return true;
    }

    internal static DeviceBattery ToDevice(string key, string connectionName, string? physicalKey, FastPairSnapshot snapshot)
    {
        // Active mask bit 1 = left, bit 0 = right. A reported peer battery alone does NOT
        // establish that the peer is present. Never use an inactive peer or the case in the total.
        var chosen = snapshot.ActiveComponents switch {
            2 => snapshot.Left,
            1 => snapshot.Right,
            3 when snapshot.Left.Percent != null && snapshot.Right.Percent != null => new FastPairBattery(
                Math.Min(snapshot.Left.Percent.Value, snapshot.Right.Percent.Value),
                snapshot.Left.Charging == snapshot.Right.Charging ? snapshot.Left.Charging : null),
            _ => new FastPairBattery(null, null)
        };
        string Component(string name, FastPairBattery value, int bit) => snapshot.ActiveComponents is byte mask
            ? (mask & bit) == 0 ? name + "：未啟用" : name + "：" + Level(value)
            : name + "：" + Level(value) + "（啟用狀態未知）";
        string detail = Component("左耳", snapshot.Left, 2) + " · " + Component("右耳", snapshot.Right, 1)
            + " · 充電盒：" + Level(snapshot.Case) + "。";
        if (snapshot.ActiveComponents == 3) detail += " 總電量顯示兩耳較低值。";
        detail += " 名稱來自目前藍牙連線資訊；電量直接來自耳機。";
        string name = BluetoothProtocol.IdentityText(System.Text.Encoding.UTF8.GetBytes(connectionName));
        return new(key, name.Length == 0 ? "藍牙耳機" : name, "藍牙 · Fast Pair 通用電量",
            new(DeviceState.Connected, chosen.Percent, chosen.Charging, snapshot.ReceivedAt),
            chosen.Percent != null ? null : snapshot.ActiveComponents == 0 ? "耳機未啟用" : "電量未知", physicalKey, detail, AllowEstimate: false);
    }

    private static string Level(FastPairBattery value) => value.Percent is int percent
        ? percent + "%" + (value.Charging == true ? "（充電中）" : "") : "未知";
}
