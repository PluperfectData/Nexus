using Microsoft.Extensions.Logging;

namespace Nexus;

internal static partial class ConnectorLog
{
    [LoggerMessage(Message = "{Text}")]
    public static partial void Write(ILogger logger, LogLevel level, string text);
}
