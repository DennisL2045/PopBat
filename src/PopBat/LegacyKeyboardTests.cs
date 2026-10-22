using System;
using System.Linq;

namespace PopBat;

internal static class LegacyKeyboardTests
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool value, string text) { if (!value) throw new InvalidOperationException("FAIL: " + text); count++; }
        var query = LegacyKeyboardProtocol.Build(33);
        Check(query.Length == 33 && query[0] == 0 && query[1] == 0x20 && query[2] == 1 && query[32] == 0x21, "fixed manufacturer battery GET");
        Check(query.Skip(3).Take(29).All(b => b == 0), "no setting data in battery query");
        Check(LegacyKeyboardProtocol.Build(65).Skip(33).All(b => b == 0), "long collection only zero padding");
        bool rejected = false; try { LegacyKeyboardProtocol.Build(64); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "unsupported report length rejected");
        var reply = (byte[])query.Clone(); reply[4] = 37;
        Check(LegacyKeyboardProtocol.Decode(reply) is { State: DeviceState.Connected, Percent: 37, Charging: null }, "percentage comes solely from receiver payload");
        reply[4] = 0;
        Check(LegacyKeyboardProtocol.Decode(reply) is { State: DeviceState.NotReady, Percent: null }, "legacy zero not flat battery");
        reply[4] = 101; Check(LegacyKeyboardProtocol.Decode(reply) == null, "legacy percentage range enforced");
        reply[4] = 100; Check(LegacyKeyboardProtocol.Decode(reply)?.Percent == 100, "legacy full battery valid");
        reply[2] = 2; Check(LegacyKeyboardProtocol.Decode(reply) == null, "other subcommand discarded");
        reply[2] = 1; reply[1] = 0x21; Check(LegacyKeyboardProtocol.Decode(reply) == null, "other command discarded");
        Check(LegacyKeyboardProtocol.Decode(query.AsSpan(0, 5)) == null, "partial legacy report rejected");
        var endpoint = new StandardHidReader.Endpoint(@"\\?\hid#vid_05ac&pid_024f&mi_03#fixture", "fixture", "2.4G Dongle", "", 0x05AC, 0x024F,
            true, false, 0, Array.Empty<HidValueCapability>(), true, 0xFF60, 0x61, 33, 33, new byte[] { 0 }, new byte[] { 0 });
        Check(LegacyKeyboardProtocol.IsTarget(endpoint), "confirmed manufacturer control profile accepted");
        Check(!LegacyKeyboardProtocol.IsTarget(endpoint with { Name = "Apple Keyboard" }), "shared VID PID insufficient to authorize query");
        Check(!LegacyKeyboardProtocol.IsTarget(endpoint with { Path = @"\\?\hid#vid_05ac&pid_024f&mi_00#fixture" }), "other interface rejected");
        Check(!LegacyKeyboardProtocol.IsTarget(endpoint with { Page = 1, Usage = 6 }), "keyboard input collection rejected");
        Check(!LegacyKeyboardProtocol.IsTarget(endpoint with { Usb = false }), "Bluetooth HID node rejected");
        Check(!LegacyKeyboardProtocol.IsTarget(endpoint with { OutputReports = new byte[] { 1 } }), "other report ID rejected");
        Check(!LegacyKeyboardProtocol.IsTarget(endpoint with { OutputLength = 65 }), "mismatched collection shape rejected");
        return count;
    }
}
