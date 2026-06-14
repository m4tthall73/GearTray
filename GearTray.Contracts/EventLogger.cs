using System;
using System.Collections.Generic;

namespace GearTray.Contracts;

public class LogEntry
{
    public string Timestamp { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string CategoryColor { get; set; } = "#888888";

    public override string ToString()
    {
        return $"[{Timestamp}] [{Category}] {Message}";
    }
}

public static class EventLogger
{
    private static readonly List<LogEntry> _history = [];
    private static readonly object _lock = new();

    public static event Action<LogEntry>? OnLogEntry;
    public static event Action<string>? OnLog; // Maintain compatibility

    public static void Log(string category, string message, string color = "#555555")
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
            Category = category.ToUpperInvariant(),
            Message = message,
            CategoryColor = color
        };

        lock (_lock)
        {
            _history.Add(entry);
            if (_history.Count > 500)
            {
                _history.RemoveAt(0);
            }
        }
        OnLogEntry?.Invoke(entry);
        OnLog?.Invoke(entry.ToString());
        Console.WriteLine(entry.ToString());
    }

    public static void Log(string message)
    {
        // Infer category and color from message contents for legacy compatibility
        string category = "SYSTEM";
        string color = "#888888";

        string lower = message.ToLowerInvariant();
        if (lower.Contains("autoswitch") || lower.Contains("defaultendpoint") || lower.Contains("default device") || lower.Contains("endpoint"))
        {
            category = "AUDIO_SWITCH";
            color = "#8A2BE2"; // Violet/Purple
        }
        else if (lower.Contains("online=true") || lower.Contains("discovered") || lower.Contains("added"))
        {
            category = "POWER_ON";
            color = "#2E7D32"; // Dark Green
        }
        else if (lower.Contains("online=false") || lower.Contains("offline") || lower.Contains("removed"))
        {
            category = "POWER_OFF";
            color = "#C62828"; // Dark Red
        }
        else if (lower.Contains("alert") || lower.Contains("threshold") || lower.Contains("battery"))
        {
            category = "ALERT";
            color = "#EF6C00"; // Dark Orange
        }

        Log(category, message, color);
    }

    public static List<LogEntry> GetHistory()
    {
        lock (_lock)
        {
            return new List<LogEntry>(_history);
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _history.Clear();
        }
    }
}
