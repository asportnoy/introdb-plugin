// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroDbPlugin.Tests;

using System;
using System.Linq;
using IntroDbPlugin.Services;
using Xunit;

public class TestSegmentPlanner
{
    private static readonly IntroDbSegment Intro = new(60, 90, 1, 3);
    private static readonly IntroDbSegment Recap = new(0, 45, 1, 3);
    private static readonly IntroDbSegment Outro = new(2800, 2900, 1, 3);

    [Fact]
    public void Build_ReturnsEnabledSegments_InOrder()
    {
        var result = Result(intro: Intro, recap: Recap, outro: Outro);

        var segments = SegmentPlanner.Build(result, SegmentToggles.All, null);

        Assert.Equal(
            new[] { PlannedSegmentKind.Recap, PlannedSegmentKind.Intro, PlannedSegmentKind.Outro },
            segments.Select(s => s.Kind));
        Assert.Equal(Ticks(60), segments[1].StartTicks);
        Assert.Equal(Ticks(90), segments[1].EndTicks);
    }

    [Fact]
    public void Build_RespectsToggles()
    {
        var result = Result(intro: Intro, recap: Recap, outro: Outro);

        var segments = SegmentPlanner.Build(result, new SegmentToggles(Intro: true, Recap: false, Outro: false), null);

        Assert.Equal(PlannedSegmentKind.Intro, Assert.Single(segments).Kind);
    }

    [Fact]
    public void Build_TrimsOutro_AtPostCreditsInsideIt()
    {
        var result = Result(outro: Outro, postCredits: new IntroDbSegment(2850, 2900, 1, 1));

        var outro = Assert.Single(SegmentPlanner.Build(result, SegmentToggles.All, null));

        Assert.Equal(Ticks(2800), outro.StartTicks);
        Assert.Equal(Ticks(2850), outro.EndTicks);
    }

    [Theory]
    [InlineData(2900, 2950)]
    [InlineData(2700, 2750)]
    [InlineData(2800, 2810)]
    public void Build_KeepsOutro_WhenPostCreditsIsNotInsideIt(double start, double end)
    {
        var result = Result(outro: Outro, postCredits: new IntroDbSegment(start, end, 1, 1));

        var outro = Assert.Single(SegmentPlanner.Build(result, SegmentToggles.All, null));

        Assert.Equal(Ticks(2900), outro.EndTicks);
    }

    [Fact]
    public void Build_ClampsEndToRuntime()
    {
        var result = Result(outro: Outro);

        var outro = Assert.Single(SegmentPlanner.Build(result, SegmentToggles.All, Ticks(2880)));

        Assert.Equal(Ticks(2880), outro.EndTicks);
    }

    [Fact]
    public void Build_DropsSegmentsStartingAfterRuntime()
    {
        var result = Result(intro: Intro, outro: Outro);

        var segments = SegmentPlanner.Build(result, SegmentToggles.All, Ticks(1200));

        Assert.Equal(PlannedSegmentKind.Intro, Assert.Single(segments).Kind);
    }

    [Fact]
    public void Build_DropsInvalidSegments()
    {
        var result = Result(intro: new IntroDbSegment(90, 60, 1, 1), recap: new IntroDbSegment(-5, 10, 1, 1));

        Assert.Empty(SegmentPlanner.Build(result, SegmentToggles.All, null));
    }

    [Fact]
    public void Build_IgnoresUnknownRuntime()
    {
        var result = Result(outro: Outro);

        var outro = Assert.Single(SegmentPlanner.Build(result, SegmentToggles.All, 0));

        Assert.Equal(Ticks(2900), outro.EndTicks);
    }

    private static long Ticks(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

    private static IntroDbSegmentsResult Result(
        IntroDbSegment? intro = null,
        IntroDbSegment? recap = null,
        IntroDbSegment? outro = null,
        IntroDbSegment? postCredits = null)
        => new(intro, recap, outro, postCredits, false, false, false);
}
