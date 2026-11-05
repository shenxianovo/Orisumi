namespace Orisumi.Dev;

internal sealed record WritingFinding(string Path, int Line, string Question, string Title, double Probability, string Excerpt);

internal sealed record ExceptionReview(DeclaredException Exception, bool? Holds, double? Probability, string Evidence);

internal sealed record ReviewReport(
    string Base,
    string Commit,
    string Model,
    bool Judged,
    IReadOnlyList<string> Files,
    IReadOnlyList<WritingFinding> Writing,
    IReadOnlyList<ExceptionReview> Exceptions,
    IReadOnlyList<string> Failures)
{
    public bool Completed => Failures.Count == 0;

    public IEnumerable<ExceptionReview> BrokenExceptions => Exceptions.Where(review => review.Holds == false);

    public int Findings => Writing.Count + BrokenExceptions.Count();
}

internal sealed class Reviewer(RepositoryContext repository, IProcessRunner runner, ModelAccess models)
{
    private sealed record Writing(string Path, Passage Passage, Question Question);

    private sealed record Declared(DeclaredException Exception, string Input);

    public async Task<ReviewReport> ReviewAsync(string requested, string commit, Run run, CancellationToken cancellationToken)
    {
        var layout = ProjectLayout.Load(repository.Root);
        var questions = Questions.Load(repository, layout);
        var evidence = new Evidence(repository, layout, await repository.Git.FilesAsync(cancellationToken));
        var reason = questions.FirstOrDefault(question => question.Id == UnusedLint.ReasonQuestion);

        var writing = await WritingAsync(questions, commit, cancellationToken);
        var exceptions = Exceptions(layout, evidence);
        var asks = writing.Select(item => (item.Passage.Text, item.Question))
            .Concat(reason is null ? [] : exceptions.Select(item => (item.Input, reason)))
            .ToArray();

        var client = models.Client();
        var model = client?.Model ?? OpenAIDecisionClient.DefaultModel;
        var judging = client is null
            ? new Judging(new Dictionary<(string, string), Answer>(), [], [])
            : await new Judge(model, client, new CacheStore(repository)).AskAsync(asks, cancellationToken);
        if (judging.Calls.Count > 0) await Judge.SaveCallsAsync(run, RunFiles.ReviewCalls, judging.Calls);

        var report = new ReviewReport(requested, commit, model, client is not null,
            [.. writing.Select(item => item.Path).Distinct()],
            [.. writing.SelectMany(item => Finding(item, judging)).OrderBy(finding => finding.Path, StringComparer.Ordinal).ThenBy(finding => finding.Line)],
            [.. exceptions.Select(item => Review(item, reason, judging))],
            judging.Failures);
        await run.WriteJsonAsync(RunFiles.Review, report);
        return report;
    }

    private async Task<List<Writing>> WritingAsync(IReadOnlyList<Question> questions, string commit, CancellationToken cancellationToken)
    {
        var writing = new List<Writing>();
        foreach (var path in await repository.Git.ChangedPathsAsync(commit, cancellationToken))
        {
            var reviewing = questions.Where(question => question.Reviews(path)).ToArray();
            if (reviewing.Length == 0 || ProjectLayout.IsScratch(path) || !File.Exists(repository.Path(path))) continue;
            var before = await repository.Git.ShowAsync(commit, path, cancellationToken);
            var added = Passages.Added(before, await File.ReadAllTextAsync(repository.Path(path), cancellationToken));
            writing.AddRange(added.SelectMany(passage => reviewing.Select(question => new Writing(path, passage, question))));
        }
        return writing;
    }

    private Declared[] Exceptions(ProjectLayout layout, Evidence evidence)
    {
        var lint = new UnusedLint(repository, runner, layout);
        var reasons = lint.Reasons();
        return [.. lint.Projects.SelectMany(project => lint.Exceptions(project, reasons).Exceptions)
            .Select(exception => new Declared(exception, evidence.Unused(exception)))];
    }

    private static IEnumerable<WritingFinding> Finding(Writing item, Judging judging)
    {
        if (judging.Answer(item.Passage.Text, item.Question) is not { } answer || !item.Question.Passes(answer, Question.Yes)) yield break;
        yield return new WritingFinding(item.Path, item.Passage.Line, item.Question.Id, item.Question.Title,
            answer.ProbabilityOf(item.Question, Question.Yes)!.Value, Terminal.Excerpt(item.Passage.Text));
    }

    private static ExceptionReview Review(Declared item, Question? reason, Judging judging)
    {
        var answer = reason is null ? null : judging.Answer(item.Input, reason);
        return new ExceptionReview(item.Exception, answer is null ? null : reason!.Passes(answer, item.Exception.Reason),
            answer?.ProbabilityOf(reason!, item.Exception.Reason), item.Input);
    }
}
