using System;
using System.IO;

namespace PopBat;

internal static class SelfTests
{
    public static int Run()
    {
        int count = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); count++; }
        void Reject(Action action, string name) { try { action(); } catch (ArgumentOutOfRangeException) { count++; return; } throw new InvalidOperationException("FAIL: " + name); }
        static byte[] Hex(string value) => Convert.FromHexString(value);
        try
        {
            Check(DeltaProtocol.IsTargetPath(@"\\?\hid#vid_0b05&pid_1afa&mi_03&col04#test"), "allow exact Delta II USB receiver");
            Check(!DeltaProtocol.IsTargetPath(@"\\?\hid#vid_046d&pid_c53f#test"), "exclude Logitech");
            Check(!DeltaProtocol.IsTargetPath(@"\\?\hid#vid_0b05&pid_1afb#test"), "exclude other ASUS devices");
            Check(!DeltaProtocol.IsTargetPath(@"\\?\hid#vid_0b05&pid_1afa0#test"), "reject partial product ID match");
            Check(!DeltaProtocol.IsTargetPath(@"\\?\bthenum#vid_0b05&pid_1afa#test"), "exclude Bluetooth");
            foreach (byte key in new byte[] { 0, 7, 8 })
            {
                byte[] query = DeltaProtocol.BuildGet(key, 64);
                Check(query.Length == 64 && query[0] == 0xCC && query[1] == 0x12 && query[2] == key && Array.TrueForAll(query[3..], b => b == 0), "read-only query whitelist");
            }
            Reject(() => DeltaProtocol.BuildGet(40, 64), "no lighting command");
            Reject(() => DeltaProtocol.BuildGet(255, 64), "no command sweep");
            Reject(() => DeltaProtocol.BuildGet(7, 6), "reject short report");
            Reject(() => DeltaProtocol.BuildGet(7, 257), "bounded report length");
            var valid = Hex("CC120700000A4B14010000000000");
            var charging = Hex("CC12080000010000000000000000");
            var offCharge = Hex("CC12080000000000000000000000");
            Check(DeltaProtocol.Decode(valid, charging) is { State: DeviceState.Connected, Percent: 75, Charging: true }, "battery byte 6, charging byte 5");
            Check(DeltaProtocol.Decode(valid, offCharge).Charging == false, "confirmed discharging");
            Check(DeltaProtocol.Decode(valid, null) is { Percent: 75, Charging: null }, "missing charge state stays unknown");
            Check(DeltaProtocol.Decode(Hex("CC12070000FFAA000000"), null).Percent == null, "reject firmware error");
            Check(DeltaProtocol.Decode(Hex("CC12070000"), charging).Percent == null, "reject truncated battery reply");
            Check(DeltaProtocol.Decode(Hex("CC1207000000000000"), offCharge).State == DeviceState.NotReady, "standby is not zero battery");
            Check(DeltaProtocol.Decode(Hex("CC1207000000000000"), charging).Percent == 0, "zero is valid while charging");
            Check(!DeltaProtocol.MatchesReply(Hex("CC510700000A4B"), 7), "reject SET replies");
            Check(!DeltaProtocol.MatchesReply(Hex("07120700000A4B"), 7), "reject other collections");
            Check(!DeltaProtocol.MatchesReply(Hex("CC120800000A4B"), 7), "reject other keys");
            Check(!DeltaProtocol.MatchesReply(Hex("CC120700FF0A4B"), 7), "reject error header");
            Check(DeltaProtocol.HeadsetPresent(Hex("CC120000000009040000090400")) == true, "connected headset");
            Check(DeltaProtocol.HeadsetPresent(Hex("CC120000000000000000090400")) == false, "dongle alone");
            Check(DeltaProtocol.HeadsetPresent(Hex("CC12000000")) == null, "short connection reply is unknown");
            var history = new BatteryHistory(Path.Combine(Path.GetTempPath(), "PopBat-selftest-" + Guid.NewGuid())) { SaveEnabled = false };
            DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-90);
            BatteryReading current = new(DeviceState.Connected, 80, false, start);
            for (int tick = 0; tick <= 180; tick++)
            {
                current = new(DeviceState.Connected, 80 - tick / 45, false, start.AddSeconds(tick * 30)); history.Add(current);
            }
            Check(history.Estimate(current) is { LowHours: > 20 and < 22, HighHours: > 35 and < 37 }, "estimate from continuous real-use intervals");
            Check(history.Estimate(current with { Charging = true }) == null, "no drain estimate while charging");
            Check(history.Estimate(current with { Charging = null }) == null, "no estimate with unknown charging");
            history.Add(BatteryReading.Empty(DeviceState.HeadsetOffline));
            history.Add(current with { CheckedAt = current.CheckedAt.AddSeconds(30) });
            Check(history.Estimate(current) == null, "disconnect breaks usage segment");
            history.Clear(); Check(history.Estimate(current) == null, "clear resets estimate");
            Check(System.Runtime.InteropServices.Marshal.SizeOf<Native.Caps>() == 64, "Windows HID caps struct layout");
            count += LogitechTests.Run();
            count += StandardBatteryTests.Run();
            count += FastPairTests.Run();
            count += LegacyKeyboardTests.Run();
            Console.WriteLine($"PASS: {count} checks; no hardware was opened and no test history was written.");
            return 0;
        }
        catch (Exception e) { Console.WriteLine(e.Message); return 1; }
    }
}
