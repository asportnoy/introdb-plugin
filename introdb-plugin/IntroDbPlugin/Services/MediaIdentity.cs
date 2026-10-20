// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace IntroDbPlugin.Services;

/// <summary>
/// Works out the IntroDB lookup (IMDb id, season, episode) for a library item.
/// </summary>
public static class MediaIdentity
{
    private const string ImdbIdPattern = @"\btt\d{7,8}\b";
    private const string SeasonEpisodePattern = @"S(?<season>\d{1,2})E(?<episode>\d{1,3})";

#if EMBY
    private static readonly string ImdbProviderKey = MetadataProviders.Imdb.ToString();
#else
    private static readonly string ImdbProviderKey = MetadataProvider.Imdb.ToString();
#endif

    private static readonly Regex ImdbIdRegex = new(ImdbIdPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeasonEpisodeRegex = new(SeasonEpisodePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Why a lookup could not be built.
    /// </summary>
    public enum Failure
    {
        /// <summary>The lookup was built.</summary>
        None,

        /// <summary>The item is not an episode or movie.</summary>
        UnsupportedItem,

        /// <summary>No IMDb id on the item, its series, or its path.</summary>
        MissingImdbId,

        /// <summary>No usable season/episode numbers.</summary>
        MissingEpisodeNumbers
    }

    /// <summary>
    /// Builds the IntroDB lookup for an item.
    /// </summary>
    /// <param name="item">Episode or movie.</param>
    /// <param name="lookup">The lookup, when successful.</param>
    /// <returns>Why it failed, or <see cref="Failure.None"/>.</returns>
    public static Failure TryCreateLookup(BaseItem item, out IntroDbLookup? lookup)
    {
        lookup = null;

        if (item is Movie movie)
        {
            if (!TryGetImdbId(movie.ProviderIds, movie.Path, out var movieImdbId))
            {
                return Failure.MissingImdbId;
            }

            lookup = IntroDbLookup.ForMovie(movieImdbId);
            return Failure.None;
        }

        if (item is not Episode episode)
        {
            return Failure.UnsupportedItem;
        }

        // Prefer the series id: IntroDB keys TV episodes by the series' IMDb id.
        if (!TryGetImdbId(episode.Series?.ProviderIds, null, out var imdbId)
            && !TryGetImdbId(episode.ProviderIds, episode.Path, out imdbId))
        {
            return Failure.MissingImdbId;
        }

        if (!TryGetSeasonEpisodeNumbers(episode, out var season, out var episodeNumber))
        {
            return Failure.MissingEpisodeNumbers;
        }

        lookup = IntroDbLookup.ForEpisode(imdbId, season, episodeNumber);
        return Failure.None;
    }

    private static bool TryGetImdbId(IDictionary<string, string>? providerIds, string? path, out string imdbId)
    {
        if (providerIds is not null
            && providerIds.TryGetValue(ImdbProviderKey, out var providerImdbId)
            && !string.IsNullOrWhiteSpace(providerImdbId))
        {
            imdbId = providerImdbId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            var match = ImdbIdRegex.Match(path);
            if (match.Success)
            {
                imdbId = match.Value;
                return true;
            }
        }

        imdbId = string.Empty;
        return false;
    }

    private static bool TryGetSeasonEpisodeNumbers(Episode episode, out int seasonNumber, out int episodeNumber)
    {
#if EMBY
        seasonNumber = episode.ParentIndexNumber ?? 0;
#else
        seasonNumber = episode.AiredSeasonNumber ?? episode.ParentIndexNumber ?? 0;
#endif
        episodeNumber = episode.IndexNumber ?? 0;

        if (seasonNumber > 0 && episodeNumber > 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(episode.Path))
        {
            var match = SeasonEpisodeRegex.Match(episode.Path);
            if (match.Success &&
                int.TryParse(match.Groups["season"].Value, out var parsedSeason) &&
                int.TryParse(match.Groups["episode"].Value, out var parsedEpisode))
            {
                seasonNumber = parsedSeason;
                episodeNumber = parsedEpisode;
            }
        }

        return seasonNumber > 0 && episodeNumber > 0;
    }
}
