// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace IntroDbPlugin.Services;

/// <summary>
/// Client for retrieving segment timestamps from IntroDB.
/// </summary>
public sealed class IntroDbClient
{
    /// <summary>
    /// Default timeout for IntroDB requests, in seconds.
    /// </summary>
    public const int DefaultTimeoutSeconds = 10;

    private const string BaseUrl = "https://api.introdb.app";
    private const string SegmentsPath = "/segments";
    private const int TooManyRequestsStatusCode = 429;
    private const int DefaultRetryAfterSeconds = 10;
    private const int MaxRetryAfterSeconds = 300;
    private const int MaxBackOffMultiplier = 6;
    private const int MaxRetries = 3;

    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(100);

    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<IntroDbClient> _logger;
    private readonly IntroDbNotFoundCache _notFoundCache;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _throttleLock = new();
    private DateTime _lastRequestUtc = DateTime.MinValue;
    private DateTime _nextAllowedSendUtc = DateTime.MinValue;
    private int _consecutiveThrottles;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbClient"/> class with a memory-only not-found cache.
    /// </summary>
    /// <param name="httpClient">HTTP client.</param>
    /// <param name="logger">Logger.</param>
    public IntroDbClient(HttpClient httpClient, ILogger<IntroDbClient> logger)
        : this(httpClient, logger, new IntroDbNotFoundCache())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbClient"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="notFoundCache">Cache of lookups IntroDB has nothing for.</param>
    public IntroDbClient(HttpClient httpClient, ILogger<IntroDbClient> logger, IntroDbNotFoundCache notFoundCache)
        : this(httpClient, logger, notFoundCache, () => DateTime.UtcNow, Task.Delay)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbClient"/> class with an injectable clock and delay.
    /// </summary>
    /// <param name="httpClient">HTTP client.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="notFoundCache">Cache of lookups IntroDB has nothing for.</param>
    /// <param name="utcNow">Returns the current UTC time.</param>
    /// <param name="delay">Waits for the given duration.</param>
    internal IntroDbClient(
        HttpClient httpClient,
        ILogger<IntroDbClient> logger,
        IntroDbNotFoundCache notFoundCache,
        Func<DateTime> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notFoundCache = notFoundCache ?? throw new ArgumentNullException(nameof(notFoundCache));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(BaseUrl, UriKind.Absolute);
        }

        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Gets the cache of lookups IntroDB has nothing for.
    /// </summary>
    public IntroDbNotFoundCache NotFoundCache => _notFoundCache;

