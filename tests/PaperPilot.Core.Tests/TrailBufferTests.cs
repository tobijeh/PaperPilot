using PaperPilot.Core;

namespace PaperPilot.Core.Tests;

public class TrailBufferTests
{
    [Fact]
    public void New_level_appears_behind_the_plane_and_drifts_back()
    {
        var t = new TrailBuffer(5);
        t.Push(0.8);
        t.Tick();
        Assert.Equal(0.8, t[0], 3);
        t.Tick();
        Assert.Equal(0.8, t[1], 3);
        Assert.True(t[0] < 0.8); // falls away gently
    }

    [Fact]
    public void Loudest_level_between_ticks_wins()
    {
        var t = new TrailBuffer();
        t.Push(0.2);
        t.Push(0.9);
        t.Push(0.4);
        t.Tick();
        Assert.Equal(0.9, t[0], 3);
    }

    [Fact]
    public void Goes_silent_after_speech_stops()
    {
        var t = new TrailBuffer(10);
        t.Push(1);
        for (int i = 0; i < 60; i++) t.Tick();
        Assert.True(t.IsSilent);
    }

    [Fact]
    public void Levels_are_clamped() 
    {
        var t = new TrailBuffer();
        t.Push(3);
        t.Tick();
        Assert.Equal(1, t[0], 3);
    }
}
