using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Nexus.Service.Tests;

/// <summary>Keeps every line the service logs, so a test can read what an operator would.</summary>
internal sealed class LogCapture : ILogEventSink
{
    private static readonly MessageTemplateTextFormatter Text = new("{Message:lj}");

    public ConcurrentQueue<string> Lines { get; } = new();

    /// <summary>Each line with the category that wrote it, such as <c>Nexus.Operator</c>.</summary>
    public ConcurrentQueue<(string? Category, string Line)> Written { get; } = new();

    public void Emit(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        Text.Format(logEvent, writer);
        var line = writer.ToString();
        Lines.Enqueue(line);
        var category = logEvent.Properties.TryGetValue("SourceContext", out var source) && source is ScalarValue { Value: string name } ? name : null;
        Written.Enqueue((category, line));
    }
}
