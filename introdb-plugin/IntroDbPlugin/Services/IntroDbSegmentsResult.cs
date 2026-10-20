// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroDbPlugin.Services;

/// <summary>
/// A single segment's aggregated timestamps from IntroDB.
/// </summary>
/// <param name="StartSeconds">Segment start in seconds.</param>
/// <param name="EndSeconds">Segment end in seconds.</param>
/// <param name="Confidence">Confidence score.</param>
/// <param name="SubmissionCount">Number of submissions.</param>
public sealed record IntroDbSegment(
    double StartSeconds,
    double EndSeconds,
    double Confidence,
    int SubmissionCount);

/// <summary>
/// All segments IntroDB has for one episode or movie.
/// </summary>
/// <param name="Intro">Intro segment, if any.</param>
/// <param name="Recap">Recap segment, if any.</param>
/// <param name="Outro">End-credits segment, if any.</param>
/// <param name="PostCredits">Post-credits scene, if any.</param>
/// <param name="NoIntro">Whether the community confirmed there is no intro.</param>
/// <param name="NoRecap">Whether the community confirmed there is no recap.</param>
/// <param name="NoOutro">Whether the community confirmed there are no end credits.</param>
public sealed record IntroDbSegmentsResult(
    IntroDbSegment? Intro,
    IntroDbSegment? Recap,
    IntroDbSegment? Outro,
    IntroDbSegment? PostCredits,
    bool NoIntro,
    bool NoRecap,
    bool NoOutro);

/// <summary>
/// Outcome of an IntroDB lookup.
/// </summary>
public enum IntroDbFetchStatus
{
    /// <summary>IntroDB answered with segment data (which may still be empty).</summary>
    Ok,

    /// <summary>IntroDB has nothing for this item (404, or cached as such).</summary>
    NotFound,

    /// <summary>Still rate limited after retries.</summary>
    RateLimited,

    /// <summary>The request failed or the response could not be used.</summary>
    Error
}

/// <summary>
/// Result of an IntroDB lookup.
/// </summary>
/// <param name="Status">Outcome.</param>
/// <param name="Segments">Segments, when <see cref="IntroDbFetchStatus.Ok"/>.</param>
/// <param name="FromCache">Whether the not-found cache answered without a request.</param>
public sealed record IntroDbFetchResult(IntroDbFetchStatus Status, IntroDbSegmentsResult? Segments, bool FromCache = false)
{
    /// <summary>Gets a not-found result.</summary>
    public static IntroDbFetchResult NotFound { get; } = new(IntroDbFetchStatus.NotFound, null);

    /// <summary>Gets a not-found result answered by the cache.</summary>
    public static IntroDbFetchResult CachedNotFound { get; } = new(IntroDbFetchStatus.NotFound, null, true);

    /// <summary>Gets a rate-limited result.</summary>
    public static IntroDbFetchResult RateLimited { get; } = new(IntroDbFetchStatus.RateLimited, null);

    /// <summary>Gets an error result.</summary>
    public static IntroDbFetchResult Error { get; } = new(IntroDbFetchStatus.Error, null);
}

/// <summary>
/// Identifies the episode or movie to look up.
/// </summary>
/// <param name="ImdbId">IMDb id (of the series for episodes).</param>
/// <param name="IsMovie">Whether this is a movie.</param>
/// <param name="Season">Season number (episodes only).</param>
/// <param name="Episode">Episode number (episodes only).</param>
public sealed record IntroDbLookup(string ImdbId, bool IsMovie, int? Season, int? Episode)
{
    /// <summary>
    /// Gets the not-found cache key for this lookup.
    /// </summary>
    public string CacheKey => IsMovie ? $"movie:{ImdbId}" : $"tv:{ImdbId}:{Season}:{Episode}";

    /// <summary>
    /// Creates a lookup for a TV episode.
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <returns>The lookup.</returns>
    public static IntroDbLookup ForEpisode(string imdbId, int season, int episode) => new(imdbId, false, season, episode);

    /// <summary>
    /// Creates a lookup for a movie.
    /// </summary>
    /// <param name="imdbId">Movie IMDb id.</param>
    /// <returns>The lookup.</returns>
    public static IntroDbLookup ForMovie(string imdbId) => new(imdbId, true, null, null);

    /// <inheritdoc />
    public override string ToString() => IsMovie ? $"{ImdbId} (movie)" : $"{ImdbId} S{Season}E{Episode}";
}
