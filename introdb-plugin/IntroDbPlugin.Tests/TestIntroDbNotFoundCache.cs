// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroDbPlugin.Tests;

using System;
using System.IO;
using IntroDbPlugin.Services;
using Xunit;

public sealed class TestIntroDbNotFoundCache : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "introdb-cache-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private string CachePath => Path.Combine(_directory, "notfound-cache.json");

    [Fact]
    public void Flush_PersistsEntries_AcrossInstances()
    {
        var cache = new IntroDbNotFoundCache(CachePath, null, () => _now);
        cache.Remember("tv:tt1:1:1", TimeSpan.FromHours(1));
        cache.Flush();

        var reloaded = new IntroDbNotFoundCache(CachePath, null, () => _now);

        Assert.True(reloaded.TryGetHit("tv:tt1:1:1"));
        Assert.Equal(1, reloaded.Count);
    }

    [Fact]
    public void Load_DropsExpiredEntries()
    {
        var cache = new IntroDbNotFoundCache(CachePath, null, () => _now);
        cache.Remember("tv:tt1:1:1", TimeSpan.FromHours(1));
        cache.Flush();

        _now = _now.AddHours(2);
        var reloaded = new IntroDbNotFoundCache(CachePath, null, () => _now);

        Assert.False(reloaded.TryGetHit("tv:tt1:1:1"));
        Assert.Equal(0, reloaded.Count);
    }

    [Fact]
    public void Remember_FlushesAutomatically_AfterThreshold()
    {
        var cache = new IntroDbNotFoundCache(CachePath, null, () => _now);
        for (var i = 0; i < 25; i++)
        {
            cache.Remember("tv:tt1:1:" + i, TimeSpan.FromHours(1));
        }

        Assert.True(File.Exists(CachePath));
    }

    [Fact]
    public void Clear_RemovesEntriesAndFile()
    {
        var cache = new IntroDbNotFoundCache(CachePath, null, () => _now);
        cache.Remember("movie:tt2", TimeSpan.FromHours(1));
        cache.Flush();

        Assert.Equal(1, cache.Clear());
        Assert.False(cache.TryGetHit("movie:tt2"));
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public void Load_StartsEmpty_WhenFileIsCorrupt()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(CachePath, "not json");

        var cache = new IntroDbNotFoundCache(CachePath, null, () => _now);

        Assert.Equal(0, cache.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
