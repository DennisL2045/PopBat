using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PopBat;

internal static class LogitechTests
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); count++; }
        void Reject(Action action, string name) { try { action(); } catch (ArgumentOutOfRangeException) { count++; return; } throw new InvalidOperationException("FAIL: " + name); }
        byte[] Build(LogitechQuery query) => LogitechProtocol.Build(query, 9, 0x73, 20, true);
        Check(LogitechProtocol.IsTargetPath(@"\\?\hid#vid_046d&pid_c547&mi_02&col02#fixture"), "Logitech any USB product");
        Check(LogitechProtocol.IsTargetPath(@"\\?\hid#vid&0002046d_pid&b034#fixture"), "Bluetooth vendor form");
        Check(!LogitechProtocol.IsTargetPath(@"\\?\hid#vid_046d0&pid_c547#fixture"), "partial vendor rejected");
        Check(!LogitechProtocol.IsTargetPath(@"\\?\hid#vid_0b05&pid_1afa#fixture"), "Logitech excludes ASUS");
        Check(!LogitechProtocol.IsControlCollection(1, 2) && !LogitechProtocol.IsControlCollection(1, 6), "no mouse or keyboard input collections");
        Check(LogitechProtocol.IsControlCollection(0xFF00, 2) && LogitechProtocol.IsControlCollection(0xFF43, 0x0202), "receiver and Bluetooth control collections");
        Reject(() => Build(new(7, LogitechOperation.Ping)), "bounded receiver slots");
        Reject(() => Build(new(1, LogitechOperation.Lookup, new(0x2201, 0))), "no DPI feature");
        Reject(() => Build(new(1, LogitechOperation.Lookup, new(0x1B04, 0))), "no key diversion feature");
        Reject(() => Build(new(1, LogitechOperation.Lookup, new(0x00D0, 0))), "no firmware feature");
        Reject(() => Build(new(1, LogitechOperation.BatteryStatus, new(0x2201, 5))), "feature type checked before sending");
        Reject(() => Build(new(1, LogitechOperation.NameChunk, new(5, 2), 80)), "bounded name fragments");
        Reject(() => Build(new(1, (LogitechOperation)255)), "no arbitrary operations");
        Reject(() => Build(new(0xFF, LogitechOperation.ReceiverName, Parameter: 7)), "no serial or unknown receiver register");
        var query = Build(new(1, LogitechOperation.UnifiedBattery, new(0x1004, 6)));
        Check(query[0] == 0x11 && query[1] == 1 && query[2] == 6 && query[3] == 0x19 && Array.TrueForAll(query[4..], b => b == 0), "GET unified battery frame");
        byte[] reply = (byte[])query.Clone(); reply[4] = 75; reply[5] = 4; reply[6] = 0;
        Check(LogitechProtocol.Match(reply, query, LogitechOperation.UnifiedBattery)?.Data?[0] == 75, "correlated long reply");
        reply[1] = 2; Check(LogitechProtocol.Match(reply, query, LogitechOperation.UnifiedBattery) == null, "other device reply discarded");
        reply[1] = 1; reply[3] = 0x10; Check(LogitechProtocol.Match(reply, query, LogitechOperation.UnifiedBattery) == null, "notifications discarded");
        reply[3] = 0x18; Check(LogitechProtocol.Match(reply, query, LogitechOperation.UnifiedBattery) == null, "other client reply discarded");
        Check(LogitechProtocol.Match(query.AsSpan(0, 7), query, LogitechOperation.UnifiedBattery) == null, "truncated long reply discarded");
        var ping = Build(new(1, LogitechOperation.Ping));
        var shortError = new byte[] { 0x10, 1, 0x8F, 0, 0x19, 1, 0 };
        Check(LogitechProtocol.Match(shortError, ping, LogitechOperation.Ping)?.Error == 1, "HID++1 protocol fallback");
        byte[] pingReply = (byte[])ping.Clone(); pingReply[4] = 2; pingReply[5] = 1;
        Check(LogitechProtocol.Match(pingReply, ping, LogitechOperation.Ping)?.Data?[0] == 2, "ping nonce matched");
        pingReply[6] ^= 1; Check(LogitechProtocol.Match(pingReply, ping, LogitechOperation.Ping) == null, "old ping discarded");
        Check(LogitechProtocol.Decode(LogitechOperation.UnifiedBattery, new byte[] { 75, 4, 1, 0 }) is { Percent: 75, Charging: true }, "unified percentage charging");
        Check(LogitechProtocol.Decode(LogitechOperation.UnifiedBattery, new byte[] { 0, 2, 0, 0 }) is { Percent: null, Level: "偏低" }, "coarse level not fabricated percentage");
        Check(LogitechProtocol.Decode(LogitechOperation.BatteryStatus, new byte[] { 0, 0, 0 })?.Percent == null, "legacy zero may be unavailable");
        Check(LogitechProtocol.Decode(LogitechOperation.BatteryStatus, new byte[] { 101, 0, 0 }) == null, "bad battery percentage discarded");
        Check(LogitechProtocol.Decode(LogitechOperation.BatteryStatus, new byte[] { 80, 0, 0xFF })?.Charging == null, "unknown charge state retained");
        Check(LogitechProtocol.Decode(LogitechOperation.LegacyCharge, new byte[] { 45, 0, 0x50 }) is { Percent: 45, Charging: true }, "HID++1 percentage register");
        Check(LogitechProtocol.Decode(LogitechOperation.LegacyStatus, new byte[] { 5, 0, 0 }) is { Percent: null, Level: "良好", Charging: false }, "HID++1 coarse register");
        var voltage = LogitechProtocol.Decode(LogitechOperation.BatteryVoltage, new byte[] { 0x0F, 0xA0, 0 });
        Check(voltage?.Percent == null && voltage?.Level == $"{4.0:0.00} V（未提供百分比）", "voltage not fabricated percentage");
        Check(LogitechProtocol.Decode(LogitechOperation.UnifiedBattery, new byte[] { 40 }) == null, "truncated battery payload");
        Check(LogitechProtocol.CleanName(Encoding.UTF8.GetBytes("Mouse\n\0\r")) == "Mouse", "name strips controls");
        Check(LogitechProtocol.ReceiverName(new byte[] { 0x40, 5, 77, 111, 117, 115, 101 }, false) == "Mouse", "receiver name register");
        Check(LogitechProtocol.ReceiverName(new byte[] { 0x61, 1, 5, 77, 111, 117, 115, 101 }, true) == "Mouse", "Bolt name register");
        Check(LogitechProtocol.ReceiverName(new byte[] { 0x40, 80, 77 }, false) == null, "truncated name rejected");
        var client = new LogitechDeviceClient(); var channel = new FakeChannel();
        var battery = client.ReadAsync(channel, 1, CancellationToken.None).GetAwaiter().GetResult();
        Check(battery?.Percent == 72 && client.Name == "Fixture Wireless Mouse", "automatic name and battery discovery");
        int firstCalls = channel.Calls;
        battery = client.ReadAsync(channel, 1, CancellationToken.None).GetAwaiter().GetResult();
        Check(channel.Calls == firstCalls + 1 && battery?.Percent == 72, "steady polling uses one cached battery GET");
        channel.NameLength = 255; client.Reset();
        client.ReadAsync(channel, 2, CancellationToken.None).GetAwaiter().GetResult();
        Check(client.Name == null && channel.LastSlot == 2, "oversized name bounded and slots isolated");
        var fallback = new LogitechDeviceClient();
        Check(fallback.ReadAsync(new FakeChannel { Feature = 0x1000 }, 3, CancellationToken.None).GetAwaiter().GetResult()?.Percent == 72, "unified absent uses battery status");
        Check(new LogitechDeviceClient().ReadAsync(new FakeChannel { Feature = 0x1001 }, 4, CancellationToken.None).GetAwaiter().GetResult()?.Percent == null, "voltage-only device supported");
        Check(new LogitechDeviceClient().ReadAsync(new FakeChannel { Legacy = true }, 5, CancellationToken.None).GetAwaiter().GetResult()?.Percent == 45, "HID++1 end-to-end fallback");
        Check(new LogitechDeviceClient().ReadAsync(new FakeChannel { Offline = true }, 6, CancellationToken.None).GetAwaiter().GetResult() == null, "offline slot has no fabricated data");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        bool cancelled = false;
        try { new LogitechDeviceClient().ReadAsync(channel, 1, cancel.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "pause/exit cancellation propagated");
        Check(BluetoothProtocol.Percent(new byte[] { 0 }) == 0 && BluetoothProtocol.Percent(new byte[] { 100 }) == 100, "GATT battery endpoints valid");
        Check(BluetoothProtocol.Percent(new byte[] { 101 }) == null && BluetoothProtocol.Percent(new byte[] { 95, 0 }) == null, "malformed battery rejected");
        Check(BluetoothProtocol.Percent(null) == null, "no OS battery fallback");
        Reject(() => BluetoothProtocol.Validate(0x1812, 0x2A4D), "no HID input report GATT access");
        Reject(() => BluetoothProtocol.Validate(0x180A, 0x2A25), "no serial GATT access");
        Reject(() => BluetoothProtocol.Validate(0x180F, 0x2902), "no notification configuration access");
        Check(BluetoothProtocol.Text(new byte[81]) == "", "GATT names bounded");
        var direct = new FakeBluetooth();
        var directResult = BluetoothProtocol.ReadAsync(direct, null, CancellationToken.None).GetAwaiter().GetResult();
        Check(directResult?.Identity.Name == "Logitech Inc. · Fixture Current Mouse" && directResult.Reading.Percent == 61, "model and fresh battery from device not old OS name");
        Check(directResult?.Reading.Charging == null, "standard GATT charge state unknown");
        direct.Calls = 0;
        BluetoothProtocol.ReadAsync(direct, directResult!.Identity, CancellationToken.None).GetAwaiter().GetResult();
        Check(direct.Calls == 1, "verified identity cache retains only fresh battery GET");
        var offlineBluetooth = new FakeBluetooth { Connected = false };
        Check(BluetoothProtocol.ReadAsync(offlineBluetooth, null, CancellationToken.None).GetAwaiter().GetResult() == null && offlineBluetooth.Calls == 0, "disconnected old paired mouse never queried or displayed");
        var otherBluetooth = new FakeBluetooth { Manufacturer = "Other Inc." };
        Check(BluetoothProtocol.ReadAsync(otherBluetooth, null, CancellationToken.None).GetAwaiter().GetResult()?.Reading.Percent == 61 && otherBluetooth.BatteryCalls == 1, "generic GATT reads other brands without a new protocol");
        Check(BluetoothProtocol.ReadAsync(new FakeBluetooth { DropOnBattery = true }, null, CancellationToken.None).GetAwaiter().GetResult() == null, "disconnect during direct read discards value");
        Check(BluetoothProtocol.ReadAsync(new FakeBluetooth { Battery = null }, null, CancellationToken.None).GetAwaiter().GetResult()?.Reading is { State: DeviceState.UnsupportedInterface, Percent: null }, "missing Battery Service never fabricates Windows percentage");
        bool bleCancelled = false;
        try { BluetoothProtocol.ReadAsync(new FakeBluetooth(), null, cancel.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { bleCancelled = true; }
        Check(bleCancelled, "Bluetooth cancellation before any operation");
        return count;
    }
    private sealed class FakeBluetooth : IBluetoothChannel
    {
        public bool Connected { get; set; } = true;
        internal int Calls, BatteryCalls;
        internal bool DropOnBattery;
        internal string Manufacturer = "Logitech Inc.";
        internal byte[]? Battery = new byte[] { 61 };
        public Task<byte[]?> ReadAsync(ushort service, ushort characteristic, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); BluetoothProtocol.Validate(service, characteristic); Calls++;
            if (characteristic == 0x2A19) { BatteryCalls++; if (DropOnBattery) Connected = false; }
            byte[]? result = characteristic switch {
                0x2A29 => Encoding.UTF8.GetBytes(Manufacturer), 0x2A24 => Encoding.UTF8.GetBytes("Fixture Current Mouse"),
                0x2A00 => Encoding.UTF8.GetBytes("Custom Direct Name"), 0x2A19 => Battery, _ => null };
            return Task.FromResult(result);
        }
    }
    private sealed class FakeChannel : ILogitechChannel
    {
        internal int Calls, LastSlot, NameLength = 22;
        internal ushort Feature = 0x1004;
        internal bool Legacy, Offline;
        public Task<LogitechReply> QueryAsync(LogitechQuery q, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; LastSlot = q.Device;
            _ = LogitechProtocol.Build(q, 9, 0x73, 20, true);
            if (Offline) return Task.FromResult(LogitechReply.Missing);
            byte[] name = Encoding.UTF8.GetBytes("Fixture Wireless Mouse");
            LogitechReply reply = q.Operation switch
            {
                LogitechOperation.Ping => Legacy ? new(null, 1) : new(new byte[] { 2, 0, 0x73 }),
                LogitechOperation.Lookup => new(new byte[] { q.Feature.Id == 5 ? (byte)2 : q.Feature.Id == Feature ? (byte)6 : (byte)0, 0, 0 }),
                LogitechOperation.NameLength => new(new[] { (byte)NameLength }),
                LogitechOperation.NameChunk => new(name.AsSpan(q.Parameter, Math.Min(16, name.Length - q.Parameter)).ToArray()),
                LogitechOperation.UnifiedBattery => new(new byte[] { 72, 4, 0, 0 }),
                LogitechOperation.BatteryStatus => new(new byte[] { 72, 70, 0 }),
                LogitechOperation.BatteryVoltage => new(new byte[] { 0x0F, 0xA0, 0 }),
                LogitechOperation.LegacyCharge => new(new byte[] { 45, 0, 0x30 }),
                _ => throw new InvalidOperationException("Unexpected fixture query")
            };
            return Task.FromResult(reply);
        }
    }
}
