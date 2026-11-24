namespace Orisumi.Platform;

// Host identifiers as the platform reports them; unreadable values are null. Which device they belong to is
// read out later (overview/devices.mdx). Native helpers receive these values instead of reading them again.
public sealed partial record HostIdentity(string Platform, string? HardwareUuid, string? HostName, string? Model,
    string? OsVersion, string? OsBuild, string? BootId, long? UserId, string? UserName, string? MachineGuid = null, string? UserSid = null)
{
    public static partial HostIdentity Read();

    // Compare the raw host keys available on this platform when selecting a writable database.
    public bool MatchesDatabase(string platform, string? hardwareUuid, string? machineGuid)
    {
        if (Platform != platform) return false;
        var hardware = DeviceUuid(Platform, HardwareUuid);
        if (hardware is not null) return string.Equals(hardware, DeviceUuid(platform, hardwareUuid), StringComparison.OrdinalIgnoreCase);
        return Platform != "windows" || MachineGuid is null || string.Equals(MachineGuid, machineGuid, StringComparison.OrdinalIgnoreCase);
    }

    public static string? DeviceUuid(string platform, string? uuid) =>
        platform == "windows" && (uuid == "00000000-0000-0000-0000-000000000000" ||
            string.Equals(uuid, "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", StringComparison.OrdinalIgnoreCase)) ? null : uuid;
}
