using System.CommandLine;
using Orisumi.Core;

namespace Orisumi.Cli.Day;

internal static class DayCommand
{
    internal static Command Create(Option<string?> store, TextWriter output)
    {
        var date = new Option<string?>("--date") { Description = "查询日期（YYYY-MM-DD），默认今天。" };
        var zone = new Option<string?>("--time-zone") { Description = "查询所用的 IANA 时区，默认本机时区。" };
        var command = new Command("day", "按设备汇总一天的活动，输出 JSON。") { date, zone };
        command.SetAction(result =>
        {
            var day = DayRange.Create(result.GetValue(date), result.GetValue(zone));
            var location = new Store(result.GetValue(store) ?? Instance.DefaultRoot());
            var titles = DayTitles.Read(location.TitleRules);
            DayJson.Write(output, day, ReadDevices(location, day, titles, HostIdentity.Read()));
        });
        return command;
    }

    private static IEnumerable<DayDevice> ReadDevices(Store store, DayRange day, DayTitles titles, HostIdentity host)
    {
        var devices = new Dictionary<string, DayDevice>(StringComparer.Ordinal);
        foreach (var source in store.Sources().Order(StringComparer.Ordinal))
        foreach (var path in store.Databases(source))
        {
            using var database = new DayDatabase(path, day);
            var identity = database.Host();
            var id = Path.GetFileNameWithoutExtension(path);
            var key = identity.DeviceKey(id);
            if (!devices.TryGetValue(key, out var device))
                devices.Add(key, device = new(key, identity.IsThisHost(host), identity.Platform ?? source));

            if (database.Has("host_observations", "observed_at_utc_ms", "host_name"))
            {
                var sequence = database.Column("host_observations", "observation_id");
                foreach (var row in database.Read($"""
                    SELECT observed_at_utc_ms, host_name FROM host_observations
                    ORDER BY observed_at_utc_ms DESC, {sequence} DESC LIMIT 1
                    """, row => (At: row.GetInt64(0), Name: DayDatabase.Text(row, 1))))
                    if (row.At > device.NameAt) (device.NameAt, device.Name) = row;
            }

            switch (source)
            {
                case "macos":
                case "windows": new DaySystem(database, titles, source).ReadInto(device); break;
                case "browser": new DayBrowser(database, titles).ReadInto(device); break;
                case "claude": device.Agents.ReadClaude(database); break;
                case "codex": device.Agents.ReadCodex(database); break;
            }
            device.Databases.Add(new(source, id, database.Missing.ToArray()));
        }
        return devices.Values.OrderByDescending(device => device.ThisHost).ThenBy(device => device.Key, StringComparer.Ordinal);
    }
}
