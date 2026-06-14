using System;
using System.Linq;
using GearTray.Contracts;
using Xunit;

namespace GearTray.Tests
{
    public class EventLoggerTests : IDisposable
    {
        public EventLoggerTests()
        {
            EventLogger.Clear();
        }

        public void Dispose()
        {
            EventLogger.Clear();
        }

        [Fact]
        public void Log_WithCategoryAndColor_AddsToHistory()
        {
            // Arrange
            string category = "TEST";
            string message = "Test message";
            string color = "#FF0000";

            // Act
            EventLogger.Log(category, message, color);

            // Assert
            var history = EventLogger.GetHistory();
            Assert.Single(history);
            
            var entry = history[0];
            Assert.Equal("TEST", entry.Category);
            Assert.Equal(message, entry.Message);
            Assert.Equal(color, entry.CategoryColor);
            Assert.False(string.IsNullOrEmpty(entry.Timestamp));
        }

        [Fact]
        public void Log_GenericMessage_InfersCategoryAndColor()
        {
            // Act & Assert for AUDIO_SWITCH
            EventLogger.Clear();
            EventLogger.Log("AutoSwitch: Selected headphones");
            var history = EventLogger.GetHistory();
            Assert.Single(history);
            Assert.Equal("AUDIO_SWITCH", history[0].Category);
            Assert.Equal("#8A2BE2", history[0].CategoryColor);

            // Act & Assert for POWER_ON
            EventLogger.Clear();
            EventLogger.Log("Device online=true");
            history = EventLogger.GetHistory();
            Assert.Single(history);
            Assert.Equal("POWER_ON", history[0].Category);
            Assert.Equal("#2E7D32", history[0].CategoryColor);

            // Act & Assert for POWER_OFF
            EventLogger.Clear();
            EventLogger.Log("Device online=false");
            history = EventLogger.GetHistory();
            Assert.Single(history);
            Assert.Equal("POWER_OFF", history[0].Category);
            Assert.Equal("#C62828", history[0].CategoryColor);

            // Act & Assert for ALERT
            EventLogger.Clear();
            EventLogger.Log("Battery level below threshold alert");
            history = EventLogger.GetHistory();
            Assert.Single(history);
            Assert.Equal("ALERT", history[0].Category);
            Assert.Equal("#EF6C00", history[0].CategoryColor);

            // Act & Assert for default SYSTEM
            EventLogger.Clear();
            EventLogger.Log("Just some regular message");
            history = EventLogger.GetHistory();
            Assert.Single(history);
            Assert.Equal("SYSTEM", history[0].Category);
            Assert.Equal("#888888", history[0].CategoryColor);
        }

        [Fact]
        public void Log_EnforcesMaxLimit()
        {
            // Act
            for (int i = 0; i < 550; i++)
            {
                EventLogger.Log("SYSTEM", $"Message {i}");
            }

            // Assert
            var history = EventLogger.GetHistory();
            Assert.Equal(500, history.Count);
            Assert.Equal("Message 50", history[0].Message); // Oldest should be index 50, as 0-49 are evicted
            Assert.Equal("Message 549", history[499].Message); // Newest is at index 499 (the end)
        }

        [Fact]
        public void Log_TriggersEvents()
        {
            // Arrange
            LogEntry? receivedEntry = null;
            string? receivedString = null;

            EventLogger.OnLogEntry += entry => receivedEntry = entry;
            EventLogger.OnLog += str => receivedString = str;

            // Act
            EventLogger.Log("TEST", "Event test message", "#FFFFFF");

            // Assert
            Assert.NotNull(receivedEntry);
            Assert.Equal("TEST", receivedEntry!.Category);
            Assert.Equal("Event test message", receivedEntry.Message);
            Assert.NotNull(receivedString);
            Assert.Contains("[TEST]", receivedString);
            Assert.Contains("Event test message", receivedString);
        }
    }
}
