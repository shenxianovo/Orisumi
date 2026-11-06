namespace Orisumi.Raw;

internal static class StoreFile
{
    public static bool Create(string path, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = $"{path}.{Guid.NewGuid():N}.pending";
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Write(bytes);
            File.Move(pending, path);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
        finally
        {
            File.Delete(pending);
        }
    }
}
