using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PopBat;

public enum DeviceState { Connected, ReceiverMissing, HeadsetOffline, NotReady, NoReply, UnsupportedInterface, AccessDenied, ReadError, UnverifiedReading }
public sealed record BatteryReading(DeviceState State, int? Percent, bool? Charging, DateTimeOffset CheckedAt, int TargetInterfaces = 0, int VendorInterfaces = 0)
{
    public static BatteryReading Empty(DeviceState state, int targets = 0, int vendors = 0) => new(state, null, null, DateTimeOffset.UtcNow, targets, vendors);
}

// This is a GET-only protocol, not a general-purpose HID command console.
public static class DeltaProtocol
{
    public const ushort VendorId = 0x0B05, ProductId = 0x1AFA, UsagePage = 0xFF00;
    public const byte ReportId = 0xCC, GetCommand = 0x12;
    public const byte ConnectionKey = 0, BatteryKey = 7, ChargingKey = 8;

    public static bool AllowsControlCollection(ushort page, ushort usage, ushort input, ushort output, bool hasOutputReport) =>
        page == UsagePage && usage == 1 && input >= 7 && input <= 256 && output >= 7 && output <= 256 && hasOutputReport;

    public static bool IsTargetPath(string path)
    {
        if (!path.StartsWith(@"\\?\hid#", StringComparison.OrdinalIgnoreCase)) return false;
        const string id = "vid_0b05&pid_1afa";
        int at = path.IndexOf(id, StringComparison.OrdinalIgnoreCase);
        int end = at + id.Length;
        return at >= 0 && end < path.Length && (path[end] == '&' || path[end] == '#');
    }

    public static byte[] BuildGet(byte key, int reportLength)
    {
        if (key != ConnectionKey && key != BatteryKey && key != ChargingKey)
            throw new ArgumentOutOfRangeException(nameof(key), "Only three read-only queries are allowed.");
        if (reportLength < 7 || reportLength > 256) throw new ArgumentOutOfRangeException(nameof(reportLength));
        var report = new byte[reportLength];
        report[0] = ReportId; report[1] = GetCommand; report[2] = key;
        return report;
    }

    public static bool MatchesReply(ReadOnlySpan<byte> reply, byte key) => reply.Length >= 5
        && reply[0] == ReportId && reply[1] == GetCommand && reply[2] == key && reply[3] == 0 && reply[4] == 0;

    // Firmware bytes are examined only to distinguish the receiver from an awake headset.
    // They are never displayed, logged, or retained.
    public static bool? HeadsetPresent(ReadOnlySpan<byte> reply)
    {
        if (!MatchesReply(reply, ConnectionKey) || reply.Length < 13) return null;
        return reply[5] != 0 || reply[6] != 0 || reply[7] != 0 || reply[8] != 0;
    }

    public static BatteryReading Decode(byte[] battery, byte[]? charging, int targets = 1, int vendors = 1)
    {
        if (!MatchesReply(battery, BatteryKey) || battery.Length < 7 || battery[6] > 100)
            return BatteryReading.Empty(DeviceState.ReadError, targets, vendors);
        bool? chargingState = charging != null && MatchesReply(charging, ChargingKey) && charging.Length > 5 && charging[5] <= 1
            ? charging[5] == 1 : null;
        // A standby receiver can answer with zero. Do not claim a flat battery.
        if (battery[6] == 0 && chargingState != true)
            return BatteryReading.Empty(DeviceState.NotReady, targets, vendors);
        return new(DeviceState.Connected, battery[6], chargingState, DateTimeOffset.UtcNow, targets, vendors);
    }
}

public sealed record BatterySample(DateTimeOffset Time, int Percent, bool Charging, int Session);
public sealed record RuntimeEstimate(double LowHours, double HighHours);

public sealed class BatteryHistory
{
    private readonly string _file;
    private readonly List<BatterySample> _samples = new();
    private int _session;
    private DateTimeOffset? _lastContact;
    private bool _wasConnected;
    public bool SaveEnabled { get; set; } = true;
    public bool StorageAvailable { get; private set; } = true;
    public BatteryHistory(string directory, bool load = true)
    {
        _file = Path.Combine(directory, "battery-history.json");
        try
        {
            if (load && File.Exists(_file) && new FileInfo(_file).Length <= 1024 * 1024)
            {
                var saved = JsonSerializer.Deserialize<List<BatterySample>>(File.ReadAllText(_file));
                if (saved != null) _samples.AddRange(saved.Where(s => s != null && s.Session >= 0 && s.Session < int.MaxValue && s.Percent >= 0 && s.Percent <= 100 && s.Time <= DateTimeOffset.UtcNow && s.Time >= DateTimeOffset.UtcNow.AddDays(-7)).TakeLast(2048));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { StorageAvailable = false; }
        // Never calculate usage across app shutdown, charge, or radio disconnection.
        _session = _samples.Count == 0 ? 1 : _samples.Max(s => s.Session) + 1;
    }

    public void Add(BatteryReading reading)
    {
        if (reading.State != DeviceState.Connected || reading.Percent == null || reading.Charging == null)
        { if (_wasConnected) _session++; _wasConnected = false; return; }
        if (_lastContact != null && reading.CheckedAt - _lastContact > TimeSpan.FromMinutes(2)) _session++;
        var previous = _samples.LastOrDefault();
        if (previous != null && previous.Session == _session && (previous.Charging != reading.Charging.Value || reading.Percent.Value > previous.Percent)) _session++;
        _wasConnected = true; _lastContact = reading.CheckedAt;
        previous = _samples.LastOrDefault();
        if (previous != null && previous.Session == _session && previous.Percent == reading.Percent && previous.Charging == reading.Charging
            && reading.CheckedAt - previous.Time < TimeSpan.FromMinutes(5)) return;
        _samples.Add(new(reading.CheckedAt, reading.Percent.Value, reading.Charging.Value, _session));
        _samples.RemoveAll(s => s.Time < reading.CheckedAt.AddDays(-7));
        if (_samples.Count > 2048) _samples.RemoveRange(0, _samples.Count - 2048);
        Save();
    }

    public RuntimeEstimate? Estimate(BatteryReading reading)
    {
        if (reading.State != DeviceState.Connected || reading.Percent == null || reading.Charging != false) return null;
        var session = _samples.Where(s => s.Session == _session && !s.Charging).ToArray();
        if (session.Length < 3) return null;
        var start = session[0];
        double hours = (reading.CheckedAt - start.Time).TotalHours;
        int drop = start.Percent - reading.Percent.Value;
        if (hours < 1 || drop < 3) return null;
        double remaining = reading.Percent.Value / (drop / hours);
        if (remaining < 0.1 || remaining > 500) return null;
        return new(remaining * 0.75, remaining * 1.25);
    }

    public void Clear()
    {
        _samples.Clear(); _session++; _lastContact = null; _wasConnected = false;
        try { if (File.Exists(_file)) File.Delete(_file); StorageAvailable = true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { StorageAvailable = false; }
    }
    private void Save()
    {
        if (!SaveEnabled) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(_samples));
            File.Move(_file + ".tmp", _file, true); StorageAvailable = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { StorageAvailable = false; }
    }
}
