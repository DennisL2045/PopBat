using System;
using System.Linq;
using System.Text;

namespace PopBat;

internal enum LogitechOperation { Ping, Lookup, NameLength, NameChunk, UnifiedBattery, BatteryStatus, BatteryVoltage, LegacyCharge, LegacyStatus, ReceiverName }
internal readonly record struct LogitechFeature(ushort Id, byte Index);
internal sealed record LogitechQuery(byte Device, LogitechOperation Operation, LogitechFeature Feature = default, byte Parameter = 0, bool Bolt = false);
internal sealed record LogitechReply(byte[]? Data, byte? Error = null)
{
    internal static readonly LogitechReply Missing = new((byte[]?)null);
}
internal sealed record LogitechBattery(int? Percent, bool? Charging, string? Level = null);

// Independent implementation of published HID++ wire formats. No arbitrary command entry point.
internal static class LogitechProtocol
{
    internal const ushort VendorId = 0x046D;
    internal const ushort NameFeature = 0x0005, StatusFeature = 0x1000, VoltageFeature = 0x1001, UnifiedFeature = 0x1004;
    internal const int MaxNameBytes = 80;
    internal static bool IsTargetPath(string path)
    {
        if (!path.StartsWith(@"\\?\hid#", StringComparison.OrdinalIgnoreCase)) return false;
        // USB and Windows Bluetooth Device ID forms; final HID attributes must also match VID.
        return path.Contains("vid_046d&", StringComparison.OrdinalIgnoreCase)
            || path.Contains("vid_046d#", StringComparison.OrdinalIgnoreCase)
            || path.Contains("vid&0002046d_pid&", StringComparison.OrdinalIgnoreCase)
            || path.Contains("vid&0001046d_pid&", StringComparison.OrdinalIgnoreCase);
    }
    internal static bool IsAllowedFeature(ushort id) => id is NameFeature or StatusFeature or VoltageFeature or UnifiedFeature;
    internal static bool IsControlCollection(ushort page, ushort usage) => page == 0xFF00 && usage is 1 or 2 || page == 0xFF43 && usage == 0x0202;

