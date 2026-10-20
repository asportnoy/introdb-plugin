// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

#if JELLYFIN_10_11_OR_GREATER
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace IntroDbPlugin.Api;

/// <summary>
/// Endpoints for removing IntroDB's segments from library items.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/IntroDB/Segments")]
[Produces(MediaTypeNames.Application.Json)]
public class IntroDbSegmentsController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private readonly ILogger<IntroDbSegmentsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbSegmentsController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSegmentManager">Media segment manager.</param>
    /// <param name="logger">Logger.</param>
    public IntroDbSegmentsController(
        ILibraryManager libraryManager,
        IMediaSegmentManager mediaSegmentManager,
        ILogger<IntroDbSegmentsController> logger)
    {
        _libraryManager = libraryManager;
        _mediaSegmentManager = mediaSegmentManager;
        _logger = logger;
    }

    /// <summary>
    /// Deletes segments created by IntroDB from the given items. Segments from other providers are kept.
    /// </summary>
    /// <param name="request">Items to clean up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Summary of removed segments.</returns>
    [HttpDelete]
    public async Task<ActionResult<DeleteSegmentsResult>> DeleteSegments(
        [FromBody, Required] DeleteSegmentsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var itemIds = (request.ItemIds ?? Array.Empty<Guid>()).Where(id => id != Guid.Empty).Distinct().ToList();
        var results = new List<ItemResult>();

        foreach (var itemId in itemIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                _logger.LogWarning("IntroDB could not resolve item {ItemId}; skipping segment removal", itemId);
                continue;
            }

            try
            {
                var removed = await RemoveIntroDbSegmentsAsync(item).ConfigureAwait(false);
                results.Add(new ItemResult(item.Id, item.Name, removed));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "IntroDB failed to delete segments for {Name} ({Id})", item.Name, item.Id);
            }
        }

        return Ok(new DeleteSegmentsResult(itemIds.Count, results.Sum(r => r.SegmentsRemoved), results));
    }

    private static bool IsIntroDbProvider(string? name)
        => string.Equals(name, Plugin.PluginName, StringComparison.OrdinalIgnoreCase);

    private async Task<int> RemoveIntroDbSegmentsAsync(BaseItem item)
    {
        var providers = _mediaSegmentManager.GetSupportedProviders(item).ToArray();
        if (!providers.Any(p => IsIntroDbProvider(p.Name)))
        {
            return 0;
        }

        // Disable every other provider (by id and name, which different server versions match on)
        // so the provider-filtered query returns only IntroDB's segments.
        var introDbOnly = new LibraryOptions
        {
            DisabledMediaSegmentProviders = providers
                .Where(p => !IsIntroDbProvider(p.Name))
                .SelectMany(p => new[] { p.Id, p.Name })
                .ToArray()
        };

        var segments = (await _mediaSegmentManager
            .GetSegmentsAsync(item, null, introDbOnly, true)
            .ConfigureAwait(false)).ToArray();

        foreach (var segment in segments)
        {
            await _mediaSegmentManager.DeleteSegmentAsync(segment.Id).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "IntroDB removed {Count} of its own segment(s) for {Name} ({Id}); other providers' segments left intact",
            segments.Length,
            item.Name,
            item.Id);

        return segments.Length;
    }

    /// <summary>
    /// Items to remove IntroDB segments from.
    /// </summary>
    public sealed class DeleteSegmentsRequest
    {
        /// <summary>
        /// Gets or sets the item ids.
        /// </summary>
        public IReadOnlyList<Guid>? ItemIds { get; set; }
    }

    /// <summary>
    /// Segments removed from one item.
    /// </summary>
    /// <param name="Id">Item id.</param>
    /// <param name="Name">Item name.</param>
    /// <param name="SegmentsRemoved">Number of segments removed.</param>
    public sealed record ItemResult(Guid Id, string Name, int SegmentsRemoved);

    /// <summary>
    /// Summary of a removal.
    /// </summary>
    /// <param name="TotalItems">Number of items requested.</param>
    /// <param name="TotalSegmentsRemoved">Number of segments removed.</param>
    /// <param name="Results">Per-item results.</param>
    public sealed record DeleteSegmentsResult(int TotalItems, int TotalSegmentsRemoved, IReadOnlyList<ItemResult> Results);
}
#endif
