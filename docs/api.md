# The API

[Documentation](README.md) › The API

The routes the service's own pages call. The routes an app calls with its key, and a connect page's, are in
[Apps and connect pages](apps-and-connect-pages.md).

| Route | Does |
|---|---|
| `GET /install`, `POST /install/database`, `POST /install/address`, `PUT` and `DELETE /install/mail`, `POST /install/mail/test`, `POST /install/admin`, `POST /install/code` | [The installer](running.md#the-installer). Its mail routes answer 409 once the instance is set up, and its test mail goes to `{"to"}`. |
| `/settings`, `/settings/addresses`, `/settings/mail`, `POST /settings/mail/test`, `/settings/database`, `PUT /settings/log` | Admins see and change the instance's addresses, mail, database, and how many days the log keeps an entry, and send a test mail |
| `GET /log?workspace=&connection=&sync=&level=&before=&instance=` | The [Log page](logs.md)'s entries: newest first, 100 at a time, with `next` for the older ones. `level=Warning` keeps warnings and errors, and an admin's `instance=true` keeps the entries about the whole instance. |
| `POST /auth/sign-in`, `POST /auth/link`, `POST /auth/link/redeem` | Signs in with a password, mails a link, or signs in with one |
| `GET /auth/me`, `GET /auth/csrf`, `POST /auth/refresh`, `POST /auth/sign-out`, `PUT /auth/password` | The session and the signed-in user's password |
| `/users` | Admins invite, change, and remove users. An invite that is not mailed answers its link. |
| `/workspaces`, `/workspaces/{id}/members/{userId}` | Workspaces and their members |
| `/workspaces/{id}/apps`, `/apps/{id}`, `/apps/{id}/key`, `/apps/{id}/destinations`, `/destinations/{id}` | Apps, their keys, and their destinations. A key shows once, when it is made, and an app lists when its key was made. A destination is kept only once its key works, and records the push key's name and prefix. |
| `POST /destinations/{id}/check` | Asks the push API whether it still takes the destination's key, and records the key's name and prefix, or why it was refused |
| `GET /connectors` | The connectors and the settings each asks for |
| `GET /workspaces/{id}/providers`, `PUT` and `DELETE /workspaces/{id}/providers/{connector}` | The workspace's app for each provider it signs in to, from `{clientId, clientSecret}`: whether it is set, the last four characters of its key, the callback to register, and why the workspace cannot sign in yet. A blank key or secret keeps the one stored. |
| `/workspaces/{id}/connections`, `/connections/{id}`, `GET /connections/{id}/streams` | Connections, kept only once their settings connect, and their streams. A connection that reads a file is made from a form with the file. A connection that signs in carries `signIn`: its `kind`, `signedInAt`, `endsAt`, `endingSoon`, `ended`, `endedAt`, `keptAliveAt`, and the labels of its `accounts`. |
| `GET` and `PUT /workspaces/{id}/reminder-email` | Who the workspace mails about its sign-ins, from `{choice, people}`: `Off`, `Everyone`, `Admins`, or `Named` with the people's user ids, among the `candidates` it answers. It answers `mailReady` too. |
| `PUT /connections/{id}/file` | Replaces the file a connection reads, from a form with `file`, once the connector reads it, and queues a run of each of its syncs |
| `/workspaces/{id}/syncs`, `POST /connections/{id}/syncs`, `POST /syncs/{id}/run`, `GET /syncs/{id}/runs`, `DELETE /syncs/{id}` | Syncs, their runs, and running one now. Each sync carries its connection's `signIn`, and running one whose connection needs a sign-in answers 409. |
| `PATCH /syncs/{id}` | Changes a sync's destination, table name, and read mode from `{destinationId, table, mode, cursorField}`, and queues a run that reads from the start. The table it wrote before stays. |
| `GET /pairings/{id}`, `POST /pairings/{id}/approve`, `POST /pairings/{id}/decline` | Admins see a pairing and approve it into a workspace, or decline it |
