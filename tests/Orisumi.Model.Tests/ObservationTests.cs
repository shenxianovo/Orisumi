using Orisumi.Core;
using static Orisumi.Model.Tests.ModelFixture;

namespace Orisumi.Model.Tests;

public sealed class ObservationTests
{
    // OBSERVATION-5, OBSERVATION-6, OBSERVATION-7, OBSERVATION-8, OBSERVATION-9
    [Test]
    [Arguments(null, null, true)]
    [Arguments(0, null, true)]
    [Arguments(null, 0, true)]
    [Arguments(0, 0, true)]
    [Arguments(0, 1, true)]
    [Arguments(1, 0, false)]
    public async Task FactTimeAllowsUnknownPointAndIntervalButNotReversedBounds(int? start, int? end, bool valid)
    {
        var schema = Schema();
        var draft = Content(Json("{}")) with { StartAt = start is { } s ? DateTimeOffset.UnixEpoch.AddHours(s) : null, EndAt = end is { } e ? DateTimeOffset.UnixEpoch.AddHours(e) : null };
        if (valid) ObservationRules.Validate(draft, schema);
        else await Reject(() => ObservationRules.Validate(draft, schema), "OBSERVATION-6");
    }

    // OBSERVATION-9
    [Test]
    [Arguments(null, true)]
    [Arguments("Asia/Shanghai", true)]
    [Arguments("Etc/UTC", true)]
    [Arguments("UTC", true)]
    [Arguments("America/New_York", true)]
    [Arguments("China Standard Time", false)]
    [Arguments("", false)]
    [Arguments("Mars/Olympus", false)]
    public async Task TimeZoneMustBeIanaOrUnknown(string? zone, bool valid)
    {
        var schema = Schema();
        var draft = Content(Json("{}")) with { TimeZone = zone };
        if (valid) ObservationRules.Validate(draft, schema);
        else await Reject(() => ObservationRules.Validate(draft, schema), "OBSERVATION-9");
    }

}
