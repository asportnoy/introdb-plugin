// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroDbPlugin.Configuration;
using IntroDbPlugin.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
#if JELLYFIN_10_11_OR_GREATER
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.MediaSegments;
#else
using Jellyfin.Data.Enums;
#endif

namespace IntroDbPlugin.Providers;

/// <summary>
/// IntroDB media segment provider.
/// </summary>
public class IntroDbSegmentProvider : IMediaSegmentProvider
{
    private readonly ILibraryManager _libraryManager;
    private readonly IntroDbClient _introDbClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IntroDbSegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbSegmentProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="introDbClient">IntroDB client.</param>
    /// <param name="serviceProvider">Service provider, used to resolve the segment manager lazily.</param>
    /// <param name="logger">Logger.</param>
    public IntroDbSegmentProvider(
        ILibraryManager libraryManager,
        IntroDbClient introDbClient,
        IServiceProvider serviceProvider,
        ILogger<IntroDbSegmentProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(introDbClient);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _libraryManager = libraryManager;
        _introDbClient = introDbClient;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _logger.LogInformation("IntroDB segment provider constructed");
    }

    /// <inheritdoc />
    public string Name => Plugin.PluginName;

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogDebug("GetMediaSegments called for ItemId={ItemId}", request.ItemId);

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null)
        {
            _logger.LogWarning("Early exit: item not found for ItemId={ItemId}", request.ItemId);
            return Array.Empty<MediaSegmentDto>();
        }

        if (item is not (Episode or Movie))
        {
            return Array.Empty<MediaSegmentDto>();
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        if (!ItemFilter.IsAllowed(item, config, _libraryManager))
        {
            _logger.LogDebug(
                "Skipping {Name}: item does not match selected filters ({SelectedShowCount} shows, {SelectedLibraryCount} libraries)",
                item.Name,
                config.GetSelectedShowIds().Count,
                config.GetSelectedLibraryIds().Count);
            return GetExistingSegments(request);
        }

        var failure = MediaIdentity.TryCreateLookup(item, out var lookup);
        if (failure == MediaIdentity.Failure.MissingImdbId)
        {
            _logger.LogWarning("Early exit: no ImdbId for {Name}", item.Name);
            return Array.Empty<MediaSegmentDto>();
        }

        if (failure == MediaIdentity.Failure.MissingEpisodeNumbers)
        {
            _logger.LogWarning("Early exit: TV episode missing season/episode for {Name}", item.Name);
            return Array.Empty<MediaSegmentDto>();
        }

        if (failure != MediaIdentity.Failure.None || lookup is null)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        if (item is Episode episode)
        {
            _logger.LogDebug(
                "Episode: Name={Name}, Series={Series}, S{Season}E{Episode}, ImdbId={ImdbId}",
                item.Name,
                episode.SeriesName,
                lookup.Season,
                lookup.Episode,
                lookup.ImdbId);
        }
        else
        {
            _logger.LogDebug("Movie: Name={Name}, ImdbId={ImdbId}", item.Name, lookup.ImdbId);
        }

        // Resolved lazily: the segment manager depends on the providers, so injecting it would be circular.
        if (config.IgnoreMediaWithExistingSegments
            && _serviceProvider.GetService<IMediaSegmentManager>() is { } segmentManager
            && segmentManager.HasSegments(request.ItemId))
        {
            _logger.LogDebug("Skipping {Name}: already has segments (IgnoreMediaWithExistingSegments enabled)", item.Name);
            return GetExistingSegments(request);
        }

        _logger.LogInformation(
            "Fetching from IntroDB API: imdbId={ImdbId}, isMovie={IsMovie}, season={Season}, episode={Episode}",
            lookup.ImdbId,
            lookup.IsMovie,
            lookup.Season,
            lookup.Episode);

        var fetch = await _introDbClient.GetSegmentsAsync(lookup, cancellationToken).ConfigureAwait(false);
        switch (fetch.Status)
        {
            case IntroDbFetchStatus.Error:
                _logger.LogWarning("IntroDB API request was an error for {Name}; preserving existing segments", item.Name);
                return GetExistingSegments(request);
            case IntroDbFetchStatus.RateLimited:
                _logger.LogDebug("IntroDB API request was rate limited for {Name}; preserving existing segments", item.Name);
                return GetExistingSegments(request);
            case IntroDbFetchStatus.NotFound when fetch.FromCache:
                _logger.LogDebug("Skipping {Name}: known not found in IntroDB (cached)", item.Name);
                return Array.Empty<MediaSegmentDto>();
            case IntroDbFetchStatus.NotFound:
                _logger.LogInformation("IntroDB API returned no data for {Name}", item.Name);
                return Array.Empty<MediaSegmentDto>();
        }

        var segments = SegmentPlanner
            .Build(fetch.Segments!, config.GetSegmentToggles(), item.RunTimeTicks)
            .Select(segment => new MediaSegmentDto
            {
                ItemId = request.ItemId,
                StartTicks = segment.StartTicks,
                EndTicks = segment.EndTicks,
                Type = ToMediaSegmentType(segment.Kind)
            })
            .ToList();

        _logger.LogInformation("Returning {Count} segments for {Name}", segments.Count, item.Name);
        return segments;
    }

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item)
    {
        var supported = item is Episode or Movie;
        _logger.LogTrace("Supports({Name}, {Type}): {Supported}", item?.Name ?? "null", item?.GetType().Name ?? "null", supported);
        return ValueTask.FromResult(supported);
    }

#if JELLYFIN_12_OR_GREATER
    /// <inheritdoc />
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
    {
        // IntroDB does not cache any analysis data locally, so cleanup is a no-op.
        _logger.LogDebug("CleanupExtractedData called for ItemId={ItemId}: no-op", itemId);
        return Task.CompletedTask;
    }
#endif

    private static IReadOnlyList<MediaSegmentDto> GetExistingSegments(MediaSegmentGenerationRequest request)
    {
#if JELLYFIN_10_11_OR_GREATER
        return request.ExistingSegments?.ToList() ?? (IReadOnlyList<MediaSegmentDto>)Array.Empty<MediaSegmentDto>();
#else
        return Array.Empty<MediaSegmentDto>();
#endif
    }

    private static MediaSegmentType ToMediaSegmentType(PlannedSegmentKind kind) => kind switch
    {
        PlannedSegmentKind.Intro => MediaSegmentType.Intro,
        PlannedSegmentKind.Recap => MediaSegmentType.Recap,
        PlannedSegmentKind.Outro => MediaSegmentType.Outro,
        _ => MediaSegmentType.Unknown
    };
}
