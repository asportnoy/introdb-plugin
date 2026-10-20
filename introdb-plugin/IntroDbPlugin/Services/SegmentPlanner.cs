// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;

namespace IntroDbPlugin.Services;

/// <summary>
/// Kind of segment produced by <see cref="SegmentPlanner"/>.
/// </summary>
public enum PlannedSegmentKind
{
    /// <summary>Opening intro.</summary>
    Intro,

    /// <summary>"Previously on" recap.</summary>
    Recap,

    /// <summary>End credits.</summary>
    Outro
}

/// <summary>
/// Which segment types to produce.
/// </summary>
/// <param name="Intro">Produce intros.</param>
/// <param name="Recap">Produce recaps.</param>
/// <param name="Outro">Produce end credits.</param>
public sealed record SegmentToggles(bool Intro, bool Recap, bool Outro)
{
    /// <summary>
    /// Gets toggles with every type enabled.
    /// </summary>
    public static SegmentToggles All { get; } = new(true, true, true);
}

/// <summary>
/// A validated segment ready to store, in ticks.
/// </summary>
/// <param name="Kind">Segment kind.</param>
/// <param name="StartTicks">Start position in ticks.</param>
/// <param name="EndTicks">End position in ticks.</param>
public sealed record PlannedSegment(PlannedSegmentKind Kind, long StartTicks, long EndTicks);

/// <summary>
/// Turns an IntroDB response into the segments to store for an item.
/// </summary>
public static class SegmentPlanner
{
    /// <summary>
    /// Builds the segments to store for an item.
    /// </summary>
    /// <param name="result">IntroDB response.</param>
    /// <param name="toggles">Enabled segment types.</param>
    /// <param name="runtimeTicks">Item runtime in ticks, if known.</param>
    /// <returns>Valid segments, ordered by start.</returns>
    public static IReadOnlyList<PlannedSegment> Build(IntroDbSegmentsResult result, SegmentToggles toggles, long? runtimeTicks)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (toggles is null)
        {
            throw new ArgumentNullException(nameof(toggles));
        }

        var runtime = runtimeTicks > 0 ? runtimeTicks : null;
        var segments = new List<PlannedSegment>();

        if (toggles.Recap)
        {
            Add(segments, PlannedSegmentKind.Recap, result.Recap, null, runtime);
        }

        if (toggles.Intro)
        {
            Add(segments, PlannedSegmentKind.Intro, result.Intro, null, runtime);
        }

        if (toggles.Outro)
        {
            // Keep post-credits scenes watchable: end the credits where the scene starts.
            var postCreditsStart = result.PostCredits is null ? (long?)null : ToTicks(result.PostCredits.StartSeconds);
            Add(segments, PlannedSegmentKind.Outro, result.Outro, postCreditsStart, runtime);
        }

        segments.Sort((a, b) => a.StartTicks.CompareTo(b.StartTicks));
        return segments;
    }

    private static void Add(
        List<PlannedSegment> segments,
        PlannedSegmentKind kind,
        IntroDbSegment? segment,
        long? cutAtTicks,
        long? runtimeTicks)
    {
        if (segment is null)
        {
            return;
        }

        var start = ToTicks(segment.StartSeconds);
        var end = ToTicks(segment.EndSeconds);

        if (start < 0 || end <= start)
        {
            return;
        }

        if (cutAtTicks > start && cutAtTicks < end)
        {
            end = cutAtTicks.Value;
        }

        if (runtimeTicks.HasValue)
        {
            if (start >= runtimeTicks.Value)
            {
                return;
            }

            end = Math.Min(end, runtimeTicks.Value);
        }

        segments.Add(new PlannedSegment(kind, start, end));
    }

    private static long ToTicks(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
}
