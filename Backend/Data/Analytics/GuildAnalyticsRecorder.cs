using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Analytics;

public sealed record GuildAnalyticsWrite(
    ulong GuildId,
    GuildAnalyticsMetric Metric,
    long Value = 1,
    GuildAnalyticsFeature Feature = GuildAnalyticsFeature.Unspecified,
    GuildAnalyticsOutcome Outcome = GuildAnalyticsOutcome.Unspecified,
    string? Operation = null,
    string? Source = null,
    string? Reason = null,
    ulong? ChannelId = null,
    double DurationSeconds = 0,
    DateTimeOffset? OccurredAt = null);

public interface IGuildAnalyticsRecorder
{
    long DroppedCount { get; }
    bool TryRecord(GuildAnalyticsWrite measurement);
}

public sealed class GuildAnalyticsRecorder : BackgroundService, IGuildAnalyticsRecorder
{
    private const int Capacity = 10_000;
    private const int BatchSize = 500;
    private readonly RankoonDbContext _database;
    private readonly TimeProvider _timeProvider;
    private readonly AnalyticsRetentionOptions _options;
    private readonly ILogger<GuildAnalyticsRecorder> _logger;
    private readonly Channel<GuildAnalyticsWrite> _queue = Channel.CreateBounded<GuildAnalyticsWrite>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private long _droppedCount;

    public GuildAnalyticsRecorder(RankoonDbContext database, TimeProvider timeProvider, IOptions<AnalyticsRetentionOptions> options, ILogger<GuildAnalyticsRecorder> logger)
    {
        _database = database;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public bool TryRecord(GuildAnalyticsWrite measurement)
    {
        if (measurement.GuildId == 0 || measurement.Value == 0 || !Enum.IsDefined(measurement.Metric)
            || !Enum.IsDefined(measurement.Feature) || !Enum.IsDefined(measurement.Outcome))
        {
            Interlocked.Increment(ref _droppedCount);
            return false;
        }

        measurement = measurement with
        {
            Operation = NormalizeDimension(measurement.Operation),
            Source = NormalizeDimension(measurement.Source),
            Reason = NormalizeDimension(measurement.Reason),
            OccurredAt = measurement.OccurredAt?.ToUniversalTime() ?? _timeProvider.GetUtcNow()
        };
        if (_queue.Writer.TryWrite(measurement)) return true;
        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<GuildAnalyticsWrite>(BatchSize);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var available = _queue.Reader.WaitToReadAsync(stoppingToken).AsTask();
                var flushDelay = Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, stoppingToken);
                await Task.WhenAny(available, flushDelay);
                while (batch.Count < BatchSize && _queue.Reader.TryRead(out var item)) batch.Add(item);
                if (batch.Count == 0) continue;
                await PersistAsync(batch, stoppingToken);
                batch.Clear();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task PersistAsync(IReadOnlyCollection<GuildAnalyticsWrite> batch, CancellationToken cancellationToken)
    {
        var increments = batch
            .SelectMany(item => new[]
            {
                ToIncrement(item, GuildAnalyticsGranularity.Hour),
                ToIncrement(item, GuildAnalyticsGranularity.Day)
            })
            .GroupBy(item => item.Key)
            .Select(group => (group.Key, Count: group.LongCount(), Value: group.Sum(item => item.Value), Duration: group.Sum(item => item.DurationSeconds)))
            .ToArray();

        var writes = new List<WriteModel<GuildAnalyticsBucket>>(increments.Length);
        foreach (var increment in increments)
        {
            var key = increment.Key;
            var filter = Builders<GuildAnalyticsBucket>.Filter.Where(bucket => bucket.GuildId == key.GuildId
                && bucket.Granularity == key.Granularity && bucket.BucketStartUtc == key.BucketStartUtc
                && bucket.Metric == key.Metric && bucket.Feature == key.Feature && bucket.Outcome == key.Outcome
                && bucket.Operation == key.Operation && bucket.Source == key.Source && bucket.Reason == key.Reason
                && bucket.ChannelId == key.ChannelId);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var update = Builders<GuildAnalyticsBucket>.Update
                .Inc(bucket => bucket.Count, increment.Count)
                .Inc(bucket => bucket.Value, increment.Value)
                .Inc(bucket => bucket.DurationSeconds, increment.Duration)
                .Set(bucket => bucket.UpdatedAtUtc, now)
                .SetOnInsert(bucket => bucket.GuildId, key.GuildId)
                .SetOnInsert(bucket => bucket.Granularity, key.Granularity)
                .SetOnInsert(bucket => bucket.BucketStartUtc, key.BucketStartUtc)
                .SetOnInsert(bucket => bucket.Metric, key.Metric)
                .SetOnInsert(bucket => bucket.Feature, key.Feature)
                .SetOnInsert(bucket => bucket.Outcome, key.Outcome)
                .SetOnInsert(bucket => bucket.Operation, key.Operation)
                .SetOnInsert(bucket => bucket.Source, key.Source)
                .SetOnInsert(bucket => bucket.Reason, key.Reason)
                .SetOnInsert(bucket => bucket.ChannelId, key.ChannelId)
                .SetOnInsert(bucket => bucket.ExpiresAtUtc, key.BucketStartUtc.Add(_options.RetentionFor(key.Granularity)));
            writes.Add(new UpdateOneModel<GuildAnalyticsBucket>(filter, update) { IsUpsert = true });
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _database.GuildAnalyticsBuckets.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (attempt < 3)
            {
                _logger.LogWarning(exception, "Unable to persist analytics bucket increments; retry {Attempt}/3", attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), _timeProvider, cancellationToken);
            }
            catch (Exception exception)
            {
                Interlocked.Add(ref _droppedCount, batch.Count);
                _logger.LogError(exception, "Dropping {Count} analytics measurements after three persistence attempts", batch.Count);
            }
        }
    }

    private static AnalyticsIncrement ToIncrement(GuildAnalyticsWrite item, GuildAnalyticsGranularity granularity)
    {
        var occurredAt = item.OccurredAt!.Value.UtcDateTime;
        var bucketStart = granularity == GuildAnalyticsGranularity.Hour
            ? new DateTime(occurredAt.Year, occurredAt.Month, occurredAt.Day, occurredAt.Hour, 0, 0, DateTimeKind.Utc)
            : new DateTime(occurredAt.Year, occurredAt.Month, occurredAt.Day, 0, 0, 0, DateTimeKind.Utc);
        return new(new(item.GuildId, granularity, bucketStart, item.Metric, item.Feature, item.Outcome,
            item.Operation ?? string.Empty, item.Source ?? string.Empty, item.Reason ?? string.Empty, item.ChannelId), item.Value, item.DurationSeconds);
    }

    private static string NormalizeDimension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = new string(value.Trim().ToLowerInvariant().Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '.').ToArray()).Trim('.');
        return normalized[..Math.Min(normalized.Length, 80)];
    }

    private readonly record struct AnalyticsKey(ulong GuildId, GuildAnalyticsGranularity Granularity, DateTime BucketStartUtc,
        GuildAnalyticsMetric Metric, GuildAnalyticsFeature Feature, GuildAnalyticsOutcome Outcome, string Operation, string Source, string Reason, ulong? ChannelId);
    private readonly record struct AnalyticsIncrement(AnalyticsKey Key, long Value, double DurationSeconds);
}
