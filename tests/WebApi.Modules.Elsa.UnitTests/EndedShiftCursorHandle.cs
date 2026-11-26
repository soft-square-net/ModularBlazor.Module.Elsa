using soft.square.net.WebApi.Modules.Elsa.Infrastructure;

namespace soft.square.net.WebApi.Modules.Elsa.UnitTests;

public class EndedShiftCursorHandle
{
    private static readonly DateTimeOffset Start = new(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly EndedShiftCursor _cursor = new(Start);
    private readonly List<EndedShift> _ended = [];
    private readonly List<DateTimeOffset> _reads = [];
    private readonly List<int> _handedOver = [];
    private readonly HashSet<int> _failing = [];

    private static EndedShift Shift(int id, double minutes) => new(id, Start.AddMinutes(minutes));

    /// <summary>Drains against a list that behaves like Shifts': since is inclusive, by end then id, one page.</summary>
    private Task<bool> DrainAsync() => _cursor.DrainAsync(
        (since, _) =>
        {
            _reads.Add(since);
            return Task.FromResult<IReadOnlyList<EndedShift>?>(_ended.Where(s => s.EndedAt >= since)
                .OrderBy(s => s.EndedAt).ThenBy(s => s.Id).Take(EndedShiftCursor.PageSize).ToArray());
        },
        (id, _) =>
        {
            if (_failing.Contains(id)) throw new HttpRequestException($"handover {id} failed");
            _handedOver.Add(id);
            return Task.CompletedTask;
        },
        Ct);

    [Fact]
    public async Task TheFirstReadStartsAtTheLookbackAndLaterOnesJustBehindTheNewestEnd()
    {
        _ended.AddRange([Shift(1, 10), Shift(2, 30)]);

        await DrainAsync();
        await DrainAsync();

        _reads.ShouldBe([Start, Start.AddMinutes(30) - EndedShiftCursor.Overlap]);
    }

    [Fact]
    public async Task AShiftListedAgainIsHandedOverOnce()
    {
        _ended.AddRange([Shift(2, 10), Shift(1, 10)]);

        await DrainAsync();
        _ended.Add(Shift(3, 10));
        await DrainAsync();
        await DrainAsync();

        _handedOver.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task AShiftThatAppearsBehindTheNewestEndIsStillHandedOver()
    {
        _ended.Add(Shift(1, 10));
        await DrainAsync();

        _ended.Add(Shift(2, 9)); // saved after shift 1 was read, with an earlier end
        await DrainAsync();

        _handedOver.ShouldBe([1, 2]);
    }

    [Fact]
    public async Task AnEmptyListKeepsTheCursorAtItsStart()
    {
        await DrainAsync();

        _cursor.Since.ShouldBe(Start);
    }

    [Fact]
    public async Task AFailedHandoverHoldsTheCursorAndIsRetriedWithoutRepeatingTheOthers()
    {
        _ended.AddRange([Shift(1, 10), Shift(2, 20), Shift(3, 60)]);
        _failing.Add(2);

        await Should.ThrowAsync<HttpRequestException>(DrainAsync);
        _handedOver.ShouldBe([1, 3]);
        _cursor.Since.ShouldBe(Start.AddMinutes(20) - EndedShiftCursor.Overlap);

        _failing.Clear();
        await DrainAsync();

        _handedOver.ShouldBe([1, 3, 2]);
        _cursor.Since.ShouldBe(Start.AddMinutes(60) - EndedShiftCursor.Overlap);
    }

    [Fact]
    public async Task ItForgetsShiftsTheNextReadCannotReturn()
    {
        _ended.AddRange([Shift(1, 10), Shift(2, 60), Shift(3, 61)]);

        await DrainAsync();

        _cursor.Remembered.ShouldBe(2);
    }

    [Fact]
    public async Task ItReadsOnWhileAPageIsFull()
    {
        var count = EndedShiftCursor.PageSize * 2 + 10;
        _ended.AddRange(Enumerable.Range(1, count).Select(i => Shift(i, i / 100d)));

        await DrainAsync();

        _handedOver.ShouldBe(Enumerable.Range(1, count));
        _reads.Count.ShouldBe(3);
    }

    [Fact]
    public async Task AFullPageOfOneInstantStopsWithAnError()
    {
        _ended.AddRange(Enumerable.Range(1, EndedShiftCursor.PageSize + 1).Select(i => Shift(i, 10)));

        await Should.ThrowAsync<InvalidOperationException>(DrainAsync);

        _handedOver.Count.ShouldBe(EndedShiftCursor.PageSize);
        _reads.Count.ShouldBe(2);
    }

    [Fact]
    public async Task WithoutAnEndedListItReportsFalseAndStaysPut()
    {
        var listed = await _cursor.DrainAsync((_, _) => Task.FromResult<IReadOnlyList<EndedShift>?>(null),
            (_, _) => throw new InvalidOperationException("nothing to hand over"), Ct);

        listed.ShouldBeFalse();
        _cursor.Since.ShouldBe(Start);
    }
}
