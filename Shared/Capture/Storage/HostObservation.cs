using Microsoft.Data.Sqlite;
using Orisumi.Core;

namespace Orisumi.Capture.Storage;

// The host a capture run starts on, read by the platform layer. A row is added only when a value changes;
public static class HostObservation
{
    // Returns the observation the run refers to; call it in the transaction that inserts the run.
    public static long Record(SqliteCommand command, string store)
    {
        var host = HostIdentity.Read();
        object?[] values =
        [
            host.HardwareUuid, host.HostName, host.Model, host.OsVersion, host.OsBuild, host.BootId, host.UserId, host.UserName,
            new Store(store).Root, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), host.Platform, host.MachineGuid, host.UserSid,
        ];
        if (command.Scalar("""
                SELECT observation_id FROM host_observations
                WHERE observation_id = (SELECT MAX(observation_id) FROM host_observations)
                  AND hardware_uuid IS $p0 AND host_name IS $p1 AND model IS $p2 AND os_version IS $p3 AND os_build IS $p4
                  AND boot_id IS $p5 AND user_id IS $p6 AND user_name IS $p7 AND store_path IS $p8 AND platform IS $p10
                  AND machine_guid IS $p11 AND user_sid IS $p12
                """, values) is long latest)
            return latest;
        return (long)command.Scalar("""
            INSERT INTO host_observations(observed_at_utc_ms,platform,hardware_uuid,host_name,model,os_version,os_build,boot_id,user_id,user_name,store_path,machine_guid,user_sid)
            VALUES ($p9,$p10,$p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p11,$p12) RETURNING observation_id
            """, values)!;
    }
}
