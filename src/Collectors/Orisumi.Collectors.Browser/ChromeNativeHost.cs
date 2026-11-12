using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Orisumi.Collectors.Browser;

public static class ChromeNativeHost
{
    private const string Name = "com.orisumi.browser";
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    public static string Register(string store, string extensionId, string executable)
    {
        if (!OperatingSystem.IsMacOS())
            throw new IOException("Chrome native host registration requires macOS");
        if (extensionId.Length != 32 || extensionId.Any(character => character is < 'a' or > 'p'))
            throw new ArgumentException("expected a 32-character Chrome extension ID");

        var storePath = Path.GetFullPath(store);
        var launcher = Path.Combine(storePath, "chrome-native-host");
        Directory.CreateDirectory(storePath);
        // Chrome does not inherit the shell's .NET environment.
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));
        var command = File.Exists(dotnet)
            ? $"{ShellQuote(dotnet)} {ShellQuote(Path.ChangeExtension(executable, ".dll"))}"
            : ShellQuote(executable);
        File.WriteAllText(launcher, $"#!/bin/sh\nexec {command} --store {ShellQuote(storePath)} browser native-host\n");
        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var manifestDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Google", "Chrome", "NativeMessagingHosts");
        Directory.CreateDirectory(manifestDirectory);
        var manifest = Path.Combine(manifestDirectory, Name + ".json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            name = Name,
            description = "Orisumi browser collector",
            path = launcher,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{extensionId}/" },
        }));
        return manifest;
    }

    public static async Task Run(string store)
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        var header = new byte[sizeof(int)];
        byte[] reply;
        try
        {
            await input.ReadExactlyAsync(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is <= 0 or > MaxMessageBytes)
                throw new InvalidDataException("invalid native message length");
            var message = new byte[length];
            await input.ReadExactlyAsync(message);
            var result = await BrowserDatabase.HandleMessage(store, message);
            reply = JsonSerializer.SerializeToUtf8Bytes(result, JsonSerializerOptions.Web);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or Win32Exception)
        {
            Console.Error.WriteLine("orisumi: " + error.GetType().Name);
            reply = JsonSerializer.SerializeToUtf8Bytes(new { ok = false, error = "The local collector could not save this batch." });
        }

        BinaryPrimitives.WriteInt32LittleEndian(header, reply.Length);
        await output.WriteAsync(header);
        await output.WriteAsync(reply);
        await output.FlushAsync();
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}
