using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Orisumi;

internal sealed record CapturedFile(string Path, string Origin, long Bytes, string Sha256);

internal sealed record Capture(
    string Id,
    string Source,
    string Collector,
    string Host,
    DateTimeOffset CapturedAt,
    string Origin,
    DateTimeOffset From,
    DateTimeOffset To,
    string? SourceVersion,
    IReadOnlyList<CapturedFile> Files)
{
    public Position At(string file, int line) => new(Source, Id, file, line);

    public TimeRange Range => new(From, To);
}

internal sealed record Position(string Source, string CaptureId, string File, int Line)
{
    public override string ToString() => $"{Source}/{CaptureId}/{File}:{Line}";

    public static Position Parse(string text)
    {
        var colon = text.LastIndexOf(':');
        var parts = colon < 0 ? [] : text[..colon].Split('/', 3);
        if (parts.Length < 3) throw new FormatException($"Not a position: {text}");
        return new(parts[0], parts[1], parts[2], int.Parse(text[(colon + 1)..], CultureInfo.InvariantCulture));
    }
}

internal sealed record Reading(string Id, string Name, string Program, DateTimeOffset ReadAt, IReadOnlyList<string> Inputs);

internal sealed class Store(string root)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Indented = new(Json) { WriteIndented = true };

    public static string Version { get; } =
        typeof(Store).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static Store Open() => new(Environment.GetEnvironmentVariable("ORISUMI_STORE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".orisumi"));

    public CaptureWriter BeginCapture(string source, string origin, TimeRange range, string? sourceVersion = null)
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var id = capturedAt.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var capture = new Capture(id, source, $"orisumi capture {source} {Version}", Environment.MachineName,
            capturedAt, origin, range.From, range.To, sourceVersion, []);
        return new CaptureWriter(Path.Combine(root, "raw", source, id), capture);
    }

    public IReadOnlyList<Capture> Captures()
    {
        var raw = Path.Combine(root, "raw");
        if (!Directory.Exists(raw)) return [];
        return [.. Directory.EnumerateFiles(raw, "capture.json", SearchOption.AllDirectories)
            .Select(file => JsonSerializer.Deserialize<Capture>(File.ReadAllText(file), Json)!)
            .OrderBy(capture => capture.CapturedAt)];
    }

    public Capture Latest(string source) =>
        Captures().LastOrDefault(capture => capture.Source == source)
        ?? throw new InvalidOperationException($"No capture of {source}. Run `capture {source}` first.");

    public string PathOf(Capture capture, string file) => Path.Combine(root, "raw", capture.Source, capture.Id, file);

    public string PathOf(Position position) => Path.Combine(root, "raw", position.Source, position.CaptureId, position.File);

    public Reading WriteReading<T>(string name, IEnumerable<Capture> inputs, IEnumerable<T> items)
    {
        var readAt = DateTimeOffset.UtcNow;
        var reading = new Reading(readAt.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture), name,
            $"orisumi read {name} {Version}", readAt, [.. inputs.Select(capture => $"{capture.Source}/{capture.Id}")]);
        var directory = Path.Combine(root, "readings", name, reading.Id);
        Directory.CreateDirectory(directory);
        using (var writer = new StreamWriter(Path.Combine(directory, "items.jsonl")))
        {
            foreach (var item in items) writer.WriteLine(JsonSerializer.Serialize(item, Json));
        }
        File.WriteAllText(Path.Combine(directory, "reading.json"), JsonSerializer.Serialize(reading, Indented));
        return reading;
    }

    public (Reading Reading, IReadOnlyList<T> Items) LatestReading<T>(string name)
    {
        var directory = Path.Combine(root, "readings", name);
        var latest = Directory.Exists(directory) ? Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal).LastOrDefault() : null;
        if (latest is null) throw new InvalidOperationException($"No reading named {name}. Run `read` first.");
        var reading = JsonSerializer.Deserialize<Reading>(File.ReadAllText(Path.Combine(latest, "reading.json")), Json)!;
        var items = File.ReadLines(Path.Combine(latest, "items.jsonl")).Select(line => JsonSerializer.Deserialize<T>(line, Json)!).ToList();
        return (reading, items);
    }

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Indented);
}

internal sealed class CaptureWriter(string directory, Capture capture)
{
    private readonly List<(string File, string Origin)> _files = [];

    public string Target(string file, string origin)
    {
        var target = Path.Combine(directory, file);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        _files.Add((file.Replace('\\', '/'), origin));
        return target;
    }

    public void Copy(string source, string file) => File.Copy(source, Target(file, source));

    public Capture Complete()
    {
        var files = _files.Select(entry => Describe(entry.File, entry.Origin)).ToList();
        var done = capture with { Files = files };
        File.WriteAllText(Path.Combine(directory, "capture.json"), Store.Serialize(done));
        return done;
    }

    private CapturedFile Describe(string file, string origin)
    {
        var path = Path.Combine(directory, file);
        using var stream = File.OpenRead(path);
        return new(file, origin, stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }
}

internal static class JsonLines
{
    public static IEnumerable<(int Line, string Text)> Read(string path)
    {
        var number = 0;
        foreach (var text in File.ReadLines(path))
        {
            number++;
            if (text.Length > 0) yield return (number, text);
        }
    }
}