    internal static byte[] Build(LogitechQuery q, byte softwareId, byte pingMark, int length, bool longReport)
    {
        if (q.Device != 0xFF && (q.Device < 1 || q.Device > 6)) throw new ArgumentOutOfRangeException(nameof(q.Device));
        if (softwareId < 1 || softwareId > 15 || length < (longReport ? 20 : 7) || length > 64) throw new ArgumentOutOfRangeException(nameof(length));
        var result = new byte[length]; result[0] = longReport ? (byte)0x11 : (byte)0x10; result[1] = q.Device;
        switch (q.Operation)
        {
            case LogitechOperation.Ping: result[3] = (byte)(0x10 | softwareId); result[6] = pingMark; break;
            case LogitechOperation.Lookup:
                if (!IsAllowedFeature(q.Feature.Id)) throw new ArgumentOutOfRangeException(nameof(q.Feature));
                result[3] = softwareId; result[4] = (byte)(q.Feature.Id >> 8); result[5] = (byte)q.Feature.Id; break;
            case LogitechOperation.NameLength:
            case LogitechOperation.NameChunk:
            case LogitechOperation.UnifiedBattery:
            case LogitechOperation.BatteryStatus:
            case LogitechOperation.BatteryVoltage:
                ushort required = q.Operation switch { LogitechOperation.NameLength or LogitechOperation.NameChunk => NameFeature,
                    LogitechOperation.UnifiedBattery => UnifiedFeature, LogitechOperation.BatteryStatus => StatusFeature, _ => VoltageFeature };
                if (q.Feature.Id != required || q.Feature.Index == 0) throw new ArgumentOutOfRangeException(nameof(q.Feature));
                if (q.Operation == LogitechOperation.NameChunk && q.Parameter >= MaxNameBytes) throw new ArgumentOutOfRangeException(nameof(q.Parameter));
                result[2] = q.Feature.Index;
                result[3] = (byte)(softwareId | (q.Operation is LogitechOperation.NameChunk or LogitechOperation.UnifiedBattery ? 0x10 : 0));
                if (q.Operation == LogitechOperation.NameChunk) result[4] = q.Parameter;
                break;
            case LogitechOperation.LegacyCharge: result[2] = 0x81; result[3] = 0x0D; break;
            case LogitechOperation.LegacyStatus: result[2] = 0x81; result[3] = 0x07; break;
            case LogitechOperation.ReceiverName:
                if (q.Device != 0xFF || q.Parameter < 1 || q.Parameter > 6) throw new ArgumentOutOfRangeException(nameof(q.Parameter));
                result[2] = 0x83; result[3] = 0xB5;
                result[4] = q.Bolt ? (byte)(0x60 + q.Parameter) : (byte)(0x40 + q.Parameter - 1);
                if (q.Bolt) result[5] = 1;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(q.Operation));
        }
        return result;
    }

    internal static LogitechReply? Match(ReadOnlySpan<byte> reply, ReadOnlySpan<byte> request, LogitechOperation operation)
    {
        int logicalLength = reply.Length > 0 ? reply[0] switch { 0x10 => 7, 0x11 => 20, _ => 0 } : 0;
        if (logicalLength == 0 || reply.Length < logicalLength || request.Length < 7) return null;
        if (reply[1] != request[1] && !(request[1] == 0xFF && reply[1] == 0)) return null;
        if ((reply[0] == 0x10 && reply[2] == 0x8F || reply[0] == 0x11 && reply[2] == 0xFF) && reply[3] == request[2] && reply[4] == request[3]) return new(null, reply[5]);
        if (reply[2] != request[2] || reply[3] != request[3]) return null;
        if (operation == LogitechOperation.Ping && reply[6] != request[6]) return null;
        if (operation == LogitechOperation.ReceiverName && reply[4] != request[4]) return null;
        return new(reply.Slice(4, logicalLength - 4).ToArray());
    }

    internal static bool? Charging(byte status) => status switch { 0 or 3 => false, 1 or 2 or 4 => true, _ => null };
    internal static LogitechBattery? Decode(LogitechOperation operation, ReadOnlySpan<byte> p)
    {
        if (operation is LogitechOperation.UnifiedBattery or LogitechOperation.BatteryStatus)
        {
            if (p.Length < (operation == LogitechOperation.UnifiedBattery ? 4 : 3) || p[0] > 100) return null;
            // Several devices use zero as "percentage unavailable" and return a coarse level instead.
            string? level = operation == LogitechOperation.UnifiedBattery && p[0] == 0
                ? p[1] switch { 1 => "極低", 2 => "偏低", 4 => "良好", 8 => "滿電", _ => "未提供百分比" } : null;
            return new(p[0] == 0 ? null : p[0], Charging(p[2]), level ?? (p[0] == 0 ? "未提供百分比" : null));
        }
        if (operation == LogitechOperation.BatteryVoltage)
        {
            if (p.Length < 3) return null;
            int millivolts = p[0] * 256 + p[1];
            if (millivolts < 1000 || millivolts > 10000) return null;
            bool? charging = (p[2] & 0x80) == 0 ? false : (p[2] & 3) switch { 0 => true, 1 or 2 => false, _ => null };
            return new(null, charging, $"{millivolts / 1000.0:0.00} V（未提供百分比）");
        }
        if (operation == LogitechOperation.LegacyCharge)
        {
            if (p.Length < 3 || p[0] > 100) return null;
            bool? charging = (p[2] & 0xF0) switch { 0x30 or 0x90 => false, 0x50 => true, _ => null };
            return new(p[0], charging);
        }
        if (operation == LogitechOperation.LegacyStatus)
        {
            if (p.Length < 3) return null;
            bool? charging = p[1] == 0 ? false : (p[1] & 0x21) == 0x21 ? true : (p[1] & 0x22) == 0x22 ? false : null;
            string level = p[0] switch { 1 => "極低", 3 => "偏低", 5 => "良好", 7 => "滿電", _ => "未知等級" };
            return new(null, charging, level);
        }
        return null;
    }
    internal static string CleanName(ReadOnlySpan<byte> bytes)
    {
        string name = Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, MaxNameBytes)]);
        return new string(name.Where(c => !char.IsControl(c) && c != '\uFFFD').ToArray()).Trim();
    }
    internal static string? ReceiverName(byte[]? data, bool bolt)
    {
        int lengthAt = bolt ? 2 : 1, textAt = lengthAt + 1;
        if (data == null || data.Length <= textAt || data[lengthAt] == 0 || data[lengthAt] > data.Length - textAt) return null;
        string name = CleanName(data.AsSpan(textAt, data[lengthAt])); return name.Length == 0 ? null : name;
    }
}
