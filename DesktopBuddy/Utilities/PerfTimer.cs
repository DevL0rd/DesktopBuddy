using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ResoniteModLoader;

namespace DesktopBuddy;

public sealed class PerfTimer : IDisposable
{
    private readonly Stopwatch _reportSw = Stopwatch.StartNew();
    private const int REPORT_INTERVAL_MS = 5_000;

    private readonly Dictionary<string, StepStats> _steps = new();
    private readonly Dictionary<string, long> _counters = new();
    private readonly object _lock = new();
    private long _totalFrames;
    private long _droppedFrames;

    private class StepStats
    {
        public long TotalTicks;
        public long MaxTicks;
        public long Count;
    }

    private int _recordsSinceCheck;

    public void Record(string step, long elapsedTicks)
    {
        lock (_lock)
        {
            if (!_steps.TryGetValue(step, out var stats))
            {
                stats = new StepStats();
                _steps[step] = stats;
            }
            stats.TotalTicks += elapsedTicks;
            if (elapsedTicks > stats.MaxTicks) stats.MaxTicks = elapsedTicks;
            stats.Count++;
        }

        if (++_recordsSinceCheck >= 100)
        {
            _recordsSinceCheck = 0;
            if (_reportSw.ElapsedMilliseconds >= REPORT_INTERVAL_MS)
            {
                PrintReport();
                _reportSw.Restart();
            }
        }
    }

    public void RecordMs(string step, double ms)
    {
        Record(step, (long)(ms * Stopwatch.Frequency / 1000.0));
    }

    public void IncrementFrames() => Interlocked.Increment(ref _totalFrames);

    public void IncrementDropped(string reason = null)
    {
        Interlocked.Increment(ref _droppedFrames);
        if (!string.IsNullOrEmpty(reason))
            IncrementCounter($"drop:{reason}");
    }

    public void IncrementCounter(string counter, long delta = 1)
    {
        lock (_lock)
        {
            if (_counters.TryGetValue(counter, out var value))
                _counters[counter] = value + delta;
            else
                _counters[counter] = delta;
        }
    }

    public TimedScope Time(string step) => new TimedScope(this, step);

    public readonly struct TimedScope : IDisposable
    {
        private readonly PerfTimer _timer;
        private readonly string _step;
        private readonly long _startTicks;

        public TimedScope(PerfTimer timer, string step)
        {
            _timer = timer;
            _step = step;
            _startTicks = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            long elapsed = Stopwatch.GetTimestamp() - _startTicks;
            _timer.Record(_step, elapsed);
        }
    }

    private void PrintReport()
    {
        Dictionary<string, StepStats> snapshot;
        Dictionary<string, long> countersSnapshot;
        long frames, dropped;

        lock (_lock)
        {
            snapshot = new Dictionary<string, StepStats>();
            foreach (var kvp in _steps)
            {
                snapshot[kvp.Key] = new StepStats
                {
                    TotalTicks = kvp.Value.TotalTicks,
                    MaxTicks = kvp.Value.MaxTicks,
                    Count = kvp.Value.Count
                };
                kvp.Value.TotalTicks = 0;
                kvp.Value.MaxTicks = 0;
                kvp.Value.Count = 0;
            }

            countersSnapshot = new Dictionary<string, long>(_counters);
            _counters.Clear();

            frames = Interlocked.Exchange(ref _totalFrames, 0);
            dropped = Interlocked.Exchange(ref _droppedFrames, 0);
        }

        if (snapshot.Count == 0 && frames == 0) return;

        double freqMs = Stopwatch.Frequency / 1000.0;
        var lines = new List<string>();
        lines.Add($"[Perf] === 5s Report: {frames} frames, {dropped} dropped ===");

        foreach (var kvp in snapshot)
        {
            if (kvp.Value.Count == 0) continue;
            double avgMs = (kvp.Value.TotalTicks / (double)kvp.Value.Count) / freqMs;
            double maxMs = kvp.Value.MaxTicks / freqMs;
            double totalMs = kvp.Value.TotalTicks / freqMs;
            lines.Add($"[Perf]   {kvp.Key}: avg={avgMs:F2}ms max={maxMs:F2}ms total={totalMs:F0}ms n={kvp.Value.Count}");
        }

        foreach (var kvp in countersSnapshot)
            lines.Add($"[Perf]   {kvp.Key}: count={kvp.Value}");

        Log.Msg(string.Join("\n", lines));
    }

    public void Dispose() { }
}
