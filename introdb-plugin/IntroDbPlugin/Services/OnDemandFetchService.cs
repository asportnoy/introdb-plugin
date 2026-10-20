// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroDbPlugin.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
#if JELLYFIN_10_11_OR_GREATER
using MediaBrowser.Controller.MediaSegments;
#endif

namespace IntroDbPlugin.Services;

/// <summary>
/// Fetches segments for items when they are added to the library or start playing,
/// instead of waiting for the next Media Segment Scan.
/// </summary>
public sealed class OnDemandFetchService : IHostedService, IDisposable
{
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(10);

    // New items usually have no metadata (IMDb id) yet; give the metadata refresh time to finish.
    private static readonly TimeSpan ItemAddedDelay = TimeSpan.FromMinutes(2);

    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private readonly IntroDbNotFoundCache _notFoundCache;
    private readonly ILogger<OnDemandFetchService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="OnDemandFetchService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="mediaSegmentManager">Media segment manager.</param>
    /// <param name="notFoundCache">Not-found cache, flushed on shutdown.</param>
    /// <param name="logger">Logger.</param>
    public OnDemandFetchService(
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        IMediaSegmentManager mediaSegmentManager,
        IntroDbNotFoundCache notFoundCache,
        ILogger<OnDemandFetchService> logger)
    {
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _mediaSegmentManager = mediaSegmentManager;
        _notFoundCache = notFoundCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _sessionManager.PlaybackStart += OnPlaybackStart;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _cts.Cancel();

        var pending = _inFlight.Values.ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(ShutdownWait, cancellationToken)).ConfigureAwait(false);
        }

        _notFoundCache.Flush();
    }

    /// <inheritdoc />
    public void Dispose() => _cts.Dispose();

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) => Enqueue(e.Item, "item added", ItemAddedDelay);

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e) => Enqueue(e.Item, "playback start", TimeSpan.Zero);

    private void Enqueue(BaseItem? item, string trigger, TimeSpan delay)
    {
        if (item is not (Episode or Movie) || _cts.IsCancellationRequested)
        {
            return;
        }

        if (Plugin.Instance?.Configuration is not PluginConfiguration config || !config.EnableOnDemandFetch)
        {
            return;
        }

        // One fetch per item at a time; repeated triggers while it runs are dropped.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inFlight.TryAdd(item.Id, gate.Task))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                }

                // Re-read the item so metadata added since the event is used.
                var current = _libraryManager.GetItemById(item.Id) ?? item;
                await FetchAsync(current, config, trigger, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "On-demand segment fetch failed ({Trigger}) for {Name}", trigger, item.Name);
            }
            finally
            {
                _inFlight.TryRemove(item.Id, out _);
                gate.TrySetResult(true);
            }
        });
    }

    private async Task FetchAsync(BaseItem item, PluginConfiguration config, string trigger, CancellationToken cancellationToken)
    {
        if (!ItemFilter.IsAllowed(item, config, _libraryManager))
        {
            _logger.LogDebug("On-demand fetch skipped ({Trigger}): {Name} not in selected libraries/shows", trigger, item.Name);
            return;
        }

        if (_mediaSegmentManager.HasSegments(item.Id))
        {
            _logger.LogDebug("On-demand fetch skipped ({Trigger}): segments already exist for {Name}", trigger, item.Name);
            return;
        }

        _logger.LogInformation("On-demand segment fetch triggered ({Trigger}) for {Name} ({Type})", trigger, item.Name, item.GetType().Name);
#if JELLYFIN_10_11_OR_GREATER
        await _mediaSegmentManager
            .RunSegmentPluginProviders(item, _libraryManager.GetLibraryOptions(item), false, cancellationToken)
            .ConfigureAwait(false);
#else
        await _mediaSegmentManager
            .RunSegmentPluginProviders(item, false, cancellationToken)
            .ConfigureAwait(false);
#endif
        _logger.LogInformation("On-demand segment fetch completed ({Trigger}) for {Name}", trigger, item.Name);
    }
}
