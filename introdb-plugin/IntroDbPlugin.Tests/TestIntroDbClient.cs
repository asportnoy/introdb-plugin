// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroDbPlugin.Tests;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IntroDbPlugin.Services;
using Microsoft.Extensions.Logging;
using Xunit;

public class TestIntroDbClient
{
    private const string FullPayload = """
        {
          "imdb_id": "tt0903747", "media_type": "tv", "is_movie": false, "season": 1, "episode": 2,
          "intro": {"start_sec": 314.5, "end_sec": 331, "start_ms": 314500, "end_ms": 331000, "confidence": 1, "submission_count": 5},
          "recap": {"start_sec": 0, "end_sec": 42, "start_ms": 0, "end_ms": 42000, "confidence": 0.8, "submission_count": 2},
          "outro": {"start_sec": 2843, "end_sec": 2901, "start_ms": 2843000, "end_ms": 2901000, "confidence": 1, "submission_count": 1},
          "post_credits": null,
          "no_intro": false, "no_recap": false, "no_outro": false
        }
        """;

    private const string EmptyPayload = """
        {"imdb_id": "tt0903747", "season": 1, "episode": 2, "intro": null, "recap": null, "outro": null, "post_credits": null,
         "no_intro": false, "no_recap": false, "no_outro": false}
        """;

    private const string ConfirmedAbsentPayload = """
        {"imdb_id": "tt0903747", "season": 1, "episode": 2, "intro": null, "recap": null, "outro": null, "post_credits": null,
         "no_intro": true, "no_recap": true, "no_outro": true}
        """;

    private static readonly IntroDbLookup Episode = IntroDbLookup.ForEpisode("tt0903747", 1, 2);

    [Fact]
    public async Task GetSegmentsAsync_RequestsEpisodeSegments()
    {
        Uri? requested = null;
        var harness = new Harness(new StubHttpMessageHandler(request =>
        {
            requested = request.RequestUri;
            return Json(FullPayload);
        }));

        await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None);

