using Hangfire;

namespace ManufacturingAI.Services.Ingest;

/// <summary>
/// Manages the per-connector Hangfire recurring job that enqueues a delta sync at the
/// connector's configured interval. An interval of 0 (or less) means "manual only" and
/// removes any existing schedule.
/// </summary>
public class ConnectorSyncScheduler(IRecurringJobManager recurringJobs)
{
    public static string JobId(Guid connectorId) => $"sync-connector-{connectorId}";

    /// <summary>
    /// Registers (or updates) the recurring sync for a connector. Passing an interval of 0
    /// — or a disabled connector — removes the schedule instead.
    /// </summary>
    public void Schedule(Guid connectorId, int intervalMinutes)
    {
        var cron = ToCron(intervalMinutes);
        if (cron is null)
        {
            Remove(connectorId);
            return;
        }

        recurringJobs.AddOrUpdate<SyncSchedulerJob>(
            JobId(connectorId),
            job => job.SyncConnectorAsync(connectorId),
            cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    public void Remove(Guid connectorId) => recurringJobs.RemoveIfExists(JobId(connectorId));

    /// <summary>
    /// Maps a sync interval (in minutes) to a Hangfire cron expression, or null when
    /// auto-sync is disabled. Unsupported cadences fall back to hourly.
    /// </summary>
    public static string? ToCron(int minutes) => minutes switch
    {
        <= 0 => null,
        15 => "*/15 * * * *",
        30 => "*/30 * * * *",
        60 => "0 * * * *",
        360 => "0 */6 * * *",
        720 => "0 */12 * * *",
        1440 => "0 0 * * *",
        < 60 when 60 % minutes == 0 => $"*/{minutes} * * * *",
        _ when minutes % 60 == 0 && minutes / 60 < 24 => $"0 */{minutes / 60} * * *",
        _ => "0 * * * *", // unsupported cadence → hourly
    };
}
