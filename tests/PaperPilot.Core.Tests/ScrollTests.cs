using PaperPilot.Core;

namespace PaperPilot.Core.Tests;

public class ScrollPlannerTests
{
    [Theory]
    [InlineData(-480, 400)]
    [InlineData(-37, 300)]
    [InlineData(1200, 500)]
    [InlineData(-5, 300)]
    public void Steps_sum_exactly_and_stay_small(int total, int duration)
    {
        var steps = ScrollPlanner.Plan(total, duration, maxStep: 40);
        Assert.Equal(total, steps.Sum());
        Assert.All(steps, s => Assert.InRange(Math.Abs(s), 1, 40));
        Assert.All(steps, s => Assert.Equal(Math.Sign(total), Math.Sign(s)));
    }

    [Fact]
    public void Zero_scroll_has_no_steps() => Assert.Empty(ScrollPlanner.Plan(0, 400));

    [Fact]
    public void Eases_in_and_out()
    {
        var steps = ScrollPlanner.Plan(-600, 400);
        int mid = Math.Abs(steps[steps.Count / 2]);
        Assert.True(Math.Abs(steps[0]) < mid);
        Assert.True(Math.Abs(steps[^1]) < mid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    [InlineData(5000)]
    public void Duration_is_between_300_and_500(int px) =>
        Assert.InRange(ScrollPlanner.DurationFor(px), 300, 500);
}

public class ScrollMeasurerTests
{
    private static ScreenLine Line(int index, string text, int y, int h = 30)
    {
        var words = text.Split(' ').Select((w, i) =>
            new ScreenWord(w, TextNormalizer.NormalizeWord(w), new PixelRect(i * 50, y, 45, h), default)).ToList();
        return new ScreenLine(index, text, words, new PixelRect(0, y, 600, h), default);
    }

    private static List<ScreenLine> Page(int offset, params string[] texts) =>
        texts.Select((t, i) => Line(i, t, 100 + i * 40 - offset)).ToList();

    private static readonly string[] Sermon =
    {
        "For God so loved the world",
        "that he gave his only Son",
        "that whoever believes in him",
        "should not perish but have",
        "eternal life. John 3:16",
    };

    [Fact]
    public void Measures_downward_scroll()
    {
        var shift = ScrollMeasurer.Measure(Page(0, Sermon), Page(57, Sermon));
        Assert.NotNull(shift);
        Assert.Equal(57, shift!.Value.Pixels);
        Assert.Equal(5, shift.Value.Support);
    }

    [Fact]
    public void Tolerates_ocr_jitter_and_lines_leaving_the_screen()
    {
        var before = Page(0, Sermon);
        var after = Page(80, Sermon).Skip(2).ToList(); // first two lines scrolled off
        after[0] = after[0] with { Screen = after[0].Screen with { Y = after[0].Screen.Y + 2 } };
        var shift = ScrollMeasurer.Measure(before, after);
        Assert.NotNull(shift);
        Assert.InRange(shift!.Value.Pixels, 79, 81);
    }

    [Fact]
    public void No_shift_when_nothing_matches()
    {
        var after = Page(0, "completely different text", "nothing in common here at all");
        Assert.Null(ScrollMeasurer.Measure(Page(0, Sermon), after));
    }

    [Fact]
    public void Repeated_lines_are_not_used()
    {
        var before = Page(0, "Amen and amen", "Amen and amen", "a unique line of text");
        var after = Page(40, "Amen and amen", "Amen and amen", "a unique line of text");
        var shift = ScrollMeasurer.Measure(before, after);
        Assert.NotNull(shift);
        Assert.Equal(40, shift!.Value.Pixels);
        Assert.Equal(1, shift.Value.Candidates);
    }

    [Fact]
    public void Unchanged_page_measures_zero()
    {
        var shift = ScrollMeasurer.Measure(Page(0, Sermon), Page(0, Sermon));
        Assert.Equal(0, shift!.Value.Pixels);
    }
}
