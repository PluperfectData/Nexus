# Users and sign-in

[Documentation](README.md) › Users and sign-in

There is no sign-up. An admin invites each user by mail, and makes and removes workspaces, their members,
and apps. A member sees their own workspaces and changes their connections and syncs.

The client's `/` opens the workspace last opened in the browser, or the first by name, and the Workspaces page, at
`/workspaces`, when there is none. The header's workspace menu names the workspace and switches between them, with
Manage workspaces for admins. Admins reach Users, Workspaces, and Settings from the account menu, and the header's row
holds the Log, which moves behind the hamburger below 30rem.

The first admin comes from [the installer](running.md#the-installer).

- A user signs in with a password, or with a link mailed to them that works once, for 15 minutes. An invite
  link works once, for 7 days. Mail goes through `Pluperfect.Mail`'s Postmark transport, with the token set on the
  Settings page. A Debug build running as Development writes `.eml` files to `data/mail` instead, until a Postmark
  token and sender are saved to test real mail; turning Postmark off returns it to the files. A released service never
  writes mail to files. An instance without mail offers no mailed links, and an invite answers its link for the admin
  to pass on.
- The session is `Pluperfect.Jwt`'s: an access token in an `HttpOnly` cookie, a refresh token that rotates,
  and the origin and antiforgery checks on every unsafe request. Every route needs a session but `/health`
  and the sign-in routes under `/auth`.
- Five wrong passwords for one address lock it for 15 minutes, and one address gets at most three mailed
  links in that time.
- Setting a password ends the user's other sessions. A forgotten password is replaced by signing in with a
  mailed link and setting a new one.

The routes behind these pages are in [The API](api.md).
