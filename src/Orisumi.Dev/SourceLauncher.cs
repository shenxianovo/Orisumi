using System.Globalization;
using Orisumi.Platform;

namespace Orisumi.Dev;

internal static class SourceLauncher
{
    internal static string? BuiltCheckout()
    {
        var checkout = Repository.Find(AppContext.BaseDirectory);
        return checkout is not null && DirectoryPaths.Contains(Path.Combine(checkout, "src", "Orisumi.Dev"), AppContext.BaseDirectory)
            ? checkout : null;
    }

    internal static async Task<int?> TryRun(string[] arguments)
    {
        // The packaged launcher always delegates to the checkout build.
        if (BuiltCheckout() is not null) return null;

        var root = Repository.Require();
        var project = Path.Combine(root, Repository.DevProject);
        return await RunProcess.WithCancellation(async token =>
        {
            // Build logs must never corrupt command stdout (in particular Native Messaging).
            var build = await RunProcess.Run(RunProcess.Dotnet, ["msbuild", Path.Combine(root, "Orisumi.slnx"), "-restore", "-target:Build", "-property:Configuration=Debug",
                "-nologo", "-verbosity:quiet", "-consoleLoggerParameters:NoSummary", "-tl:off"],
                Console.Error, token);
            if (build != 0)
                return build;

            // Ask MSBuild for the output path so SDK/framework changes in the checkout take effect too.
            using var target = new StringWriter(CultureInfo.InvariantCulture);
            var query = await RunProcess.Run(RunProcess.Dotnet, ["msbuild", project, "-nologo", "-property:Configuration=Debug", "-getProperty:TargetPath"],
                target, token);
            if (query != 0)
            {
                await Console.Error.WriteAsync(target.ToString());
                return query;
            }

            var assembly = target.ToString().Trim();
            if (!File.Exists(assembly))
                throw new IOException("构建未生成可运行的 Orisumi.Dev 程序。");
            return await RunProcess.Run(RunProcess.Dotnet, [assembly, .. arguments], output: null, token);
        });
    }
}
