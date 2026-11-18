# Using the connectors in code

[Documentation](../README.md) › [Connectors](README.md) › Using the connectors in code

Standalone, in memory:

```csharp
var result = await source.ReadToMemoryAsync(
    config,
    new ReadRequest([new StreamSelection("orders", SyncMode.FullRefresh)]),
    logger);

MemoryTable orders = result.Tables["orders"];
// orders.Rows, orders.State, result.RefreshedCredentials
```

Source to destination, where `tokenStore` and `stateStore` are the host's:

```csharp
var messages = source.ReadAsync(sourceConfig, readRequest, ct)
    .Tap((tokens, cancellation) => tokenStore.SaveAsync(tokens, cancellation), logger, ct);

await foreach (var checkpoint in destination.WriteAsync(destinationConfig, writeRequest, messages, ct))
{
    await stateStore.SaveAsync(checkpoint, ct);
}
```

A destination consumes `CredentialsRefreshed` and `LogMessage` without acting on them. `Tap` hands each to
the host before passing it on; without it, refreshed tokens are lost.

An action:

```csharp
var placed = await new SchwabOrders(httpClient).PlaceAsync(
    config,
    "12345678",
    EquityOrder.Limit(EquityInstruction.Buy, "AAPL", 10, 150.25m));

// placed.Value is the order id; persist placed.RefreshedCredentials when it is not null.
```
