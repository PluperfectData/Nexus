# Logs

[Documentation](README.md) › Logs

The service keeps two logs.

**The Log page**, `/log` on the client, lists what happened, newest first, each in a sentence that names its
workspace, connection, or sync:

- each run, with the rows it sent or its error, and when it tries again
- a connector's own warnings, a sign-in about to end, and a connection that needs a new sign-in
- each change someone made, with who made it: an email, or `app` and the app's name

An admin sees every entry, and a member sees their own workspaces'. Entries about users, settings, and the
installer belong to the whole instance, so only admins see them. Each connection and table on the workspace page
links to its own entries. A removed workspace's entries stay, as entries about the whole instance.

Entries are kept in the database for 30 days, a number an admin changes on the Settings page, from 1 to 365. Signing
in or out, reading anything, and the install code write no entry.

**The developer log** goes to the console and to a file a day in `data/logs`, `nexus-<date>.log`, keeping 14. It
has every Log page entry, under `Nexus.Operator`, one line for each request, and the framework's detail and the
stack traces the Log page leaves out. Its levels come from `Serilog:MinimumLevel`: Information, with
`Microsoft.AspNetCore` and `Microsoft.EntityFrameworkCore` at Warning. For more detail, set
`Serilog__MinimumLevel__Default` to `Debug`.
