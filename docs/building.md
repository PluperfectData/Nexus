# Building and testing

[Documentation](README.md) › Building and testing

.NET SDK 10.0.302 or a later feature band (`Server/global.json`).

```bash
dotnet build Server/Nexus.slnx
```

```bash
dotnet test Server/Nexus.slnx
```

Restores are locked to `packages.lock.json`. After a dependency change, run
`dotnet restore --force-evaluate` and commit the lock files.

`Server/tests/Nexus.Schwab.Tests` answers every request from canned responses and needs no Schwab account.
