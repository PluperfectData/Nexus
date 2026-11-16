using System.Globalization;
using Serilog;
using Serilog.Events;

namespace Nexus.Service;

/// <summary>The developer log: the console, and a file a day under the data directory.</summary>
public static class DeveloperLog
{
    public const string Folder = "logs";

    public const int FilesKept = 14;

    private const string Template = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public static LoggerConfiguration Configure(LoggerConfiguration logger, IConfiguration configuration, string dataDirectory) =>
        logger
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: Template, formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(
                Path.Combine(dataDirectory, Folder, "nexus-.log"),
                outputTemplate: Template,
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: FilesKept,
                // add-admin runs beside the service, writing to the same day's file.
                shared: true);

    /// <summary>
    /// A request's line is an error only when the service failed it. The install gate's 503 is the service working as
    /// meant.
    /// </summary>
    public static LogEventLevel RequestLevel(HttpContext http, double _, Exception? ex) =>
        ex is not null || (http.Response.StatusCode >= 500 && http.Response.StatusCode != StatusCodes.Status503ServiceUnavailable)
            ? LogEventLevel.Error
            : LogEventLevel.Information;
}
