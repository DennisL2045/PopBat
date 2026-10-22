using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PopBat;

public sealed class DeltaHidReader
{
    private sealed record Candidate(string Path, ushort InputLength, ushort OutputLength);
    public DeltaReadDiagnostics Diagnostics { get; private set; } = new();
    public Task<BatteryReading> ReadAsync(CancellationToken token) => Task.Run(async () =>
    {
        Diagnostics = new() { Stage = "Discovery" };
        BatteryReading Finish(BatteryReading value) {
            Diagnostics.Outcome = value.State.ToString();
            if (value.State == DeviceState.Connected) { Diagnostics.Stage = "Complete"; Diagnostics.Win32Error = null; }
            return value;
        }
        try
        {
            var candidates = Discover(out int targetCount, out bool accessDenied);
            if (candidates.Count == 0) return Finish(BatteryReading.Empty(accessDenied ? DeviceState.AccessDenied : targetCount == 0 ? DeviceState.ReceiverMissing : DeviceState.UnsupportedInterface, targetCount));
            BatteryReading last = BatteryReading.Empty(DeviceState.NoReply, targetCount, candidates.Count);
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested(); Diagnostics.Stage = "OpeningControl";
                using var handle = Native.CreateFile(candidate.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error(); Diagnostics.Win32Error = error;
                    last = BatteryReading.Empty(error == 5 ? DeviceState.AccessDenied : DeviceState.ReadError, targetCount, candidates.Count); continue;
                }
                using var stream = new FileStream(handle, FileAccess.ReadWrite, 256, true);
                try
                {
                    Diagnostics.Stage = "ConnectionQuery";
                    var connection = await RequestAsync(stream, candidate, DeltaProtocol.ConnectionKey, token);
                    bool? present = connection == null ? null : DeltaProtocol.HeadsetPresent(connection);
                    if (present == false) { last = BatteryReading.Empty(DeviceState.HeadsetOffline, targetCount, candidates.Count); continue; }
                    if (present == null) { last = BatteryReading.Empty(DeviceState.NoReply, targetCount, candidates.Count); continue; }
                    Diagnostics.Stage = "BatteryQuery";
                    var battery = await RequestAsync(stream, candidate, DeltaProtocol.BatteryKey, token);
                    if (battery == null) { last = BatteryReading.Empty(DeviceState.NoReply, targetCount, candidates.Count); continue; }
                    byte[]? charging = null; Diagnostics.Stage = "ChargingQuery";
                    try { charging = await RequestAsync(stream, candidate, DeltaProtocol.ChargingKey, token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                    last = DeltaProtocol.Decode(battery, charging, targetCount, candidates.Count);
                    if (last.State == DeviceState.Connected) return Finish(last);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { last = BatteryReading.Empty(DeviceState.NoReply, targetCount, candidates.Count); }
                catch (IOException e) { SetIoError(e); last = BatteryReading.Empty(DeviceState.ReadError, targetCount, candidates.Count); }
            }
            return Finish(last);
        }
        catch (OperationCanceledException) { Diagnostics.Outcome = "Cancelled"; throw; }
        catch (UnauthorizedAccessException) { Diagnostics.Win32Error = 5; return Finish(BatteryReading.Empty(DeviceState.AccessDenied)); }
        catch (Exception e) when (e is IOException or Win32Exception or ArgumentException) {
            if (e is Win32Exception win32) Diagnostics.Win32Error = win32.NativeErrorCode;
            if (e is IOException io) SetIoError(io);
            return Finish(BatteryReading.Empty(DeviceState.ReadError));
        }
    }, token);
    private void SetIoError(IOException error) {
        if (((uint)error.HResult & 0xffff0000) == 0x80070000) Diagnostics.Win32Error = error.HResult & 0xffff;
    }
    private async Task<byte[]?> RequestAsync(FileStream stream, Candidate candidate, byte key, CancellationToken token)
    {
        var query = new DeltaQueryDiagnostic { GetKey = key }; Diagnostics.Queries.Add(query);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(1200));
        try {
            await stream.WriteAsync(DeltaProtocol.BuildGet(key, candidate.OutputLength), deadline.Token);
            query.Result = "WaitingForReply";
            var buffer = new byte[candidate.InputLength];
            for (int i = 0; i < 128; i++) {
                int length = await stream.ReadAsync(buffer, deadline.Token);
                if (length == 0) { query.Result = "NoData"; return null; }
                // Never retain or log unrelated packets, input events or payload bytes.
                if (DeltaProtocol.MatchesReply(buffer.AsSpan(0, length), key)) { query.Result = "Replied"; return buffer.AsSpan(0, length).ToArray(); }
            }
            query.Result = "NoMatchingReply"; return null;
        }
        catch (OperationCanceledException) { query.Result = token.IsCancellationRequested ? "Cancelled" : "TimedOut"; throw; }
        catch (IOException) { query.Result = "IoError"; throw; }
    }
    private List<Candidate> Discover(out int targetCount, out bool accessDenied)
    {
        var candidates = new List<Candidate>(); targetCount = 0; accessDenied = false;
        Native.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr list = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (list == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            for (uint index = 0; ; index++) {
                var info = new Native.InterfaceData { Size = Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(list, IntPtr.Zero, ref hidGuid, index, ref info)) {
                    int error = Marshal.GetLastWin32Error(); if (error == 259) break; throw new Win32Exception(error);
                }
                Native.SetupDiGetDeviceInterfaceDetail(list, ref info, IntPtr.Zero, 0, out uint bytes, IntPtr.Zero);
                if (bytes < 8 || bytes > 65536) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)bytes); string? path;
                try {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(list, ref info, detail, bytes, out _, IntPtr.Zero)) continue;
                    path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                } finally { Marshal.FreeHGlobal(detail); }
                // Filter before opening any device or reading identifying metadata.
                if (path == null || !DeltaProtocol.IsTargetPath(path)) continue;
                targetCount++;
                var entry = new DeltaInterfaceDiagnostic { Index = targetCount }; Diagnostics.Interfaces.Add(entry);
                using var metadata = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (metadata.IsInvalid) {
                    entry.Win32Error = Marshal.GetLastWin32Error(); entry.Result = "MetadataOpenFailed";
                    accessDenied |= entry.Win32Error == 5; Diagnostics.Win32Error = entry.Win32Error; continue;
                }
                var attributes = new Native.Attributes { Size = Marshal.SizeOf<Native.Attributes>() };
                if (!Native.HidD_GetAttributes(metadata, ref attributes) || attributes.VendorId != DeltaProtocol.VendorId || attributes.ProductId != DeltaProtocol.ProductId) {
                    entry.Result = "IdentityNotConfirmed"; continue;
                }
                if (!Native.HidD_GetPreparsedData(metadata, out IntPtr preparsed)) { entry.Result = "DescriptorUnavailable"; continue; }
                try {
                    if (Native.HidP_GetCaps(preparsed, out var caps) != 0x00110000) { entry.Result = "CapabilitiesUnavailable"; continue; }
                    entry.UsagePage = caps.UsagePage; entry.Usage = caps.Usage; entry.InputLength = caps.InputLength; entry.OutputLength = caps.OutputLength;
                    if (caps.UsagePage != DeltaProtocol.UsagePage || caps.Usage != 1) { entry.Result = "NotAllowedControlCollection"; continue; }
                    entry.OutputReportAllowed = HasReport(preparsed, caps.OutputValues, false) || HasReport(preparsed, caps.OutputButtons, true);
                    if (!DeltaProtocol.AllowsControlCollection(caps.UsagePage, caps.Usage, caps.InputLength, caps.OutputLength, entry.OutputReportAllowed)) {
                        entry.Result = entry.OutputReportAllowed ? "ReportLengthNotSupported" : "OutputReportMissing"; continue;
                    }
                    entry.Result = "Allowed";
                    if (candidates.Count < 4) candidates.Add(new(path, caps.InputLength, caps.OutputLength));
                } finally { Native.HidD_FreePreparsedData(preparsed); }
            }
        } finally { Native.SetupDiDestroyDeviceInfoList(list); }
        return candidates;
    }
    private static bool HasReport(IntPtr preparsed, ushort count, bool buttons)
    {
        if (count == 0 || count > 128) return false;
        ushort capacity = count; IntPtr buffer = Marshal.AllocHGlobal(count * 72);
        try {
            int result = buttons ? Native.HidP_GetButtonCaps(1, buffer, ref count, preparsed) : Native.HidP_GetValueCaps(1, buffer, ref count, preparsed);
            if (result != 0x00110000 || count > capacity) return false;
            for (int i = 0; i < count; i++) if (Marshal.ReadByte(buffer, i * 72 + 2) == DeltaProtocol.ReportId) return true;
            return false;
        } finally { Marshal.FreeHGlobal(buffer); }
    }
}
internal static class Native
{
    [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_Parent(out uint parent, uint child, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", ExactSpelling = true)] internal static extern uint CM_Get_DevNode_Property(uint node, ref PropertyKey key, out uint type, byte[] buffer, ref uint length, uint flags);
    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { public int Size; public Guid ClassGuid; public int Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct Attributes { public int Size; public ushort VendorId, ProductId, Version; }
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceInfo { public int Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] internal struct Caps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputData, OutputButtons, OutputValues, OutputData, FeatureButtons, FeatureValues, FeatureData;
    }
    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_GetAttributes(SafeFileHandle device, ref Attributes attributes);
    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_GetProductString(SafeFileHandle device, StringBuilder buffer, uint length);
    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_GetManufacturerString(SafeFileHandle device, StringBuilder buffer, uint length);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_GetFeature(SafeFileHandle device, [In, Out] byte[] buffer, int length);
    [DllImport("hid.dll")] internal static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr preparsed, byte[] report, int length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll")] internal static extern int HidP_GetValueCaps(int type, IntPtr caps, ref ushort count, IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetButtonCaps(int type, IntPtr caps, ref ushort count, IntPtr data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr list, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInfo(IntPtr list, uint index, ref DeviceInfo device);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr list, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetailWithInfo(IntPtr list, ref InterfaceData data, IntPtr detail, uint size, out uint required, ref DeviceInfo device);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceProperty(IntPtr list, ref DeviceInfo device, ref PropertyKey key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
