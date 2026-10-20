// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

#if EMBY
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroDbPlugin.Configuration;
using IntroDbPlugin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace IntroDbPlugin.Emby;

/// <summary>
/// Scheduled task to fetch intro timestamps from IntroDB and store them.
/// </summary>
public class IntroDbScheduledTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IItemRepository _itemRepository;
    private readonly ILogger _logger;
    private readonly IntroDbClient _introDbClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbScheduledTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="itemRepository">Item repository for chapter storage.</param>
    /// <param name="logManager">Log manager.</param>
    public IntroDbScheduledTask(ILibraryManager libraryManager, IItemRepository itemRepository, ILogManager logManager)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
        _logger = logManager?.GetLogger(Name) ?? throw new ArgumentNullException(nameof(logManager));

        var httpClient = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(IntroDbClient.DefaultTimeoutSeconds)
        };
        _introDbClient = new IntroDbClient(httpClient, new EmbyLoggerAdapter<IntroDbClient>(logManager));
    }

    /// <inheritdoc />
    public string Name => "IntroDB Segment Fetcher";

    /// <inheritdoc />
    public string Key => "IntroDbIntroFetcher";

    /// <inheritdoc />
    public string Description => "Fetches intro and end-credits markers from IntroDB for TV episodes and movies.";

    /// <inheritdoc />
    public string Category => "IntroDB";

    /// <inheritdoc />
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { nameof(Episode), nameof(Movie) },
            IsVirtualItem = false,
            Recursive = true
        }).Where(i => i is Episode || i is Movie).ToList();

        var total = items.Count;
        var current = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ProcessItemAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error processing {0}", ex, item.Name);
            }

            current++;
            progress.Report((double)current / Math.Max(total, 1) * 100);
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        };
    }

    private async Task ProcessItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (MediaIdentity.TryCreateLookup(item, out var lookup) != MediaIdentity.Failure.None || lookup is null)
        {
            return;
        }

        var fetch = await _introDbClient
            .GetSegmentsAsync(lookup, cancellationToken)
            .ConfigureAwait(false);

        if (fetch.Status != IntroDbFetchStatus.Ok || fetch.Segments == null)
        {
            return;
        }

        var result = fetch.Segments;

        // Emby has no recap marker; intros map to IntroStart/IntroEnd and the outro to CreditsStart.
        var config = EmbyPlugin.Instance?.Configuration ?? new PluginConfiguration();
        var toggles = new SegmentToggles(config.EnableIntro, false, config.EnableCredits);
        var segments = SegmentPlanner.Build(result, toggles, item.RunTimeTicks);
        if (segments.Count == 0)
        {
            return;
        }

        // Get existing chapters from repository
        var chapters = _itemRepository.GetChapters(item)?.ToList() ?? new List<ChapterInfo>();

        // Replace the marker types we manage (and legacy named markers)
        var intro = segments.FirstOrDefault(s => s.Kind == PlannedSegmentKind.Intro);
        var outro = segments.FirstOrDefault(s => s.Kind == PlannedSegmentKind.Outro);
        chapters.RemoveAll(c =>
            (intro != null && (c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd)) ||
            (outro != null && c.MarkerType == MarkerType.CreditsStart) ||
            c.Name?.StartsWith("[IntroDB]", StringComparison.Ordinal) == true);

        if (intro != null)
        {
            chapters.Add(new ChapterInfo { StartPositionTicks = intro.StartTicks, MarkerType = MarkerType.IntroStart });
            chapters.Add(new ChapterInfo { StartPositionTicks = intro.EndTicks, MarkerType = MarkerType.IntroEnd });
        }

        if (outro != null)
        {
            chapters.Add(new ChapterInfo { StartPositionTicks = outro.StartTicks, MarkerType = MarkerType.CreditsStart });
        }

        // Sort by position
        chapters = chapters.OrderBy(c => c.StartPositionTicks).ToList();

        // Save chapters back to repository
        _itemRepository.SaveChapters(item.InternalId, chapters);

        _logger.Info("Updated IntroDB markers for {0} ({1})", item.Name, lookup);
    }
}
#endif