    /// <summary>
    /// Fetch all segments for an episode or movie.
    /// </summary>
    /// <param name="lookup">What to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome and, when successful, the segments.</returns>
    public async Task<IntroDbFetchResult> GetSegmentsAsync(IntroDbLookup lookup, CancellationToken cancellationToken)
    {
        if (lookup is null)
        {
            throw new ArgumentNullException(nameof(lookup));
        }

        if (string.IsNullOrWhiteSpace(lookup.ImdbId))
        {
            throw new ArgumentException("IMDb id must be provided.", nameof(lookup));
        }

        if (!lookup.IsMovie && (!(lookup.Season > 0) || !(lookup.Episode > 0)))
        {
            throw new ArgumentOutOfRangeException(nameof(lookup), "Season and episode numbers must be positive.");
        }

        if (_notFoundCache.TryGetHit(lookup.CacheKey))
        {
            return IntroDbFetchResult.CachedNotFound;
        }

        var requestUri = BuildSegmentsUri(lookup);
        _logger.LogInformation("IntroDB API request: {Uri}", requestUri);

        HttpResponseMessage? response;
        try
        {
            response = await SendWithRetryAsync(requestUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogError(ex, "IntroDB API request failed for {Uri}", requestUri);
            return IntroDbFetchResult.Error;
        }

        if (response is null)
        {
            return IntroDbFetchResult.RateLimited;
        }

        using var _ = response;
        _logger.LogInformation("IntroDB API response: StatusCode={StatusCode} for {Uri}", response.StatusCode, requestUri);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _notFoundCache.Remember(lookup.CacheKey, IntroDbNotFoundCache.NotFoundTtl);
            return IntroDbFetchResult.NotFound;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "IntroDB API error response body: {Body}",
                string.IsNullOrEmpty(body) ? "(empty)" : body!.Length > 500 ? body.Substring(0, 500) + "..." : body);
            return IntroDbFetchResult.Error;
        }

#if EMBY
        using var payloadStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#else
        using var payloadStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#endif
        SegmentsResponse? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<SegmentsResponse>(
                payloadStream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "IntroDB API response could not be parsed for {Uri}", requestUri);
            return IntroDbFetchResult.Error;
        }

        if (payload is null)
        {
            _logger.LogError("IntroDB API response could not be parsed for {Uri}", requestUri);
            return IntroDbFetchResult.Error;
        }

        var result = new IntroDbSegmentsResult(
            ToSegment(payload.Intro),
            ToSegment(payload.Recap),
            ToSegment(payload.Outro),
            ToSegment(payload.PostCredits),
            payload.NoIntro,
            payload.NoRecap,
            payload.NoOutro);

        _logger.LogDebug(
            "IntroDB API parsed response: Intro={HasIntro}, Recap={HasRecap}, Outro={HasOutro}, PostCredits={HasPostCredits}, NoIntro={NoIntro}, NoRecap={NoRecap}, NoOutro={NoOutro}",
            result.Intro is not null,
            result.Recap is not null,
            result.Outro is not null,
            result.PostCredits is not null,
            result.NoIntro,
            result.NoRecap,
            result.NoOutro);

        if (result.Intro is null && result.Recap is null && result.Outro is null && result.PostCredits is null)
        {
            var confirmedAbsent = result.NoIntro && result.NoRecap && result.NoOutro;
            _notFoundCache.Remember(
                lookup.CacheKey,
                confirmedAbsent ? IntroDbNotFoundCache.ConfirmedAbsentTtl : IntroDbNotFoundCache.NotFoundTtl);
        }

        return new IntroDbFetchResult(IntroDbFetchStatus.Ok, result);
    }

    /// <summary>
    /// Computes how long to wait after a throttled response: the Retry-After header,
    /// then the body's <c>reset_at</c>, then a default.
    /// </summary>
    /// <param name="headers">Response headers.</param>
    /// <param name="body">Response body, which may carry <c>reset_at</c>.</param>
    /// <param name="utcNow">Current UTC time, used for absolute reset times.</param>
    /// <returns>Seconds to wait, clamped to [1, <see cref="MaxRetryAfterSeconds"/>].</returns>
    internal static int GetRetryAfterSeconds(HttpResponseHeaders headers, string? body, DateTime utcNow)
    {
        var retryAfter = headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta)
        {
            return ClampRetryAfterSeconds((int)Math.Ceiling(delta.TotalSeconds));
        }

        if (retryAfter?.Date is DateTimeOffset date)
        {
            return ClampRetryAfterSeconds((int)Math.Ceiling((date.UtcDateTime - utcNow).TotalSeconds));
        }

        if (TryGetResetAt(body, out var resetUtc))
        {
            return ClampRetryAfterSeconds((int)Math.Ceiling((resetUtc - utcNow).TotalSeconds));
        }

        return DefaultRetryAfterSeconds;
    }

    /// <summary>
    /// Grows the wait across consecutive throttled responses.
    /// </summary>
    /// <param name="baseSeconds">Wait taken from the response.</param>
    /// <param name="consecutive">Number of consecutive throttled responses.</param>
    /// <returns>Adjusted wait in seconds, capped at <see cref="MaxRetryAfterSeconds"/>.</returns>
    internal static int ApplyConsecutiveBackOff(int baseSeconds, int consecutive)
    {
        var multiplier = Math.Max(1, Math.Min(consecutive, MaxBackOffMultiplier));
        return Math.Min(baseSeconds * multiplier, MaxRetryAfterSeconds);
    }

    private static bool TryGetResetAt(string? body, out DateTime resetUtc)
    {
        resetUtc = default;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body!);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("reset_at", out var resetAt))
            {
                return false;
            }

            if (resetAt.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(resetAt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                resetUtc = parsed.UtcDateTime;
                return true;
            }

            if (resetAt.ValueKind == JsonValueKind.Number && resetAt.TryGetInt64(out var epoch) && epoch > 0)
            {
                // Values this large are epoch milliseconds; smaller ones are epoch seconds.
                resetUtc = epoch > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime
                    : DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                return true;
            }
        }
        catch (JsonException)
        {
            // Not JSON (e.g. a proxy's plain-text 429).
        }

        return false;
    }

    private static int ClampRetryAfterSeconds(int seconds) => Math.Max(1, Math.Min(seconds, MaxRetryAfterSeconds));

    private static bool IsThrottled(HttpStatusCode statusCode)
        => (int)statusCode == TooManyRequestsStatusCode || statusCode == HttpStatusCode.ServiceUnavailable;

    private static IntroDbSegment? ToSegment(SegmentResponse? segment)
    {
        if (segment is null)
        {
            return null;
        }

        return new IntroDbSegment(
            segment.StartMs / 1000d,
            segment.EndMs / 1000d,
            segment.Confidence,
            segment.SubmissionCount);
    }

    /// <summary>
    /// Sends a GET request, waiting out and retrying throttled responses.
    /// </summary>
    /// <returns>The response, or null when still throttled after <see cref="MaxRetries"/> retries.</returns>
    private async Task<HttpResponseMessage?> SendWithRetryAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await WaitForTurnAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!IsThrottled(response.StatusCode))
            {
                Interlocked.Exchange(ref _consecutiveThrottles, 0);
                return response;
            }

            var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            var waitSeconds = RegisterThrottle(response.Headers, body, response.StatusCode);
            response.Dispose();

            if (attempt >= MaxRetries)
            {
                return null;
            }

            _logger.LogDebug(
                "IntroDB API rate limit is currently active until {RateLimitExpiryUtc} UTC. Retrying {Uri} (attempt {Attempt}/{MaxRetries}).",
                _utcNow().AddSeconds(waitSeconds).ToString("u", CultureInfo.InvariantCulture),
                requestUri,
                attempt + 1,
                MaxRetries);
        }
    }

    /// <summary>
    /// Waits for the shared request gate: any active cooldown and the minimum request spacing.
    /// </summary>
    private async Task WaitForTurnAsync(CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTime waitUntil;
            lock (_throttleLock)
            {
                waitUntil = _lastRequestUtc + MinRequestInterval;
                if (_nextAllowedSendUtc > waitUntil)
                {
                    waitUntil = _nextAllowedSendUtc;
                }
            }

            var waitTime = waitUntil - _utcNow();
            if (waitTime > TimeSpan.Zero)
            {
                await _delay(waitTime, cancellationToken).ConfigureAwait(false);
            }

            lock (_throttleLock)
            {
                _lastRequestUtc = _utcNow();
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    /// <summary>
    /// Records a throttled response and pauses all requests until it has passed.
    /// </summary>
    /// <returns>Seconds until requests resume.</returns>
    private int RegisterThrottle(HttpResponseHeaders headers, string? body, HttpStatusCode statusCode)
    {
        var now = _utcNow();
        var consecutive = Interlocked.Increment(ref _consecutiveThrottles);
        var waitSeconds = ApplyConsecutiveBackOff(GetRetryAfterSeconds(headers, body, now), consecutive);
        var resumeUtc = now.AddSeconds(waitSeconds);

        bool cooldownStarted;
        lock (_throttleLock)
        {
            cooldownStarted = _nextAllowedSendUtc <= now;
            if (resumeUtc > _nextAllowedSendUtc)
            {
                _nextAllowedSendUtc = resumeUtc;
            }
        }

        if (cooldownStarted)
        {
            _logger.LogWarning(
                "IntroDB API rate limit exceeded (status {StatusCode}). Will not send requests until {RateLimitExpiryUtc} UTC. Retry-after: {RetryAfterSeconds}s. Consecutive 429 responses: {Consecutive429Count}",
                (int)statusCode,
                resumeUtc.ToString("u", CultureInfo.InvariantCulture),
                waitSeconds,
                consecutive);
        }

        return waitSeconds;
    }

    private static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
#if EMBY
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#else
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
#endif
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private Uri BuildSegmentsUri(IntroDbLookup lookup)
    {
        var baseUri = _httpClient.BaseAddress ?? new Uri(BaseUrl, UriKind.Absolute);
        var query = lookup.IsMovie
            ? $"imdb_id={Uri.EscapeDataString(lookup.ImdbId)}&is_movie=true"
            : string.Format(
                CultureInfo.InvariantCulture,
                "imdb_id={0}&season={1}&episode={2}",
                Uri.EscapeDataString(lookup.ImdbId),
                lookup.Season,
                lookup.Episode);

        var builder = new UriBuilder(new Uri(baseUri, SegmentsPath))
        {
            Query = query
        };
        return builder.Uri;
    }

    private sealed class SegmentsResponse
    {
        [JsonPropertyName("intro")]
        public SegmentResponse? Intro { get; set; }

        [JsonPropertyName("recap")]
        public SegmentResponse? Recap { get; set; }

        [JsonPropertyName("outro")]
        public SegmentResponse? Outro { get; set; }

        [JsonPropertyName("post_credits")]
        public SegmentResponse? PostCredits { get; set; }

        [JsonPropertyName("no_intro")]
        public bool NoIntro { get; set; }

        [JsonPropertyName("no_recap")]
        public bool NoRecap { get; set; }

        [JsonPropertyName("no_outro")]
        public bool NoOutro { get; set; }
    }

    private sealed class SegmentResponse
    {
        [JsonPropertyName("start_ms")]
        public long StartMs { get; set; }

        [JsonPropertyName("end_ms")]
        public long EndMs { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("submission_count")]
        public int SubmissionCount { get; set; }
    }
}
