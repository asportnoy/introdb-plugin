// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using IntroDbPlugin.Services;
using MediaBrowser.Model.Plugins;

namespace IntroDbPlugin.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        EnableIntro = true;
        EnableRecap = true;
        EnableCredits = true;
        SelectedLibraryIds = string.Empty;
        SelectedShowIds = string.Empty;
        IgnoreMediaWithExistingSegments = false;
        EnableOnDemandFetch = true;
    }

    /// <summary>
    /// Gets or sets a value indicating whether intro segments are fetched.
    /// </summary>
    public bool EnableIntro { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether recap segments are fetched.
    /// </summary>
    public bool EnableRecap { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether end-credits (outro) segments are fetched.
    /// </summary>
    public bool EnableCredits { get; set; }

    /// <summary>
    /// Gets or sets a comma-separated list of library ids to limit fetching to. Empty means all.
    /// </summary>
    public string SelectedLibraryIds { get; set; }

    /// <summary>
    /// Gets or sets a comma-separated list of series or movie ids to limit fetching to. Empty means all.
    /// </summary>
    public string SelectedShowIds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether items that already have segments (from any provider) are skipped.
    /// </summary>
    public bool IgnoreMediaWithExistingSegments { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether segments are fetched when items are added or start playing.
    /// </summary>
    public bool EnableOnDemandFetch { get; set; }

    /// <summary>
    /// Gets the enabled segment types.
    /// </summary>
    /// <returns>Segment toggles.</returns>
    public SegmentToggles GetSegmentToggles() => new(EnableIntro, EnableRecap, EnableCredits);

    /// <summary>
    /// Parses <see cref="SelectedLibraryIds"/>.
    /// </summary>
    /// <returns>Selected library ids.</returns>
    public IReadOnlyCollection<Guid> GetSelectedLibraryIds() => ParseIds(SelectedLibraryIds);

    /// <summary>
    /// Parses <see cref="SelectedShowIds"/>.
    /// </summary>
    /// <returns>Selected series or movie ids.</returns>
    public IReadOnlyCollection<Guid> GetSelectedShowIds() => ParseIds(SelectedShowIds);

    private static HashSet<Guid> ParseIds(string? csv)
    {
        return new HashSet<Guid>(
            (csv ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => Guid.TryParse(id.Trim(), out var guid) ? guid : Guid.Empty)
                .Where(guid => guid != Guid.Empty));
    }
}
