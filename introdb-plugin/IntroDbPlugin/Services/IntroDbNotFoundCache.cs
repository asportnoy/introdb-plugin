// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace IntroDbPlugin.Services;

/// <summary>
/// Remembers lookups IntroDB has nothing for, so repeat scans don't ask again.
/// Optionally persisted to a JSON file.
/// </summary>
public sealed class IntroDbNotFoundCache
{
    /// <summary>
    /// How long a 404 is remembered.
    /// </summary>
    public static readonly TimeSpan NotFoundTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a community-confirmed absence is remembered.
    /// </summary>
    public static readonly TimeSpan ConfirmedAbsentTtl = TimeSpan.FromDays(14);

    private const int FlushThreshold = 25;

    private readonly string? _filePath;
    private readonly ILogger? _logger;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<string, DateTime> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _fileLock = new();
    private volatile bool _loaded;
    private int _unsavedChanges;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbNotFoundCache"/> class.
    /// </summary>
    /// <param name="filePath">JSON file to persist to, or null for memory only.</param>
    /// <param name="logger">Logger.</param>
    public IntroDbNotFoundCache(string? filePath = null, ILogger? logger = null)
        : this(filePath, logger, () => DateTime.UtcNow)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbNotFoundCache"/> class with an injectable clock.
    /// </summary>
    /// <param name="filePath">JSON file to persist to, or null for memory only.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="utcNow">Returns the current UTC time.</param>
    internal IntroDbNotFoundCache(string? filePath, ILogger? logger, Func<DateTime> utcNow)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
        _logger = logger;
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>
    /// Gets the number of unexpired entries.
    /// </summary>
    public int Count
    {
        get
        {
            EnsureLoaded();
            var now = _utcNow();
            return _entries.Count(e => e.Value > now);
        }
    }

    /// <summary>
    /// Checks whether a key is cached and unexpired.
    /// </summary>
    /// <param name="key">Lookup key.</param>
    /// <returns>True if the lookup should be skipped.</returns>
    public bool TryGetHit(string key)
    {
        EnsureLoaded();
        if (!_entries.TryGetValue(key, out var expiresUtc))
        {
            return false;
        }

        if (expiresUtc > _utcNow())
        {
            return true;
        }

        _entries.TryRemove(key, out _);
        return false;
    }

    /// <summary>
    /// Remembers that a lookup has nothing.
    /// </summary>
    /// <param name="key">Lookup key.</param>
    /// <param name="ttl">How long to remember it.</param>
    public void Remember(string key, TimeSpan ttl)
    {
        EnsureLoaded();
        _entries[key] = _utcNow() + ttl;
        if (Interlocked.Increment(ref _unsavedChanges) >= FlushThreshold)
        {
            Flush();
        }
    }

    /// <summary>
    /// Removes every entry, including the file.
    /// </summary>
    /// <returns>Number of entries removed.</returns>
    public int Clear()
    {
        lock (_fileLock)
        {
            _loaded = true;
            var count = _entries.Count;
            _entries.Clear();
            Interlocked.Exchange(ref _unsavedChanges, 0);

            if (_filePath is not null)
            {
                try
                {
                    File.Delete(_filePath);
                }
                catch (IOException ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete IntroDB not-found cache file {CachePath}", _filePath);
                }
            }

            return count;
        }
    }

    /// <summary>
    /// Writes unsaved changes to the file, if persistence is enabled.
    /// </summary>
    public void Flush()
    {
        if (_filePath is null)
        {
            Interlocked.Exchange(ref _unsavedChanges, 0);
            return;
        }

        lock (_fileLock)
        {
            if (Interlocked.Exchange(ref _unsavedChanges, 0) == 0 && File.Exists(_filePath))
            {
                return;
            }

            var now = _utcNow();
            var snapshot = _entries.Where(e => e.Value > now).ToDictionary(e => e.Key, e => e.Value);
            var tempPath = _filePath + ".tmp";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot));
                if (File.Exists(_filePath))
                {
                    File.Replace(tempPath, _filePath, null);
                }
                else
                {
                    File.Move(tempPath, _filePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Failed to save IntroDB not-found cache to {CachePath}", _filePath);
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_fileLock)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            if (_filePath is null || !File.Exists(_filePath))
            {
                return;
            }

            try
            {
                var stored = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_filePath));
                var now = _utcNow();
                foreach (var entry in stored ?? new Dictionary<string, DateTime>())
                {
                    if (entry.Value > now)
                    {
                        _entries.TryAdd(entry.Key, DateTime.SpecifyKind(entry.Value, DateTimeKind.Utc));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Failed to load IntroDB not-found cache from {CachePath}", _filePath);
            }
        }
    }
}
