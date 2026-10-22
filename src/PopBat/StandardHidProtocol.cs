using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace PopBat;

[StructLayout(LayoutKind.Explicit, Size = 72)]
internal struct HidValueCapability
{
    [FieldOffset(0)] internal ushort Page;
    [FieldOffset(2)] internal byte ReportId;
    [FieldOffset(3)] internal byte Alias;
    [FieldOffset(4)] internal ushort BitField;
    [FieldOffset(6)] internal ushort Link;
    [FieldOffset(12)] internal byte IsRange;
    [FieldOffset(15)] internal byte Absolute;
    [FieldOffset(16)] internal byte HasNull;
    [FieldOffset(18)] internal ushort BitSize;
    [FieldOffset(20)] internal ushort Count;
    [FieldOffset(40)] internal int Minimum;
    [FieldOffset(44)] internal int Maximum;
    [FieldOffset(56)] internal ushort Usage;
    [FieldOffset(58)] internal ushort UsageMaximum;
}

// These usages describe battery capacity, not vendor commands. No input reports are acquired.
internal static class StandardHidProtocol
{
    internal static bool BatteryUsage(HidValueCapability value) => (value.Page, value.Usage) is (6, 0x20) or (0x85, 0x64)
        && (value.IsRange == 0 || value.UsageMaximum == value.Usage);
    internal static bool Valid(HidValueCapability value) => BatteryUsage(value) && value.Absolute != 0 && value.Alias == 0 && (value.BitField & 1) == 0
        && value.Count == 1 && value.BitSize is > 0 and <= 32 && value.Minimum >= 0 && value.Maximum > value.Minimum && value.Maximum <= 65535
        && (value.Page != 0x85 || (value.Minimum == 0 && value.Maximum == 100));
    internal static IReadOnlyList<HidValueCapability> SafeFeatures(IReadOnlyList<HidValueCapability> features, IReadOnlySet<byte>? buttonReports)
    {
        if (buttonReports == null) return Array.Empty<HidValueCapability>();
        return features.Where(Valid).GroupBy(v => v.ReportId)
            .Where(g => !buttonReports.Contains(g.Key) && features.Where(v => v.ReportId == g.Key).All(Valid))
            .Select(g => g.First()).Take(4).ToArray();
    }
    internal static int? Percent(HidValueCapability capability, uint value)
    {
        if (!Valid(capability) || value < capability.Minimum || value > capability.Maximum) return null;
        return (int)Math.Round(100.0 * (value - capability.Minimum) / (capability.Maximum - capability.Minimum), MidpointRounding.AwayFromZero);
    }
}
