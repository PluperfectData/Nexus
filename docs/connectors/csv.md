# CSV

[Documentation](../README.md) › [Connectors](README.md) › CSV

`CsvSource`, id `csv`, auth `FileUpload`, on Sylvan.Data.Csv. One file, one stream.

| Key | Kind | Notes |
|---|---|---|
| `file` | File | Required. Passed in `ConnectorConfig.Files`. |
| `stream` | Text | Required. The stream's name. |
| `header` | Boolean | Default `true`. Without a header, columns are `column1` to `columnN`. |
| `delimiter` | Choice | `Comma` (default), `Semicolon`, `Tab`, `Pipe`. |

- UTF-8, unless a byte order mark names another encoding.
- Discovery reads the file once. A read passes over it twice: once to infer types, once to yield rows.
- Full refresh only, in batches of 10,000 rows, then a `StateCheckpoint` of `{}`. No primary key, so write
  with `Overwrite` or `Append`.
- Empty fields are null in every column. A column with an empty field is nullable; a column of only empty
  fields is a nullable `String`. Blank lines are skipped.

Each column takes the first type every non-empty value fits. Formats are invariant: no thousands separators,
no locale dates.

| `ColumnType` | Accepted text |
|---|---|
| `Boolean` | `true`, `false`, in any case |
| `Int64` | An optional sign and digits |
| `Decimal(p, s)` | An optional sign, digits, and `.`, at most 28 digits per value. `p` and `s` are the widest seen; `p` is at most 38. |
| `Float64` | A number with an exponent, or one too long for `Decimal` |
| `Date` | `yyyy-MM-dd` |
| `Timestamp` | `yyyy-MM-ddTHH:mm:ss[.fffffff]` and `Z` or `±hh:mm`. A space may replace the `T`. |
| `DateTime` | `yyyy-MM-ddTHH:mm[:ss[.fffffff]]`, no zone. A space may replace the `T`. |
| `String` | Anything else |

| Failure | `ConnectorErrorKind` |
|---|---|
| No file, no `stream`, an invalid `header` or `delimiter`, an empty file, a missing file, an incremental read | `ConfigInvalid` |
| Any other `IOException` reading the file | `Transient` |
| A row with the wrong field count, malformed CSV, a repeated column name, a value that changed between passes | `SourceError` |
