# DuckDB

[Documentation](../README.md) › [Connectors](README.md) › DuckDB

`DuckDbDestination`, id `duckdb`. Setting `path`: the database file, created if absent.

| Write mode | Behavior |
|---|---|
| `Append` | Inserts every row. |
| `Upsert` | Deletes target rows whose primary key is staged, then inserts the last staged row per key. Requires `PrimaryKey`. |
| `Overwrite` | Loads `__nexus_overwrite_<stream>`, then replaces `<stream>` in one transaction when the write ends. The stream's checkpoints are yielded after the replace. |

- One table per stream, named for the stream. A column the source adds is added with
  `ALTER TABLE ADD COLUMN`; none are dropped. No `NOT NULL` or key constraints.
- Rows are appended to a temp stage table per stream and merged into the target by column name at each
  checkpoint, in one transaction. A failed write rolls back to the last checkpoint.
- Tables prefixed `__nexus_` are internal.

| Failure | `ConnectorErrorKind` |
|---|---|
| Open: another process holds the file | `Transient` |
| Open: any other failure | `ConfigInvalid` |
| `DuckDBErrorType` `Conversion`, `OutOfRange`, `Decimal`, `MismatchType`, `Constraint`, `InvalidInput`, `ObjectSize`; a value overflowing its column | `SourceError` |
| `DuckDBErrorType` `Transaction`, `OutOfMemory`, `Io`, `Interrupt` | `Transient` |
| `DuckDBErrorType` `Permission` | `ConfigInvalid` |
| Any other `DuckDBErrorType` (`Binder`, `Catalog`, `Parser`, `Internal`, ...) | Not classified; `DuckDBException` propagates |

The original exception is `InnerException`.

| `ColumnType` | DuckDB type |
|---|---|
| `String` | `VARCHAR` |
| `Int64` | `BIGINT` |
| `Decimal` | `DECIMAL(p, s)`; precision 0 becomes 38 |
| `Float64` | `DOUBLE` |
| `Boolean` | `BOOLEAN` |
| `Date` | `DATE` |
| `Timestamp` | `TIMESTAMPTZ` |
| `DateTime` | `TIMESTAMP` |
| `Time` | `TIME` |
| `Json` | `JSON` |
| `Binary` | `BLOB` |
