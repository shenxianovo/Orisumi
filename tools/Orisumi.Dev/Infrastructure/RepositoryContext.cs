namespace Orisumi.Dev;

internal sealed record RepositoryContext(string Root)
{
    public string Path(params string[] parts) =>
        System.IO.Path.Combine([Root, .. parts]);

    public Git Git => new(Root);

    public static async Task<RepositoryContext> DiscoverAsync(string start) => new(await Git.TopLevelAsync(start));
}
