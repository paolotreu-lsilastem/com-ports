using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

var result = new EnumerationResult(
    ComPortEnumerator.GetComPorts(),
    FtdiComPortEnumerator.GetFtdiComPorts()
        .OrderBy(item => PortNameComparer.ExtractComNumber(item.Com))
        .ThenBy(item => item.Com, StringComparer.OrdinalIgnoreCase)
        .ToArray());

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false
}));

internal sealed record EnumerationResult(string[] Coms, FtdiPortInfo[] Ftdis);

internal sealed record FtdiPortInfo(string Com, string Serial, string? Description);

internal static class PortNameComparer
{
    public static int ExtractComNumber(string? comPort)
    {
        if (string.IsNullOrWhiteSpace(comPort))
        {
            return int.MaxValue;
        }

        var digits = new string(comPort.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : int.MaxValue;
    }
}

internal static class ComPortEnumerator
{
    public static string[] GetComPorts()
    {
        return DeviceEnumerator.EnumeratePresentComPorts()
            .Select(static item => item.ComPort)
            .Where(static port => !string.IsNullOrWhiteSpace(port))
            .Select(static port => port.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(port => PortNameComparer.ExtractComNumber(port))
            .ThenBy(port => port, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal static class FtdiComPortEnumerator
{
    private const int CmDrpDevDesc = 0x00000001;
    private const int CmDrpFriendlyName = 0x0000000D;

    public static FtdiPortInfo[] GetFtdiComPorts()
    {
        var results = new Dictionary<string, FtdiPortInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var comDevice in DeviceEnumerator.EnumeratePresentComPorts())
        {
            if (!TryFindFtdiAncestor(comDevice.InstanceId, out var ftdi))
            {
                continue;
            }

            var parsed = ParseFtdiInstanceId(ftdi.InstanceId);
            if (string.IsNullOrWhiteSpace(parsed.SerialNumberCandidate))
            {
                continue;
            }

            var description = FirstNonEmpty(
                ftdi.Description,
                ftdi.FriendlyName,
                CleanComSuffix(comDevice.FriendlyName, comDevice.ComPort),
                comDevice.Description,
                comDevice.FriendlyName);

            results[comDevice.ComPort] = new FtdiPortInfo(
                comDevice.ComPort,
                parsed.SerialNumberCandidate,
                description);
        }

        return results.Values.ToArray();
    }

    private static bool TryFindFtdiAncestor(string deviceInstanceId, out FtdiAncestor ftdi)
    {
        ftdi = default;

        var configResult = DeviceEnumerator.CMLocateDevNode(out var currentDeviceInstance, deviceInstanceId, 0);
        if (configResult != DeviceEnumerator.CrSuccess)
        {
            return false;
        }

        while (true)
        {
            configResult = DeviceEnumerator.CMGetParent(out var parentDeviceInstance, currentDeviceInstance, 0);
            if (configResult != DeviceEnumerator.CrSuccess)
            {
                return false;
            }

            var parentInstanceId = GetDeviceInstanceIdFromDevInst(parentDeviceInstance);
            if (string.IsNullOrWhiteSpace(parentInstanceId))
            {
                return false;
            }

            if (IsFtdiInstanceId(parentInstanceId))
            {
                ftdi = new FtdiAncestor(
                    parentInstanceId,
                    GetDevicePropertyFromDevInst(parentDeviceInstance, CmDrpDevDesc),
                    GetDevicePropertyFromDevInst(parentDeviceInstance, CmDrpFriendlyName));
                return true;
            }

            currentDeviceInstance = parentDeviceInstance;
        }
    }

    private static string? GetDeviceInstanceIdFromDevInst(uint devInst)
    {
        var builder = new StringBuilder(512);
        var configResult = DeviceEnumerator.CMGetDeviceId(devInst, builder, builder.Capacity, 0);
        return configResult == DeviceEnumerator.CrSuccess ? builder.ToString() : null;
    }

    private static string? GetDevicePropertyFromDevInst(uint devInst, int property)
    {
        var requiredSize = 0u;
        _ = CMGetDevNodeRegistryProperty(
            devInst,
            property,
            out _,
            null,
            ref requiredSize,
            0);

        if (requiredSize == 0)
        {
            return null;
        }

        var buffer = new byte[requiredSize];
        var configResult = CMGetDevNodeRegistryProperty(
            devInst,
            property,
            out _,
            buffer,
            ref requiredSize,
            0);

        if (configResult != DeviceEnumerator.CrSuccess)
        {
            return null;
        }

        return TrimNullTerminator(Encoding.Unicode.GetString(buffer));
    }

    private static bool IsFtdiInstanceId(string instanceId)
    {
        return instanceId.StartsWith("FTDIBUS\\VID_0403+PID_", StringComparison.OrdinalIgnoreCase) ||
               instanceId.StartsWith("USB\\VID_0403&PID_", StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedFtdiId ParseFtdiInstanceId(string instanceId)
    {
        var parsed = new ParsedFtdiId("", "", "", "");
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return parsed;
        }

        var upper = instanceId.ToUpperInvariant();

        if (upper.StartsWith("FTDIBUS\\", StringComparison.Ordinal))
        {
            var tail = instanceId["FTDIBUS\\".Length..];
            var slash = tail.IndexOf('\\');
            if (slash >= 0)
            {
                tail = tail[..slash];
            }

            foreach (var part in tail.Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) && part.Length >= 8)
                {
                    parsed = parsed with { Vid = part[4..] };
                }
                else if (part.StartsWith("PID_", StringComparison.OrdinalIgnoreCase) && part.Length >= 8)
                {
                    parsed = parsed with { Pid = part[4..] };
                }
                else
                {
                    parsed = parsed with
                    {
                        RawSerialToken = part,
                        SerialNumberCandidate = NormalizeFtdiSerialToken(part)
                    };
                }
            }

            return parsed;
        }

        if (upper.StartsWith("USB\\", StringComparison.Ordinal))
        {
            var slashParts = instanceId["USB\\".Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (slashParts.Length > 0)
            {
                foreach (var ampPart in slashParts[0].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (ampPart.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) && ampPart.Length >= 8)
                    {
                        parsed = parsed with { Vid = ampPart[4..] };
                    }
                    else if (ampPart.StartsWith("PID_", StringComparison.OrdinalIgnoreCase) && ampPart.Length >= 8)
                    {
                        parsed = parsed with { Pid = ampPart[4..] };
                    }
                }
            }

            if (slashParts.Length > 1)
            {
                parsed = parsed with
                {
                    RawSerialToken = slashParts[1],
                    SerialNumberCandidate = slashParts[1]
                };
            }
        }

        return parsed;
    }

    private static string NormalizeFtdiSerialToken(string rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return rawToken;
        }

        var last = rawToken[^1];
        return last is 'A' or 'B' or 'C' or 'D'
            ? rawToken[..^1]
            : rawToken;
    }

    private static string? CleanComSuffix(string? value, string comPort)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var suffix = $"({comPort})";
        var trimmed = value.Trim();
        return trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^suffix.Length].TrimEnd()
            : trimmed;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string TrimNullTerminator(string value)
    {
        var nullIndex = value.IndexOf('\0');
        return nullIndex >= 0 ? value[..nullIndex] : value;
    }

