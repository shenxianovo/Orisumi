using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Capture.Storage;

namespace Orisumi.Collectors.MacOS;

// One instance per helper stream. A failed batch ends the run because Swift has advanced
// its comparison state. Local handles never cross runs.
internal sealed class MacOSObservations
{
    private readonly Dictionary<long, long> pendingInput = new();
    private readonly Dictionary<string, string> cameras = new();
    private string? focus;
    private long sequence;

    internal void Write(SqliteCommand command, long run, JsonElement batch)
    {
        foreach (var observation in batch.EnumerateArray())
            WriteEvent(command, run, observation);
    }

    internal void Finish()
    {
        if (pendingInput.Count != 0)
            throw new InvalidDataException("macOS observer stopped with incomplete input events");
    }

    private void WriteEvent(SqliteCommand command, long run, JsonElement e)
    {
        string? Text(string key) => e.GetProperty(key).GetString();
        long? Number(string key)
        {
            var value = e.GetProperty(key);
            return value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
        }
        bool? Flag(string key) => Boolean(e.GetProperty(key));
        string? Id(string key) => Text(key) is { } handle ? $"{run}:{handle}" : null;

        var at = Number("at")!.Value;
        var type = Text("type");
        switch (type)
        {
            case "process":
                command.Execute("INSERT INTO process_instances VALUES($p0,$p1,$p2,$p3,$p4,$p5)",
                    Id("instance"), run, Number("pid"), Text("bundle"), Text("name"), Number("launched"));
                return;
            case "read_failure":
                command.Execute("INSERT INTO capture_read_failures(run_id,observed_at_utc_ms,error) VALUES($p0,$p1,$p2)",
                    run, at, Text("error"));
                return;
            case "audio_link":
                command.Execute("""
                    UPDATE audio_process_instances SET process_instance_id=$p0
                    WHERE audio_process_instance_id=$p1 AND process_instance_id IS NULL
                    """, Id("application"), Id("instance"));
                return;
            case "camera_reset":
                cameras.Clear();
                return;
            case "input_start":
                pendingInput.Add(Number("input")!.Value, ++sequence);
                return;
        }

        // A press is completed on release; keep the sequence assigned when it started.
        var order = type is "keyboard" or "mouse" ? TakeInput(Number("input")!.Value) : ++sequence;
        void Insert(string table, params object?[] fields) => WriteRow(command, table, run, order, at, fields);

        switch (type)
        {
            case "workspace":
                var kind = Text("kind");
                switch (kind)
                {
                    case "snapshot" or "activated":
                        var app = Id("instance");
                        Insert("foreground_app_events", kind, app is not null, app);
                        break;
                    case "sleep" or "wake":
                        Insert("system_sleep_events", kind);
                        break;
                    case "session_active" or "session_inactive":
                        Insert("user_session_events", kind);
                        break;
                    default: throw new InvalidDataException($"Unknown macOS workspace event: {kind}");
                }
                break;
            case "screen_lock":
                Insert("screen_lock_events", Text("kind"), Flag("value"));
                break;
            case "display_sleep":
                Insert("display_sleep_events", Text("kind"), Flag("value"));
                break;
            case "application":
                var instance = Id("instance");
                Insert("running_app_observations", Text("kind"), instance);
                if (Text("kind") != "disappeared")
                    Insert("application_activation_policy_observations", instance, Text("policy"));
                break;
            case "policy":
                Insert("application_activation_policy_observations", Id("instance"), Text("policy"));
                break;
            case "window":
                var windowKind = Text("kind");
                if (windowKind != "title_changed")
                {
                    var hasWindow = Flag("hasWindow");
                    focus = hasWindow == true ? $"{run}:{order}" : null;
                    if (focus is not null)
                        command.Execute("INSERT INTO window_focus_instances VALUES($p0,$p1,$p2)", focus, run, Id("instance"));
                    Insert("focused_window_events", windowKind, hasWindow, focus);
                }
                if (focus is not null)
                    Insert("window_title_observations", focus, Text("title"));
                break;
            case "channel":
                Insert("capture_channel_events", Text("channel"), Flag("available"), Text("reason"));
                break;
            case "method":
                Insert("capture_method_events", Text("channel"), Text("mode"));
                break;
            case "permission":
                Insert("capture_permission_events", Text("permission"), Text("kind"));
                break;
            case "clock":
                Insert("clock_events", Text("kind"), Number("monotonic"));
                break;
            case "time_zone":
                Insert("time_zone_events", Text("zone"));
                break;
            case "afk":
                Insert("afk_events", Text("kind"), Text("state"), Number("idle"));
                break;
            case "keyboard":
                command.Execute("""
                    INSERT INTO keyboard_presses(run_id,run_sequence,pressed_at_utc_ms,released_at_utc_ms,key_code,modifiers_at_press)
                    VALUES($p0,$p1,$p2,$p3,$p4,$p5)
                    """, run, order, at, Number("released"), Number("key"), Number("modifiers"));
                break;
            case "mouse":
                Insert("mouse_button_events", Text("kind"), e.GetProperty("x").GetDouble(), e.GetProperty("y").GetDouble(), Number("button"));
                break;
            case "media_presence":
                var media = Text("media");
                var handle = Id("instance")!;
                var presence = Text("kind");
                var objectId = Number("object");
                if (media == "camera_device")
                {
                    if (presence != "disappeared")
                    {
                        cameras[handle] = (string)command.Scalar("""
                            INSERT INTO camera_devices VALUES($p0,$p1,$p2)
                            ON CONFLICT(device_uid) DO UPDATE SET device_name=COALESCE(camera_devices.device_name,excluded.device_name)
                            RETURNING camera_device_id
                            """, handle, Text("uid"), Text("name"))!;
                    }
                    Insert("camera_device_presence_events", cameras[handle], presence, objectId);
                }
                else if (media == "audio_process")
                {
                    if (presence != "disappeared")
                        command.Execute("INSERT INTO audio_process_instances VALUES($p0,$p1,$p2,$p3,$p4,$p5)",
                            handle, run, objectId, Number("pid"), Text("bundle"), Id("application"));
                    Insert("audio_process_presence_events", handle, presence);
                }
                else throw new InvalidDataException($"Unknown macOS media type: {media}");

                foreach (var state in e.GetProperty("values").EnumerateObject())
                    WriteMediaState(command, run, order, at, state.Name, handle, Boolean(state.Value));
                if (media == "camera_device" && presence == "disappeared") cameras.Remove(handle);
                break;
            case "media_state":
                WriteMediaState(command, run, order, at, Text("channel"), Id("instance")!, Flag("value"));
                break;
            default: throw new InvalidDataException($"Unknown macOS observation: {type}");
        }
    }

    private long TakeInput(long token)
    {
        if (!pendingInput.Remove(token, out var order))
            throw new InvalidDataException($"macOS input event {token} has no start");
        return order;
    }

    private void WriteMediaState(SqliteCommand command, long run, long order, long at, string? channel, string id, bool? value)
    {
        var table = channel switch
        {
            "microphone" => "audio_input_observations",
            "audio_output" => "audio_output_observations",
            "camera" => "camera_running_observations",
            _ => throw new InvalidDataException($"Unknown macOS media channel: {channel}"),
        };
        WriteRow(command, table, run, order, at, channel == "camera" ? cameras[id] : id, value);
    }

    private static bool? Boolean(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetBoolean();

    private static void WriteRow(SqliteCommand command, string table, long run, long order, long at, params object?[] fields)
    {
        object?[] values = [run, order, at, .. fields];
        var parameters = string.Join(',', Enumerable.Range(0, values.Length).Select(i => $"$p{i}"));
        command.Execute($"INSERT INTO {table} VALUES({parameters})", values);
    }
}