        Assert.Equal("http://api.introdb.app/segments?imdb_id=tt0903747&season=1&episode=2", requested?.ToString());
    }

    [Fact]
    public async Task GetSegmentsAsync_RequestsMovieSegments()
    {
        Uri? requested = null;
        var harness = new Harness(new StubHttpMessageHandler(request =>
        {
            requested = request.RequestUri;
            return Json(EmptyPayload);
        }));

        await harness.Client.GetSegmentsAsync(IntroDbLookup.ForMovie("tt0133093"), CancellationToken.None);

        Assert.Equal("http://api.introdb.app/segments?imdb_id=tt0133093&is_movie=true", requested?.ToString());
    }

    [Fact]
    public async Task GetSegmentsAsync_ParsesAllSegmentTypes()
    {
        var harness = new Harness(new StubHttpMessageHandler(_ => Json(FullPayload)));

        var fetch = await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None);

        Assert.Equal(IntroDbFetchStatus.Ok, fetch.Status);
        var result = fetch.Segments!;
        Assert.Equal(new IntroDbSegment(314.5, 331, 1, 5), result.Intro);
        Assert.Equal(new IntroDbSegment(0, 42, 0.8, 2), result.Recap);
        Assert.Equal(new IntroDbSegment(2843, 2901, 1, 1), result.Outro);
        Assert.Null(result.PostCredits);
        Assert.False(result.NoIntro);
    }

    [Fact]
    public async Task GetSegmentsAsync_ReturnsError_WhenBadRequest()
    {
        var harness = new Harness(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)));

        Assert.Equal(IntroDbFetchStatus.Error, (await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task GetSegmentsAsync_CachesNotFound_ForOneDay()
    {
        var calls = 0;
        var harness = new Harness(new StubHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        Assert.Equal(IntroDbFetchStatus.NotFound, (await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None)).Status);
        var cached = await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None);
        Assert.Equal(IntroDbFetchStatus.NotFound, cached.Status);
        Assert.True(cached.FromCache);
        Assert.Equal(1, calls);

        harness.Now = harness.Now.AddHours(25);
        Assert.False((await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None)).FromCache);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetSegmentsAsync_CachesEmptyResponse_ForOneDay()
    {
        var calls = 0;
        var harness = new Harness(new StubHttpMessageHandler(_ =>
        {
            calls++;
            return Json(EmptyPayload);
        }));

        await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None);
        harness.Now = harness.Now.AddHours(23);
        await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None);
        Assert.Equal(1, calls);

        harness.Now = harness.Now.AddHours(2);
        await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetSegmentsAsync_CachesConfirmedAbsence_ForTwoWeeks()
    {
        var calls = 0;
        var harness = new Harness(new StubHttpMessageHandler(_ =>
        {
            calls++;
            return Json(ConfirmedAbsentPayload);
        }));

        await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None);
        harness.Now = harness.Now.AddDays(13);
        await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None);
        Assert.Equal(1, calls);

        harness.Now = harness.Now.AddDays(2);
        await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetSegmentsAsync_ReturnsError_WhenRequestThrows()
    {
        var harness = new Harness(new StubHttpMessageHandler(_ => throw new HttpRequestException("boom")));

        Assert.Equal(IntroDbFetchStatus.Error, (await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task GetSegmentsAsync_DoesNotCacheErrors()
    {
        var calls = 0;
        var harness = new Harness(new StubHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }));

        Assert.Equal(IntroDbFetchStatus.Error, (await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None)).Status);
        await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetSegmentsAsync_RetriesAfterRetryAfter_WhenRateLimited()
    {
        var calls = 0;
        var harness = new Harness(new StubHttpMessageHandler(_ =>
        {
            calls++;
            return calls == 1 ? RateLimited(TimeSpan.FromSeconds(2)) : Json(FullPayload);
        }));

        var fetch = await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None);

        Assert.Equal(IntroDbFetchStatus.Ok, fetch.Status);
        Assert.Equal(2, calls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, harness.CooldownDelays);
    }

    [Fact]
    public async Task GetSegmentsAsync_ReturnsRateLimited_WhenStillRateLimitedAfterRetries()
    {
        var calls = 0;
        var harness = new Harness(new StubHttpMessageHandler(_ =>
        {
            calls++;
            return RateLimited(null);
        }));

        var fetch = await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None);

        Assert.Equal(IntroDbFetchStatus.RateLimited, fetch.Status);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task GetSegmentsAsync_GrowsBackOff_WhenRepeatedlyRateLimitedWithoutHeader()
    {
        var harness = new Harness(new StubHttpMessageHandler(_ => RateLimited(null)));

        await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None);

        Assert.Equal(
            new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30) },
            harness.CooldownDelays);
    }

    [Fact]
    public async Task GetSegmentsAsync_HonoursRetryAfterDate_WhenRateLimited()
    {
        var calls = 0;
        Harness? harness = null;
        harness = new Harness(new StubHttpMessageHandler(_ =>
        {
            calls++;
            if (calls > 1)
            {
                return Json(FullPayload);
            }

            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(new DateTimeOffset(harness!.Now.AddSeconds(30)));
            return response;
        }));

        await harness.Client.GetSegmentsAsync(Episode, CancellationToken.None);

        Assert.Equal(new[] { TimeSpan.FromSeconds(30) }, harness.CooldownDelays);
    }

    [Theory]
    [InlineData("\"2026-01-01T00:00:45Z\"")]
    [InlineData("1767225645")]
    [InlineData("1767225645000")]
    public void GetRetryAfterSeconds_ReadsResetAtFromBody(string resetAt)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)429);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var seconds = IntroDbClient.GetRetryAfterSeconds(response.Headers, "{\"error\":\"Rate limited\",\"limit\":60,\"reset_at\":" + resetAt + "}", now);

        Assert.Equal(45, seconds);
    }

    [Fact]
    public void GetRetryAfterSeconds_PrefersRetryAfterHeaderOverBody()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(5, IntroDbClient.GetRetryAfterSeconds(response.Headers, "{\"reset_at\":1767225645}", now));
    }

    [Fact]
    public void GetRetryAfterSeconds_DefaultsWhenBodyIsNotJson()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)429);

        Assert.Equal(10, IntroDbClient.GetRetryAfterSeconds(response.Headers, "Too Many Requests", DateTime.UtcNow));
    }

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage RateLimited(TimeSpan? retryAfter)
    {
        var response = new HttpResponseMessage((HttpStatusCode)429);
        if (retryAfter.HasValue)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
        }

        return response;
    }

    /// <summary>
    /// Client wired to a fake clock whose delays advance time instantly.
    /// </summary>
    private sealed class Harness
    {
        private readonly List<TimeSpan> _delays = new();

        public Harness(HttpMessageHandler handler)
        {
            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://api.introdb.app")
            };

            Client = new IntroDbClient(
                httpClient,
                new LoggerFactory().CreateLogger<IntroDbClient>(),
                new IntroDbNotFoundCache(null, null, () => Now),
                () => Now,
                (delay, _) =>
                {
                    _delays.Add(delay);
                    Now += delay;
                    return Task.CompletedTask;
                });
        }

        public IntroDbClient Client { get; }

        public DateTime Now { get; set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Gets delays longer than the minimum request spacing, i.e. rate-limit cooldowns.
        /// </summary>
        public IReadOnlyList<TimeSpan> CooldownDelays => _delays.FindAll(d => d >= TimeSpan.FromSeconds(1));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Task.FromResult(_handler(request));
        }
    }
}
