# The service

[Documentation](README.md) › The service

`Server/src/Nexus.Service` keeps users, workspaces, apps, their destinations, connections, and syncs in a MySQL
database of its own, and runs each sync by reading its stream through a source connector and writing it
through `PushDestination`. It reads through the MySQL, PostgreSQL, SQL Server, and CSV sources. `Client/` holds its
pages.

- A connection whose connector signs in, such as Schwab, is made by signing in. The sign-in starts from the
  workspace page (`POST /workspaces/{id}/sign-ins` with `{name, connector, settings}`), from a connect page
  (`POST /connect/sign-in` with `{name, settings}`), or to renew a connection (`POST /connections/{id}/sign-in`),
  and each answers the provider's sign-in page. The provider sends the browser back to `GET /oauth/callback`,
  which keeps the tokens, encrypted, and sends it on: to the connect page to pick streams, to the workspace page
  with `?signedIn=`, or with `?error=` and the reason. A sign-in the service no longer holds goes to the Workspaces
  page, with its `error`. Schwab's setting that allows orders is never offered,
  since the service only reads.
- Each workspace signs in through its own app at the provider, such as its Schwab app, which its members set under
  Sign-in apps on the workspace page, beside the callback to register on it. The app's secret is encrypted and
  never shown again. A connector whose app is not set says so wherever it is offered, in place of signing in, and a
  sync of it fails and says so.
- A sign-in under way is kept in the database for 10 minutes, by a hash of the `state` the provider's redirect
  returns, so a restart of the service loses none. Each is used once.
- A connection whose connector reads a file, such as CSV, is made from a `multipart/form-data` upload: `name`,
  `connector`, `settings` as a JSON object of text, and `file`, up to 1 GB. The file is kept in `data/files` as the
  connection's own copy, kept only once the connector reads it, and deleted with the connection.

- A worker runs queued syncs one at a time. A full refresh overwrites its table. An incremental sync upserts
  by the stream's primary key, or appends when there is none, and resumes from the last checkpoint the
  destination committed.
- A `Transient` or `RateLimited` failure runs again after 1, 5, and 15 minutes, or after a longer wait the
  provider asked for, then stops. An `AuthExpired` failure marks the connection as needing a sign-in.
- A sync that was queued or running when the service stopped runs again when it starts.
- Each run tells the push API where the table's rows come from: the workspace, the connection, its connector,
  and the stream.
- Removing a destination, or the app it belongs to, leaves its syncs in place without one. Such a sync does not
  run until someone picks another destination for it, and a run already queued fails and says so.
- Connection settings, tokens, push keys, and mailed links are protected with ASP.NET Data Protection. Its
  keys live in `data/keys`, beside the service, outside the database they protect.
