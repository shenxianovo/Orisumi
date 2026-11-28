using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace Orisumi.Core;

public sealed partial record HostIdentity
{
    public static partial HostIdentity Read()
    {
        var version = Environment.OSVersion.Version;
        return new("windows", SmbiosUuid(), Environment.MachineName,
            RegistryText(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName"), version.ToString(), version.Build.ToString(),
            null, null, Environment.UserName, RegistryText(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid"), CurrentSid());
    }

    private static string? CurrentSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (SecurityException) { return null; }
    }

    private static string? RegistryText(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static string? SmbiosUuid()
    {
        const uint rawSmbios = 0x52534D42; // 'RSMB'
        var size = GetSystemFirmwareTable(rawSmbios, 0, null, 0);
        if (size < 8) return null;
        var data = new byte[size];
        var read = GetSystemFirmwareTable(rawSmbios, 0, data, size);
        if (read < 8 || read > size) return null;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (length > read - 8) return null;
        var table = data.AsSpan(8, (int)length);
        while (table.Length >= 4)
        {
            var type = table[0];
            var formattedSize = table[1];
            if (formattedSize < 4 || formattedSize > table.Length || type == 127) break;
            if (type == 1 && formattedSize >= 24)
            {
                // DSP0134 System Information: the first three UUID fields are little-endian from SMBIOS 2.6.
                var legacyOrder = data[1] < 2 || data[1] == 2 && data[2] < 6;
                return new Guid(table.Slice(8, 16), bigEndian: legacyOrder).ToString("D").ToUpperInvariant();
            }
            var end = table[formattedSize..].IndexOf("\0\0"u8);
            if (end < 0) break;
            table = table[(formattedSize + end + 2)..];
        }
        return null;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, [Out] byte[]? buffer, uint size);
}