    private readonly record struct ParsedFtdiId(
        string Vid,
        string Pid,
        string RawSerialToken,
        string SerialNumberCandidate);

    private readonly record struct FtdiAncestor(
        string InstanceId,
        string? Description,
        string? FriendlyName);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    private static extern int CMLocateDevNode(
        out uint deviceInstance,
        string deviceId,
        int flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static extern int CMGetParent(
        out uint parentDeviceInstance,
        uint deviceInstance,
        int flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    private static extern int CMGetDeviceId(
        uint deviceInstance,
        StringBuilder buffer,
        int bufferLen,
        int flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW", CharSet = CharSet.Unicode)]
    private static extern int CMGetDevNodeRegistryProperty(
        uint deviceInstance,
        int property,
        out uint propertyRegDataType,
        [Out] byte[]? propertyBuffer,
        ref uint propertyBufferSize,
        int flags);
}

internal static class DeviceEnumerator
{
    private const int DigcfPresent = 0x00000002;
    private const int DigcfDeviceInterface = 0x00000010;
    private const int ErrorNoMoreItems = 259;
    private const int SpdrpDevDesc = 0x00000000;
    private const int SpdrpFriendlyName = 0x0000000C;
    private const uint DicsFlagGlobal = 0x00000001;
    private const uint DiregDev = 0x00000001;
    private const uint KeyQueryValue = 0x0001;

    public const int CrSuccess = 0x00000000;

    public static IReadOnlyList<ComDeviceInfo> EnumeratePresentComPorts()
    {
        var results = new List<ComDeviceInfo>();
        var guidDevinterfaceComport = new Guid("86E0D1E0-8089-11D0-9CE4-08003E301F73");

        var deviceInfoSet = SetupDiGetClassDevs(
            ref guidDevinterfaceComport,
            IntPtr.Zero,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);

        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet.ToInt64() == -1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed.");
        }

        try
        {
            for (var index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    cbSize = Marshal.SizeOf<SpDeviceInterfaceData>()
                };

                var ok = SetupDiEnumDeviceInterfaces(
                    deviceInfoSet,
                    IntPtr.Zero,
                    ref guidDevinterfaceComport,
                    index,
                    ref interfaceData);

                if (!ok)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new Win32Exception(error, "SetupDiEnumDeviceInterfaces failed.");
                }

                var devInfoData = new SpDevinfoData
                {
                    cbSize = Marshal.SizeOf<SpDevinfoData>()
                };

                _ = GetDevicePath(deviceInfoSet, ref interfaceData, ref devInfoData);

                var comPort = GetPortName(deviceInfoSet, ref devInfoData);
                if (string.IsNullOrWhiteSpace(comPort))
                {
                    continue;
                }

                results.Add(new ComDeviceInfo(
                    comPort,
                    GetDeviceInstanceId(deviceInfoSet, ref devInfoData),
                    GetDeviceRegistryPropertyString(deviceInfoSet, ref devInfoData, SpdrpFriendlyName),
                    GetDeviceRegistryPropertyString(deviceInfoSet, ref devInfoData, SpdrpDevDesc)));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return results;
    }

    private static string? GetDevicePath(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData interfaceData,
        ref SpDevinfoData devInfoData)
    {
        _ = SetupDiGetDeviceInterfaceDetail(
            deviceInfoSet,
            ref interfaceData,
            IntPtr.Zero,
            0,
            out var requiredSize,
            ref devInfoData);

        var detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);

            var ok = SetupDiGetDeviceInterfaceDetail(
                deviceInfoSet,
                ref interfaceData,
                detailBuffer,
                requiredSize,
                out _,
                ref devInfoData);

            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetDeviceInterfaceDetail failed.");
            }

            var devicePathPtr = IntPtr.Size == 8
                ? new IntPtr(detailBuffer.ToInt64() + 8)
                : new IntPtr(detailBuffer.ToInt32() + 4);

            return Marshal.PtrToStringAuto(devicePathPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    private static string? GetPortName(IntPtr deviceInfoSet, ref SpDevinfoData devInfoData)
    {
        var registryKeyHandle = SetupDiOpenDevRegKey(
            deviceInfoSet,
            ref devInfoData,
            DicsFlagGlobal,
            0,
            DiregDev,
            KeyQueryValue);

        if (registryKeyHandle == IntPtr.Zero || registryKeyHandle.ToInt64() == -1)
        {
            return null;
        }

        try
        {
            using var key = RegistryKey.FromHandle(new SafeRegistryHandle(registryKeyHandle, ownsHandle: true));
            return key.GetValue("PortName")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetDeviceRegistryPropertyString(IntPtr deviceInfoSet, ref SpDevinfoData devInfoData, int property)
    {
        _ = SetupDiGetDeviceRegistryProperty(
            deviceInfoSet,
            ref devInfoData,
            property,
            out _,
            null,
            0,
            out var requiredSize);

        if (requiredSize == 0)
        {
            return null;
        }

        var buffer = new byte[requiredSize];
        var ok = SetupDiGetDeviceRegistryProperty(
            deviceInfoSet,
            ref devInfoData,
            property,
            out _,
            buffer,
            (uint)buffer.Length,
            out _);

        if (!ok)
        {
            return null;
        }

        return TrimNullTerminator(Encoding.Unicode.GetString(buffer));
    }

    private static string GetDeviceInstanceId(IntPtr deviceInfoSet, ref SpDevinfoData devInfoData)
    {
        var builder = new StringBuilder(512);
        var ok = SetupDiGetDeviceInstanceId(
            deviceInfoSet,
            ref devInfoData,
            builder,
            builder.Capacity,
            out _);

        if (!ok)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetDeviceInstanceId failed.");
        }

        return builder.ToString();
    }

    private static string TrimNullTerminator(string value)
    {
        var nullIndex = value.IndexOf('\0');
        return nullIndex >= 0 ? value[..nullIndex] : value;
    }

    internal readonly record struct ComDeviceInfo(
        string ComPort,
        string InstanceId,
        string? FriendlyName,
        string? Description);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        IntPtr enumerator,
        IntPtr hwndParent,
        int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        int memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        int property,
        out uint propertyRegDataType,
        [Out] byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        uint scope,
        uint hwProfile,
        uint keyType,
        uint samDesired);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    internal static extern int CMLocateDevNode(
        out uint deviceInstance,
        string deviceId,
        int flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    internal static extern int CMGetParent(
        out uint parentDeviceInstance,
        uint deviceInstance,
        int flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    internal static extern int CMGetDeviceId(
        uint deviceInstance,
        StringBuilder buffer,
        int bufferLen,
        int flags);
}
