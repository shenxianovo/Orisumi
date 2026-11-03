namespace Orisumi.Dev;

internal sealed record Answer(IReadOnlyDictionary<string, double>? Probabilities)
{
    public static Answer Refusal { get; } = new((IReadOnlyDictionary<string, double>?)null);

    public bool Refused => Probabilities is null;

    public double? ProbabilityOf(Question question, string expected) => Probabilities switch
    {
        null => null,
        _ when question.Type == QuestionType.Predicate => expected == Question.Yes
            ? Probabilities[Question.Yes] : 1 - Probabilities[Question.Yes],
        _ => Probabilities.GetValueOrDefault(expected, 0),
    };
}
