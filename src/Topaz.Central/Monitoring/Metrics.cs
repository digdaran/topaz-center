using Prometheus;

namespace Topaz.Central.Monitoring;

public static class AppMetrics
{
    public static readonly Counter BatchesReceived = Metrics.CreateCounter(
        "topaz_batches_received_total", "Batches received from stations.", "result");

    public static readonly Counter RecordsApplied = Metrics.CreateCounter(
        "topaz_records_applied_total", "Record changes applied to the central store.", "op");

    public static readonly Counter ProcessingFailures = Metrics.CreateCounter(
        "topaz_batch_processing_failures_total", "Batch processing attempts that failed.");

    public static readonly Histogram ProcessingSeconds = Metrics.CreateHistogram(
        "topaz_batch_processing_seconds", "Time to apply one batch.",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.005, 2, 14) });

    public static readonly Gauge StationSilentSeconds = Metrics.CreateGauge(
        "topaz_station_silent_seconds", "Seconds since the station was last heard from.", "station");

    public static readonly Gauge StationAgentQueue = Metrics.CreateGauge(
        "topaz_station_agent_queue_pending", "Unsent batches in the station agent queue (last heartbeat).", "station");

    public static readonly Gauge StationAgentDead = Metrics.CreateGauge(
        "topaz_station_agent_queue_dead", "Batches rejected by the server and parked on the agent.", "station");

    public static readonly Gauge ServerBacklog = Metrics.CreateGauge(
        "topaz_raw_batches_backlog", "Received batches waiting to be processed.");

    public static readonly Gauge ServerFailed = Metrics.CreateGauge(
        "topaz_raw_batches_failed", "Batches parked after repeated processing failures.");
}
