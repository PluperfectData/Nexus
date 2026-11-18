# Push API

[Documentation](../README.md) › [Connectors](README.md) › Push API

`PushDestination`, id `push`, writes each stream to a table of the same name through an app's push API, such
as Scry's. Settings: `url`, the push API's address, such as `http://localhost:6161/ingest`; `key`, a push
API key, which is secret; and `origin`, optional, which says where the rows come from and goes with each
table's columns when it is made or replaced. `IdentifyAsync` calls `GET {url}`, which names the key, and answers
its name and prefix. `CheckAsync` does the same and answers whether it worked.

| Write mode | Requests |
|---|---|
| `Append` | `PUT {url}/sources/{table}` with the columns and key, then batches with `mode: append` |
| `Upsert` | The same `PUT`, then batches with `mode: merge`. Requires `PrimaryKey`. |
| `Overwrite` | `POST {url}/sources/{table}/replace` with the columns, after a `PUT` when the table is new, then batches. The app swaps the replacement in on `POST .../commit` when the write ends, and the stream's checkpoints are yielded after it. |

- Before each other checkpoint, every row waiting is sent and the stream is committed.
- A batch holds at most 5,000 rows and 8 MB. Each carries an id unique to the write, so the app skips one
  sent twice.
- Values go in the forms the push API reads: a decimal as text, a timestamp with its offset, a date-time
  with none, a date as `yyyy-MM-dd`, JSON as itself, and bytes as base64. A double that is not finite, or JSON
  text that does not parse, is a `SourceError`.

| Failure | `ConnectorErrorKind` |
|---|---|
| An address that is not http or https, or no key | `ConfigInvalid` |
| 401, 403, and a 404 outside a replace | `ConfigInvalid` |
| 409, a column that changed or went | `SourceError`, saying a full refresh replaces the table |
| 429, after `HttpRetry`'s tries | `RateLimited` |
| 5xx, a timeout, or no answer, after `HttpRetry`'s tries | `Transient` |
| Any other answer | `SourceError`, carrying the app's detail |
