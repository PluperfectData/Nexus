# Nexus documentation

The detail behind Nexus: how each part behaves, every setting and route, and how the code is laid out. To learn what
Nexus is and get it running, start with the [README](../README.md).

## Running an instance

- **[Running Nexus](running.md)**: starting it on your machine, and the installer that sets an instance up
  - [Starting the service and the client](running.md#starting-the-service-and-the-client)
  - [The installer](running.md#the-installer)
- **[Users and sign-in](users-and-sign-in.md)**: invites, mailed links, passwords, sessions, and finding your way around the pages
- **[Apps and connect pages](apps-and-connect-pages.md)**: pairing an app such as Scry, its key, and the connect pages it sends people to
- **[Logs](logs.md)**: the Log page, who sees what, and the developer log
- **[Configuration](configuration.md)**: every setting configuration can set in place of the pages

## How the service works

- **[The service](service.md)**: what it keeps, how connections sign in or take a file, and how syncs run and retry
- **[The API](api.md)**: every route the service's pages call

## Connectors

- **[Connectors](connectors/README.md)**: what each connector reads or writes
- **[The connector contract](connectors/contract.md)**: the interfaces, column types, errors, secrets, files, and retries every connector shares
  - [Actions](connectors/contract.md#actions)
  - [Column types](connectors/contract.md#column-types)
  - [Errors](connectors/contract.md#errors)
  - [Secrets](connectors/contract.md#secrets)
  - [Files](connectors/contract.md#files)
  - [HTTP](connectors/contract.md#http)
- **[CSV](connectors/csv.md)**: reading a CSV file
- **[MySQL, PostgreSQL, and SQL Server](connectors/sql.md)**: reading tables and views from a database
- **[Charles Schwab](connectors/schwab.md)**: balances, positions, transactions, orders, and market data, and placing orders
  - [The sign-in tool](connectors/schwab.md#the-sign-in-tool)
- **[DuckDB](connectors/duckdb.md)**: writing streams into a DuckDB file
- **[Push API](connectors/push.md)**: writing streams through an app's push API, such as Scry's
- **[Using the connectors in code](connectors/usage.md)**: reading into memory, piping a source into a destination, and running an action

## Working on Nexus

- **[How Nexus is built](architecture.md)**: the repository's projects, and the checkouts the service builds against
  - [The repository](architecture.md#the-repository)
  - [Sibling checkouts](architecture.md#sibling-checkouts)
- **[Building and testing](building.md)**: the SDK, the tests, and the database servers the SQL tests need
