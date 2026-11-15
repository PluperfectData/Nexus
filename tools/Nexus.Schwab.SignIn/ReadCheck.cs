using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Nexus.DuckDb;

namespace Nexus.Schwab.SignIn;

/// <summary>
/// Reads every Schwab stream with the saved sign-in into a local DuckDB file and prints each stream's row count. It
/// only reads: orders stay switched off.
/// </summary>
internal static class ReadCheck
{
    private static readonly IReadOnlyDictionary<string, WriteMode> Modes = new Dictionary<string, WriteMode>
    {
        // Balances and positions are snapshots stamped with the read time, so each run adds its own.
        [SchwabSource.BalancesStream] = WriteMode.Append,
        [SchwabSource.PositionsStream] = WriteMode.Append,
        [SchwabSource.TransactionsStream] = WriteMode.Upsert,
        [SchwabSource.OrdersStream] = WriteMode.Upsert,
    };

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!TryParse(args, out var from, out var output, out var problem))
        {
            await Console.Error.WriteLineAsync(problem);
            return 1;
        }

        var assembly = typeof(ReadCheck).Assembly;
        var id = assembly.GetCustomAttribute<UserSecretsIdAttribute>()!.UserSecretsId;
        var secrets = new ConfigurationBuilder().AddUserSecrets(assembly, optional: true).Build();
        var missing = new[] { SignInFlow.AppKey, SignInFlow.AppSecret, SignInFlow.AccessToken, SignInFlow.RefreshToken }
            .Where(key => string.IsNullOrWhiteSpace(secrets[key]))
            .ToList();
        if (missing.Count > 0)
        {
            await Console.Error.WriteLineAsync(
                $"{string.Join(", ", missing)} not set. Sign in first by running this tool without arguments.");
            return 1;
        }

        var expiresAt = DateTimeOffset.TryParse(secrets[SignInFlow.ExpiresAt], CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)
            ? expiry
            : (DateTimeOffset?)null;
        var config = new ConnectorConfig(
            new Dictionary<string, string?> { [SchwabSettings.StartDate] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            Tokens: new OAuthTokens(secrets[SignInFlow.AccessToken]!, secrets[SignInFlow.RefreshToken], expiresAt),
            Client: new OAuthClient(secrets[SignInFlow.AppKey]!.Trim(), secrets[SignInFlow.AppSecret]!.Trim()));

        using var http = new HttpClient();
        var source = new SchwabSource(http);
        var check = await source.CheckAsync(config, ct);
        if (!check.Succeeded)
        {
            await Console.Error.WriteLineAsync($"Schwab refused the check ({check.ErrorKind}): {check.Message}");
            return 1;
        }

        var secretsPath = PathHelper.GetSecretsPathFromSecretsId(id);
        var rows = Modes.Keys.ToDictionary(stream => stream, _ => 0L);

        async IAsyncEnumerable<SyncMessage> Counted(IAsyncEnumerable<SyncMessage> messages)
        {
            await foreach (var message in messages)
            {
                if (message is RecordBatch batch)
                {
                    rows[batch.Stream] += batch.Rows.Count;
                }

                yield return message;
            }
        }

        Task SaveTokens(OAuthTokens tokens, CancellationToken _)
        {
            var saved = new Dictionary<string, string> { [SignInFlow.AccessToken] = tokens.AccessToken };
            if (tokens.RefreshToken is { } refresh)
            {
                saved[SignInFlow.RefreshToken] = refresh;
            }

            if (tokens.ExpiresAt is { } expires)
            {
                saved[SignInFlow.ExpiresAt] = expires.ToString("O", CultureInfo.InvariantCulture);
            }

            SecretsFile.Save(secretsPath, saved);
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Console.WriteLine($"Reading every Schwab stream from {from:yyyy-MM-dd} into {output}.");
        try
        {
            var read = source.ReadAsync(
                config,
                new ReadRequest(Modes.Keys.Select(stream => new StreamSelection(stream, SyncMode.Incremental)).ToList()),
                ct);
            var write = new DuckDbDestination().WriteAsync(
                new ConnectorConfig(new Dictionary<string, string?> { [DuckDbDestination.PathSetting] = output }),
                new WriteRequest(Modes),
                Counted(read.Tap(SaveTokens, ct: ct)),
                ct);
            await foreach (var _ in write.WithCancellation(ct))
            {
            }
        }
        catch (ConnectorException ex)
        {
            await Console.Error.WriteLineAsync($"The read stopped ({ex.Kind}): {ex.Message}");
            return 1;
        }

        foreach (var (stream, count) in rows)
        {
            Console.WriteLine(string.Create(CultureInfo.CurrentCulture, $"{stream}: {count:N0} rows"));
        }

        Console.WriteLine($"Done. The rows are in {output}.");
        return 0;
    }

    private static bool TryParse(IReadOnlyList<string> args, out DateOnly from, out string output, out string problem)
    {
        from = DateOnly.FromDateTime(DateTime.Today).AddYears(-1);
        output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexus", "schwab.duckdb");
        problem = string.Empty;

        for (var i = 0; i < args.Count; i += 2)
        {
            var value = i + 1 < args.Count ? args[i + 1] : null;
            switch (args[i])
            {
                case "--from" when DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                    from = date;
                    break;
                case "--out" when !string.IsNullOrWhiteSpace(value):
                    output = Path.GetFullPath(value);
                    break;
                default:
                    problem = "Usage: read [--from yyyy-MM-dd] [--out path.duckdb]. Without them, it reads a year back into "
                        + output + ".";
                    return false;
            }
        }

        return true;
    }
}
