using NUnit.Framework;
using Shouldly;

namespace Nexus.Sql.Tests;

[TestFixtureSource(typeof(TestDatabases), nameof(TestDatabases.WithSchemas))]
public sealed class SchemaTests(TestDatabase database)
{
    [OneTimeSetUp]
    public Task CreateDatabase() => database.CreateAsync();

    [OneTimeTearDown]
    public Task DropDatabase() => database.DropAsync();

    [Test]
    public async Task Discover_names_each_table_in_every_schema_by_schema_and_table()
    {
        await database.ExecuteAsync("CREATE SCHEMA billing");
        await database.ExecuteAsync("CREATE TABLE billing.orders (id INT NOT NULL PRIMARY KEY)");
        await database.ExecuteAsync("CREATE TABLE orders (id INT NOT NULL PRIMARY KEY)");

        var catalog = await database.Source.DiscoverAsync(database.Config, default);

        catalog.Streams.Select(stream => stream.Name).ShouldBe([database.Stream("orders"), "billing.orders"], ignoreOrder: true);
    }
}
