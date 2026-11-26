using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine;
using Debug = UnityEngine.Debug;

[assembly: InternalsVisibleTo("OscQueryAnimationDebugger.Tests")]

internal sealed class OscQueryLogLimiter
{
    internal const double IntervalSeconds = 5;
    internal const int MaxRepeatedMessages = 32;

    private enum EntryKind
    {
        Verbose,
        Repeated,
        Overflow
    }

    private sealed class Entry
    {
        public LogType Type;
        public EntryKind Kind;
        public string Description;
        public double Deadline;
        public long Suppressed;
    }

    private readonly object _lock = new object();
    private readonly Func<double> _clock;
    private readonly Action<LogType, string> _write;
    private readonly Dictionary<(LogType, string), Entry> _verbose = new Dictionary<(LogType, string), Entry>();
    private readonly Dictionary<(LogType, string), Entry> _repeated = new Dictionary<(LogType, string), Entry>();
    private readonly Dictionary<LogType, Entry> _overflow = new Dictionary<LogType, Entry>();
    private double _nextFlush = double.PositiveInfinity;

    internal OscQueryLogLimiter()
        : this(() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency, WriteUnityLog)
    {
    }

    internal OscQueryLogLimiter(Func<double> clock, Action<LogType, string> write)
    {
        _clock = clock;
        _write = write;
    }

    internal bool ShouldLogVerbose(string category, LogType type)
    {
        lock (_lock)
        {
            var key = (type, category);
            if (_verbose.TryGetValue(key, out Entry entry))
            {
                entry.Suppressed++;
                return false;
            }

            _verbose.Add(key, CreateEntry(type, EntryKind.Verbose, category));
            return true;
        }
    }

    internal void LogRepeated(LogType type, string message)
    {
        string toWrite = null;
        lock (_lock)
        {
            var key = (type, message);
            if (_repeated.TryGetValue(key, out Entry entry))
            {
                entry.Suppressed++;
            }
            else if (CountRepeatedMessages(type) < MaxRepeatedMessages)
            {
                _repeated.Add(key, CreateEntry(type, EntryKind.Repeated, message));
                toWrite = message;
            }
            else if (_overflow.TryGetValue(type, out Entry overflow))
            {
                overflow.Suppressed++;
            }
            else
            {
                _overflow.Add(type, CreateEntry(type, EntryKind.Overflow, message));
                toWrite = $"{message} (ログの種類が多いため以降は集約します)";
            }
        }

        if (toWrite != null)
            _write(type, toWrite);
    }

    internal void Flush(bool force = false)
    {
        double now = _clock();
        List<(LogType, string)> summaries;
        lock (_lock)
        {
            if (!force && now < _nextFlush) return;

            summaries = new List<(LogType, string)>();
            double nextFlush = double.PositiveInfinity;
            Drain(_verbose, now, force, summaries, ref nextFlush);
            Drain(_repeated, now, force, summaries, ref nextFlush);
            Drain(_overflow, now, force, summaries, ref nextFlush);
            _nextFlush = nextFlush;
        }

        foreach (var summary in summaries)
            _write(summary.Item1, summary.Item2);
    }

    private Entry CreateEntry(LogType type, EntryKind kind, string description)
    {
        double deadline = _clock() + IntervalSeconds;
        if (deadline < _nextFlush) _nextFlush = deadline;
        return new Entry { Type = type, Kind = kind, Description = description, Deadline = deadline };
    }

    private int CountRepeatedMessages(LogType type)
    {
        int count = 0;
        foreach (var key in _repeated.Keys)
        {
            if (key.Item1 == type) count++;
        }
        return count;
    }

    private static void Drain<TKey>(
        Dictionary<TKey, Entry> entries,
        double now,
        bool force,
        List<(LogType, string)> summaries,
        ref double nextFlush)
    {
        List<TKey> expired = null;
        foreach (var pair in entries)
        {
            Entry entry = pair.Value;
            if (!force && now < entry.Deadline)
            {
                if (entry.Deadline < nextFlush) nextFlush = entry.Deadline;
                continue;
            }

            if (entry.Suppressed > 0)
                summaries.Add((entry.Type, FormatSummary(entry)));

            if (expired == null) expired = new List<TKey>();
            expired.Add(pair.Key);
        }

        if (expired == null) return;
        foreach (TKey key in expired)
            entries.Remove(key);
    }

    private static string FormatSummary(Entry entry)
    {
        switch (entry.Kind)
        {
            case EntryKind.Verbose:
                return $"[OSCQuery Animation Debugger] {entry.Description}: 詳細ログを {entry.Suppressed} 件省略しました";
            case EntryKind.Repeated:
                return $"{entry.Description} (同じメッセージを {entry.Suppressed} 件省略)";
            default:
                return $"[OSCQuery Animation Debugger] ログの種類が多いため {entry.Suppressed} 件を省略しました。例: {entry.Description}";
        }
    }

    private static void WriteUnityLog(LogType type, string message)
    {
        switch (type)
        {
            case LogType.Warning:
                Debug.LogWarning(message);
                break;
            case LogType.Error:
                Debug.LogError(message);
                break;
            default:
                Debug.Log(message);
                break;
        }
    }
}

public partial class OscQueryAnimationDebugger
{
    private readonly OscQueryLogLimiter _logLimiter = new OscQueryLogLimiter();

    internal bool ShouldLogVerbose(string category, LogType type = LogType.Log)
    {
        return VerboseReceiveLogging && _logLimiter.ShouldLogVerbose(category, type);
    }

    internal void LogWarningRepeated(string message)
    {
        _logLimiter.LogRepeated(LogType.Warning, message);
    }

    internal void LogErrorRepeated(string message)
    {
        _logLimiter.LogRepeated(LogType.Error, message);
    }

    private void LogInfoRepeated(string message)
    {
        _logLimiter.LogRepeated(LogType.Log, message);
    }
}
