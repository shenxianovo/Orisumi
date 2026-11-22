using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orisumi.Cli.Day;

internal static class DayJson
{
    internal static void Write(TextWriter output, DayRange day, IEnumerable<DayDevice> devices)
    {
        using var bytes = new MemoryStream();
        using var json = new Utf8JsonWriter(bytes, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("date", day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        json.WriteString("time_zone", day.Zone.Id);
        Time("from", day.From);
        Time("to", day.To);
        json.WriteStartArray("devices");
        foreach (var device in devices)
        {
            var presence = device.PresenceFor(day);
            var attention = device.AttentionFor(presence);
            json.WriteStartObject();
            json.WriteString("device", device.Key);
            json.WriteString("name", device.Name);
            json.WriteBoolean("this_host", device.ThisHost);
            Strings("time_zones", device.TimeZones);
            Array("databases", device.Databases, database =>
            {
                json.WriteString("source", database.Source);
                json.WriteString("database", database.Id);
                Strings("missing", database.Missing);
            });
            json.WriteStartObject("summary");
            json.WriteNumber("observed_ms", presence.Where(span => span.Value != "unobserved").Sum(span => span.End - span.Start));
            json.WriteNumber("active_ms", presence.Where(span => span.Value == "active").Sum(span => span.End - span.Start));
            Array("apps", DayDevice.AppTotals(attention), app =>
            {
                App(app.App);
                json.WriteNumber("foreground_ms", app.Foreground);
                json.WriteNumber("active_ms", app.Active);
            });
            Array("titles", DayDevice.TitleTotals(attention, 60_000), title =>
            {
                App(title.App);
                json.WriteString("title", title.Title);
                json.WriteNumber("foreground_ms", title.Foreground);
                json.WriteNumber("active_ms", title.Active);
            });
            json.WriteEndObject();
            Array("presence", presence, span =>
            {
                Time("start", span.Start);
                Time("end", span.End);
                json.WriteString("state", span.Value);
            });
            Array("attention", attention, span =>
            {
                Time("start", span.Start);
                Time("end", span.End);
                json.WriteNumber("active_ms", span.Active);
                App(span.Value.App);
                json.WriteString("title", span.Value.Title);
                if (span.Value.App.Bundle == DayDevice.Chrome)
                {
                    if (span.Value.Tab is not { } tab) json.WriteNull("tab");
                    else
                    {
                        json.WriteStartObject("tab");
                        json.WriteString("url", tab.Url);
                        json.WriteString("title", tab.Title);
                        json.WriteEndObject();
                    }
                }
            });
            Array("agent_sessions", device.Agents.Fold().OrderBy(session => session.Start), session =>
            {
                json.WriteString("source", session.Source);
                json.WriteString("id", session.Id);
                json.WriteString("title", session.Title);
                json.WriteString("cwd", session.Cwd);
                json.WriteString("git_branch", session.Branch);
                Time("start", session.Start);
                Time("end", session.End);
                json.WriteNumber("prompts", session.Prompts);
                json.WriteNumber("subagents", session.Subagents);
            });
            Array("visits", device.Visits.OrderBy(visit => visit.At), visit =>
            {
                Time("at", visit.At);
                json.WriteString("url", visit.Url);
                json.WriteString("title", visit.Title);
                json.WriteString("transition", visit.Transition);
            });
            Array("downloads", device.Downloads.OrderBy(download => download.Start), download =>
            {
                Time("start", download.Start);
                json.WriteString("url", download.Url);
                json.WriteString("path", download.Path);
                json.WriteString("state", download.State);
            });
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
        output.WriteLine(Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length));

        void Time(string field, long milliseconds) => json.WriteString(field, day.Format(milliseconds));

        void App(DayApp app)
        {
            json.WriteString("app", app.Name);
            json.WriteString("bundle_id", app.Bundle);
        }

        void Strings(string field, IEnumerable<string> values)
        {
            json.WriteStartArray(field);
            foreach (var value in values) json.WriteStringValue(value);
            json.WriteEndArray();
        }

        void Array<T>(string field, IEnumerable<T> items, Action<T> write)
        {
            json.WriteStartArray(field);
            foreach (var item in items)
            {
                json.WriteStartObject();
                write(item);
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
    }
}
