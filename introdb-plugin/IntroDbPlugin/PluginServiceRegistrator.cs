// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using IntroDbPlugin.Providers;
using IntroDbPlugin.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
#if JELLYFIN_10_11_OR_GREATER
using MediaBrowser.Controller.MediaSegments;
#endif

namespace IntroDbPlugin;

/// <summary>
/// Register IntroDB services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(sp =>
        {
            var dataFolder = Plugin.Instance?.DataFolderPath;
            var cachePath = string.IsNullOrEmpty(dataFolder) ? null : Path.Combine(dataFolder, "notfound-cache.json");
            return new IntroDbNotFoundCache(cachePath, sp.GetRequiredService<ILogger<IntroDbNotFoundCache>>());
        });
        serviceCollection.AddSingleton(sp =>
        {
            var httpClient = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(IntroDbClient.DefaultTimeoutSeconds)
            };

            return new IntroDbClient(
                httpClient,
                sp.GetRequiredService<ILogger<IntroDbClient>>(),
                sp.GetRequiredService<IntroDbNotFoundCache>());
        });
        serviceCollection.AddSingleton<IMediaSegmentProvider, IntroDbSegmentProvider>();
        serviceCollection.AddHostedService<OnDemandFetchService>();
    }
}
