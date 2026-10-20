// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Net.Mime;
using IntroDbPlugin.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IntroDbPlugin.Api;

/// <summary>
/// Endpoints for the IntroDB not-found cache.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/IntroDB/NotFoundCache")]
[Produces(MediaTypeNames.Application.Json)]
public class IntroDbCacheController : ControllerBase
{
    private readonly IntroDbNotFoundCache _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbCacheController"/> class.
    /// </summary>
    /// <param name="cache">Not-found cache.</param>
    public IntroDbCacheController(IntroDbNotFoundCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>
    /// Gets the number of cached not-found lookups.
    /// </summary>
    /// <returns>Cache status.</returns>
    [HttpGet]
    public ActionResult<CacheStatus> Get() => Ok(new CacheStatus(_cache.Count));

    /// <summary>
    /// Clears the not-found cache so every item is looked up again.
    /// </summary>
    /// <returns>Number of entries cleared.</returns>
    [HttpPost("Clear")]
    public ActionResult<CacheClearResult> Clear() => Ok(new CacheClearResult(_cache.Clear()));

    /// <summary>
    /// Not-found cache status.
    /// </summary>
    /// <param name="Entries">Number of unexpired entries.</param>
    public sealed record CacheStatus(int Entries);

    /// <summary>
    /// Result of clearing the cache.
    /// </summary>
    /// <param name="ClearedEntries">Number of entries removed.</param>
    public sealed record CacheClearResult(int ClearedEntries);
}
