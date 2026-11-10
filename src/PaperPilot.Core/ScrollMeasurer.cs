namespace PaperPilot.Core;

/// <param name="Pixels">How far the content moved up (positive = page scrolled down).</param>
/// <param name="Support">How many distinct lines agreed on that shift.</param>
/// <param name="Candidates">How many lines could be matched at all.</param>
public readonly record struct ScrollShift(int Pixels, int Support, int Candidates)
{
    public override string ToString() => $"{Pixels}px ({Support}/{Candidates} lines agree)";
}

/// <summary>
/// Works out how far the page moved between two OCR reads by finding the same lines in both.
/// Used to calibrate scrolling and to notice movement Paper Pilot didn't cause.
/// </summary>
public static class ScrollMeasurer
{
    private const int MinTextLength = 6;

    /// <summary>Returns the vertical shift, or null when it can't be determined confidently.</summary>
    public static ScrollShift? Measure(IReadOnlyList<ScreenLine> before, IReadOnlyList<ScreenLine> after)
    {
        var beforeByText = UniqueByText(before);
        var afterByText = UniqueByText(after);

        var shifts = new List<int>();
        var heights = new List<int>();
        foreach (var (text, a) in afterByText)
        {
            if (beforeByText.TryGetValue(text, out var b))
            {
                shifts.Add(b.Screen.Y - a.Screen.Y);
                heights.Add(Math.Max(a.Screen.Height, b.Screen.Height));
            }
        }
        if (shifts.Count == 0) return null;

        shifts.Sort();
        heights.Sort();
        int median = shifts[shifts.Count / 2];
        int lineHeight = heights[heights.Count / 2];
        int tolerance = Math.Max(3, lineHeight / 6);

        var agreeing = shifts.Where(s => Math.Abs(s - median) <= tolerance).ToList();
        int support = agreeing.Count;

        // Need two agreeing lines, unless only one line could be matched at all and nothing contradicts it.
        if (support < 2 && !(shifts.Count == 1 && support == 1)) return null;
        // A majority must agree, otherwise the page probably changed rather than moved.
        if (support * 2 <= shifts.Count) return null;

        int refined = (int)Math.Round(agreeing.Average());
        return new ScrollShift(refined, support, shifts.Count);
    }

    private static Dictionary<string, ScreenLine> UniqueByText(IReadOnlyList<ScreenLine> lines)
    {
        var map = new Dictionary<string, ScreenLine>();
        var duplicates = new HashSet<string>();
        foreach (var line in lines)
        {
            var key = line.NormalizedText;
            if (key.Length < MinTextLength) continue;
            if (!map.TryAdd(key, line)) duplicates.Add(key);
        }
        foreach (var d in duplicates) map.Remove(d);
        return map;
    }
}
