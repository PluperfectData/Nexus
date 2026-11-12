using Microsoft.Extensions.Configuration;
using Nexus.Sql;

namespace Nexus.Sql.Tests;

/// <summary>A literal, the SQL type it is stored as, and what a source should read back.</summary>
public sealed record TypeCase(string SqlType, string Literal, ColumnType Type, object? Value, int Precision = 0, int Scale = 0);

/// <summary>A scratch database on a real server, created for one fixture and dropped after it.</summary>
public abstract class TestDatabase(string name)
{
    private static readonly IConfigurationRoot Configuration = new ConfigurationBuilder()
        .AddUserSecrets(typeof(TestDatabase).Assembly, optional: true)
        .AddEnvironmentVariables()
        .Build();

    public string Name { get; } = name;

    public abstract ISourceConnector Source { get; }

    public ConnectorConfig Config { get; protected set; } = new(new Dictionary<string, string?>());

    /// <summary>The type of a column holding a date and time with no zone.</summary>
    public abstract string DateTimeType { get; }

    /// <summary>Values each read as their contract type, in one row.</summary>
    public abstract IReadOnlyList<TypeCase> TypeCases { get; }

    /// <summary>A value the contract cannot carry, which the source reports as a SourceError.</summary>
    public abstract TypeCase Unreadable { get; }

    protected string DatabaseName { get; } = $"nexus_test_{Guid.NewGuid():N}"[..24];

    public abstract string Stream(string table);

    public abstract string InsertSeries(string table, int count);

    public abstract Task CreateAsync();

    public abstract Task DropAsync();

    public abstract Task ExecuteAsync(string sql);

    public override string ToString() => Name;

    protected string AdminConnectionString() =>
        Configuration.GetConnectionString(Name)
        ?? throw new InvalidOperationException(
            $"Set ConnectionStrings:{Name} in this project's user secrets, or ConnectionStrings__{Name} in the environment.");

    protected static ConnectorConfig ConfigFor(
        string host,
        string? port,
        string database,
        string user,
        string? password,
        string? tls,
        params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            [SqlSettings.Host] = host,
            [SqlSettings.Port] = port,
            [SqlSettings.Database] = database,
            [SqlSettings.User] = user,
            [SqlSettings.Password] = password,
            [SqlSettings.Tls] = tls,
        };

        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return new ConnectorConfig(values);
    }
}

public static class TestDatabases
{
    public static IEnumerable<TestDatabase> All() =>
        [new MySqlTestDatabase(), new PostgresTestDatabase(), new SqlServerTestDatabase()];

    public static IEnumerable<TestDatabase> WithSchemas() =>
        [new PostgresTestDatabase(), new SqlServerTestDatabase()];
}
