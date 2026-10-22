using System;

namespace PopBat;

// Fixed read-only battery query from the manufacturer M75 1.0.0.0 driver.
// No arbitrary command argument, configuration, initialization or mode change.
internal static class LegacyKeyboardProtocol
{
    internal static byte[] Build(int reportLength)
    {
        if (reportLength is not (33 or 65)) throw new ArgumentOutOfRangeException(nameof(reportLength));
        var bytes = new byte[reportLength];
        bytes[1] = 0x20; bytes[2] = 1; bytes[32] = 0x21;
        return bytes;
    }
    internal static bool Matches(ReadOnlySpan<byte> bytes) => bytes.Length is 33 or 65
        && bytes[0] == 0 && bytes[1] == 0x20 && bytes[2] == 1 && bytes[3] == 0;
    internal static BatteryReading? Decode(ReadOnlySpan<byte> bytes)
    {
        if (!Matches(bytes) || bytes[4] > 100) return null;
        // Manufacturer software discards zero; a sleeping receiver may return zero.
        return bytes[4] == 0 ? BatteryReading.Empty(DeviceState.NotReady)
            : new(DeviceState.Connected, bytes[4], null, DateTimeOffset.UtcNow);
    }
    internal static bool IsTarget(StandardHidReader.Endpoint device) => device.Usb && device.Vendor == 0x05AC && device.Product == 0x024F
        && device.Name.Equals("2.4G Dongle", StringComparison.OrdinalIgnoreCase)
        && device.Path.Contains("&mi_03", StringComparison.OrdinalIgnoreCase)
        && device.Page == 0xFF60 && device.Usage == 0x61 && device.InputLength is 33 or 65 && device.OutputLength == device.InputLength
        && device.InputReports is { Length: 1 } && device.InputReports[0] == 0
        && device.OutputReports is { Length: 1 } && device.OutputReports[0] == 0;
}
