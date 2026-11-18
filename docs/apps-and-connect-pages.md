# Apps and connect pages

[Documentation](README.md) › Apps and connect pages

An app, such as Scry, links to Nexus by pairing, with no key copied by hand:

1. The app's server calls `POST /pairings` with its name, its push API's address, the callback for the admin's
   return, its connect pages' return address, and the SHA-256 hash of a verifier it keeps. Nexus answers the
   approval page's address, `/pair?id=...` on the client.
2. An admin signs in there, picks a workspace, and approves. Nexus sends them to the callback with `?pairing=<id>`,
   or with `?error=declined`.
3. The app's server calls `POST /pairings/{id}/claim` with the verifier and a push key for its push API. Nexus
   checks the key, makes the app and its destination, and answers the app's key, once.

A pairing lasts 10 minutes and is claimed once. `POST /pairings` and the claim take no session, since only
the app's server calls them. An admin can still make an app by hand on its workspace's page.

The app sends its key as `Authorization: Bearer nexus_...`. A key reaches only the routes below, and only the
syncs into its own app's destinations.

| Route | Does |
|---|---|
| `GET /app` | The app itself: its name, its workspace, its destinations with their ids, and its return addresses |
| `DELETE /app/key` | Revokes the app's own key, as an app does when it unlinks |
| `GET /connectors` | The connectors and the settings each asks for, each with `notReady`: why the app's workspace cannot connect it yet, such as a sign-in app it has not set |
| `POST /connect-sessions` | From `{"connector", "destinationId", "returnUrl"}`, opens a connect page and answers its address. The destination must be the app's, and the return address one an admin added to the app. With `connectionId`, a connection in the app's workspace that signs in, the page signs it in again and returns with `?connection=`. |
| `GET /syncs` | The app's syncs, each with how its last run went and whether its connection needs a new sign-in |
| `POST /syncs/{id}/run`, `DELETE /syncs/{id}` | Runs one of the app's syncs now, or removes it |

A connect page is for one person, who needs no user in the instance. It lasts 10 minutes, makes one connection
in the app's workspace, adds the syncs the person picks into the app's destination, and sends them back to the
return address with `?connection=<id>`, or `?error=cancelled` when they cancel. The page's address carries its
token after the `#`, and the page sends it as `Authorization: Bearer nxc_...` to `GET /connect`,
`POST /connect/connection`, `GET /connect/streams`, `POST /connect/syncs`, and `POST /connect/cancel`.
`POST /connect/connection` takes [the same form with the file](service.md) for a connector that reads one.

A request that carries an `Authorization` header skips the antiforgery check, since another site's page cannot
send one without a CORS preflight, which the service answers for its client's origin alone.
