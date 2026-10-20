// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

#if JELLYFIN
using System;
using System.Linq;
using IntroDbPlugin.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace IntroDbPlugin.Services;

/// <summary>
/// Applies the configured library/show selection.
/// </summary>
public static class ItemFilter
{
    /// <summary>
    /// Checks whether an item is inside the configured selection. No selection allows everything.
    /// </summary>
    /// <param name="item">Episode or movie.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <returns>True if the item should be fetched.</returns>
    public static bool IsAllowed(BaseItem item, PluginConfiguration config, ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(libraryManager);

        var libraries = config.GetSelectedLibraryIds();
        var shows = config.GetSelectedShowIds();
        if (libraries.Count == 0 && shows.Count == 0)
        {
            return true;
        }

        if (shows.Contains(item.Id) || (item is Episode episode && shows.Contains(episode.SeriesId)))
        {
            return true;
        }

        if (libraries.Count == 0)
        {
            return false;
        }

        return libraryManager.GetItemList(new InternalItemsQuery
        {
            ItemIds = new[] { item.Id },
            AncestorIds = libraries.ToArray()
        }).Count > 0;
    }
}
#endif
