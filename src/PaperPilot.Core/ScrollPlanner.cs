namespace PaperPilot.Core;

/// <summary>
/// Splits one scroll into small wheel steps that follow an ease-in-out curve, so the page
/// glides instead of jumping. Pure math so it can be tested.
/// </summary>
public static class ScrollPlanner
{
    public const int StepIntervalMs = 16;

    /// <summary>Animation length for a scroll of this many pixels (300–500 ms per the spec).</summary>
    public static int DurationFor(int pixels) => Math.Clamp(300 + Math.Abs(pixels) / 3, 300, 500);

    /// <summary>
    /// Integer wheel deltas, one per step, summing exactly to <paramref name="totalDelta"/>.
    /// Each step is at most <paramref name="maxStep"/> in size (more steps are added if needed).
    /// </summary>
    public static IReadOnlyList<int> Plan(int totalDelta, int durationMs, int maxStep = 40)
    {
        if (totalDelta == 0) return Array.Empty<int>();
        int sign = Math.Sign(totalDelta);
        int total = Math.Abs(totalDelta);

        int steps = Math.Max(1, durationMs / StepIntervalMs);
        // Ease-in-out peaks at ~1.57x the average speed; keep the biggest step under maxStep
        // (big scrolls simply take a little longer).
        int minStepsForSize = (int)Math.Ceiling(total * 1.65 / Math.Max(1, maxStep));
        steps = Math.Max(steps, minStepsForSize);
        steps = Math.Min(steps, total); // never a step of 0 when we can avoid it

        var result = new List<int>(steps);
        int sent = 0;
        for (int i = 1; i <= steps; i++)
        {
            int target = (int)Math.Round(total * EaseInOut(i / (double)steps));
            int step = target - sent;
            if (step == 0) continue;
            result.Add(sign * step);
            sent = target;
        }
        return result;
    }

    /// <summary>Smooth start and stop (sine ease-in-out; peak speed ≈1.57× average), t in [0,1].</summary>
    public static double EaseInOut(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return 0.5 - Math.Cos(Math.PI * t) / 2;
    }
}
