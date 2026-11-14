using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace Orisumi.Collectors.Network;

internal sealed record ProcessIdentity(long? StartedAtUtcUs, string? ExecutablePath);
internal sealed record SocketKey(string Protocol, int IpVersion, string LocalAddress, int? LocalPort,
    string RemoteAddress, int? RemotePort, string? Interface);
internal sealed record SocketSample(SocketKey Key, string? State, long? ReceivedBytes, long? SentBytes);
internal sealed record ProcessSample(int Pid, string Name, ProcessIdentity Identity,
    long? ReceivedBytes, long? SentBytes, List<SocketSample> Sockets);

internal static class NettopSnapshot
{
    internal static List<ProcessSample> Parse(string csv, Func<int, ProcessIdentity> identify)
    {
        using var parser = new TextFieldParser(new StringReader(csv))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false,
        };
        parser.SetDelimiters(",");
        var header = parser.ReadFields() ?? throw new InvalidDataException("nettop returned no CSV header.");
        var required = new[] { "interface", "state", "bytes_in", "bytes_out" };
        if (header[0] != "" || required.Any(name => header.Count(column => column == name) != 1))
            throw new InvalidDataException("nettop CSV is missing required columns.");
        var interfaceColumn = Array.IndexOf(header, "interface");
        var stateColumn = Array.IndexOf(header, "state");
        var inColumn = Array.IndexOf(header, "bytes_in");
        var outColumn = Array.IndexOf(header, "bytes_out");
        var result = new List<ProcessSample>();
        var pids = new HashSet<int>();
        ProcessSample? current = null;
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields()!;
            if (fields.Length != header.Length)
                throw new InvalidDataException("Unexpected nettop CSV row width.");
            var received = Counter(fields[inColumn]);
            var sent = Counter(fields[outColumn]);
            var socket = Regex.Match(fields[0], @"^(tcp|udp)(4|6) (.+)<->(.+)$");
            if (socket.Success)
            {
                if (current is null) throw new InvalidDataException("nettop socket has no owning process.");
                var version = int.Parse(socket.Groups[2].Value, CultureInfo.InvariantCulture);
                var local = Endpoint(socket.Groups[3].Value, version);
                var remote = Endpoint(socket.Groups[4].Value, version);
                current.Sockets.Add(new SocketSample(new SocketKey(socket.Groups[1].Value, version,
                    local.Address, local.Port, remote.Address, remote.Port, Optional(fields[interfaceColumn])),
                    Optional(fields[stateColumn]), received, sent));
                continue;
            }
            var process = Regex.Match(fields[0], @"^(.+)\.([1-9][0-9]*)$");
            if (!process.Success || !int.TryParse(process.Groups[2].Value, out var pid) || !pids.Add(pid))
                throw new InvalidDataException("Unexpected or repeated nettop process row.");
            current = new ProcessSample(pid, process.Groups[1].Value, identify(pid), received, sent, []);
            result.Add(current);
        }
        return result;
    }

    private static string? Optional(string value) => value.Length == 0 ? null : value;

    private static long? Counter(string value)
    {
        if (value.Length == 0) return null;
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)) return count;
        throw new InvalidDataException("nettop returned an invalid byte counter.");
    }

    private static (string Address, int? Port) Endpoint(string value, int version)
    {
        var separator = value.LastIndexOf(version == 4 ? ':' : '.');
        if (separator <= 0) throw new InvalidDataException("Invalid nettop endpoint.");
        var port = value[(separator + 1)..];
        if (port == "*") return (value[..separator], null);
        if (int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 0 and <= 65535)
            return (value[..separator], number);
        throw new InvalidDataException("Invalid nettop port.");
    }

    internal static ProcessIdentity Identify(int pid, DateTimeOffset sampleStarted)
        => MacProcessIdentity.Read(pid, sampleStarted);
}
