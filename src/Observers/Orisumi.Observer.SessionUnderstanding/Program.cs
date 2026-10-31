using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.SessionUnderstanding;

internal static class Program
{
    public static async Task<int> Main()
    {
        await using var context = DataClient.Connect();
        await using var data = context.Direct;
        try
        {
            var settings = context.Settings;
            var model = settings.TryGetProperty("model", out var value) ? value.GetString()! : "gpt-6.1-terra";
            var seconds = settings.TryGetProperty("intervalSeconds", out value) ? value.GetDouble() : 300;
            var minutes = settings.TryGetProperty("idleMinutes", out value) ? value.GetDouble() : 30;
            if (!double.IsFinite(seconds) || seconds <= 0 || !double.IsFinite(minutes) || minutes <= 0)
                throw new ArgumentException("intervalSeconds and idleMinutes must be positive and finite.");
            using var http = new HttpClient();
            var recognizer = new SessionRecognizer(data, new(data), new OpenAiSessionModel(http, Environment.GetEnvironmentVariable("OPENAI_API_KEY"), model),
                model, context.PackageVersion!, TimeSpan.FromMinutes(minutes), TimeProvider.System, Console.Error.WriteLine);
            while (!context.Stopping.IsCancellationRequested)
            {
                try { await recognizer.RunOnceAsync(context.Stopping); }
                catch (Exception e) when (e is HttpRequestException or WriteRejectedException
                    || e is OperationCanceledException && !context.Stopping.IsCancellationRequested)
                { await Console.Error.WriteLineAsync($"这一轮结束，下轮重试：{e.Message}"); }
                await Task.Delay(TimeSpan.FromSeconds(seconds), context.Stopping);
            }
            return 0;
        }
        catch (OperationCanceledException) when (context.Stopping.IsCancellationRequested) { return 0; }
        catch (Exception e) { await Console.Error.WriteLineAsync(e.Message); return 1; }
    }
}
