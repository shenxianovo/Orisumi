using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orisumi.Core;

namespace Orisumi.Desktop;

internal sealed record JobStatus(string State, int? Pid, int? LastExitCode, string Hash, long? StartedAtUtcMs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JobSpec))]
[JsonSerializable(typeof(Dictionary<string, JobStatus>))]
internal sealed partial class JobJson : JsonSerializerContext;

internal sealed class JobFiles(Store store)
{
    internal string DirectoryPath => Path.Combine(store.State, "jobs");
    private string LockPath => store.LockFile("supervisor");
    internal string StatusPath => Path.Combine(DirectoryPath, "status.json");

    internal FileStream Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal bool Alive()
    {
        try
        {
            using var probe = File.Open(LockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (IOException error) when ((error.HResult & 0xffff) == 32) { return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal string Desired(string source) => Instance.IsName(source) && source != "status"
        ? Path.Combine(DirectoryPath, source + ".json") : throw new ArgumentException($"无效的任务名：{source}");

    internal IEnumerable<string> Names() => Directory.Exists(DirectoryPath)
        ? Directory.EnumerateFiles(DirectoryPath, "*.json").Select(Path.GetFileNameWithoutExtension)
            .OfType<string>().Where(name => name != "status" && Instance.IsName(name)) : [];

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static byte[]? Read(string path)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            file.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal Dictionary<string, JobStatus> Status() => Read(StatusPath) is { } bytes
        ? JsonSerializer.Deserialize(bytes, JobJson.Default.DictionaryStringJobStatus)! : [];

    internal static void Write(string path, byte[]? bytes)
    {
        if (bytes is null)
        {
            if (Directory.Exists(Path.GetDirectoryName(path))) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            AtomicFile.Replace(temporary, path);
        }
        finally { File.Delete(temporary); }
    }
}
