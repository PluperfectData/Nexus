# Contributing to Nexus

Thank you for helping. This page covers the terms that come with contributing, and how a change gets from your
machine into Nexus.

## Contents

- [Contributor terms](#contributor-terms)
- [Before you start](#before-you-start)
- [Fork, branch, and open a pull request](#fork-branch-and-open-a-pull-request)
- [What a pull request needs](#what-a-pull-request-needs)
- [Review and merging](#review-and-merging)

## Contributor terms

Nexus is licensed under the GNU Affero General Public License, version 3 only (AGPL-3.0-only).

By contributing to Nexus, whether in a pull request, a patch, or code or text offered in an issue, you agree that:

1. your contribution is licensed to the project under AGPL-3.0-only;
2. you give Nexus's maintainer permission to relicense your contribution under any other terms, including commercial
   and proprietary licenses, without asking you again; and
3. the contribution is yours to give: you wrote it, or you have the right to submit it under these terms.

If you don't want to give that permission, please don't contribute to Nexus while it remains under this license.

## Before you start

- **A small fix**, such as a typo or a bug with an obvious cause: open a pull request.
- **Anything bigger**, such as a new feature, a new connector, a change to how something behaves, or a new package:
  open an issue first and describe what you have in mind, so we can agree on the approach before you build it.
- **A bug you can't fix yourself**: open an issue with what you did, what you expected, and what happened instead.

## Fork, branch, and open a pull request

1. Fork Nexus on GitHub.
2. Clone your fork beside the repositories Nexus builds against, as the README's [quick start](README.md#quick-start)
   lays out, using your fork's address in place of Nexus's.
3. Make a branch from `main`, named for your change, such as `fix-csv-dates` or `add-stripe-source`. Keep one change
   to a branch.
4. Make the change, with its tests, and run the checks below.
5. Push the branch to your fork, and open a pull request into `main`. Say what it changes and why, and link the issue
   it answers.

## What a pull request needs

CI runs these on every pull request, and they must pass. Run them first, from `Pluperfect.Data/Nexus`:

```bash
dotnet test Server/Nexus.slnx
```

```bash
npm test --prefix Client
npm run lint --prefix Client
npm run build --prefix Client
```

The SQL connectors' tests need a MySQL, a PostgreSQL, and a SQL Server server. [Building and testing](docs/building.md)
says how to point the tests at yours. CI runs all three.

- **Tests.** A change to a connector gets an NUnit test in its project under `Server/tests`, and a change to the
  service gets one in `Server/tests/Nexus.Service.Tests`, which runs the real service over a SQLite file. A change to
  the client gets a Jest test beside the component, hook, or page it changes, with everything that unit talks to
  mocked.
- **No warnings.** The build treats every warning as an error.
- **Phones.** Every page works at 360 pixels wide. Check any page you change at that width.
- **Docs.** When a change alters how something behaves, change the page in [`docs/`](docs/README.md) that describes it.
  A new connector gets a page in `docs/connectors` and a row in the README's [Connectors](README.md#connectors) table.
- **Packages.** A new package needs a license that works with AGPL-3.0, and a row in the README's
  [Built with](README.md#built-with) tables, in the same pull request. Say in the pull request why it's needed.
- **Lock files.** Restores are locked. After changing any project's packages, run `dotnet restore --force-evaluate`
  and commit the `packages.lock.json` files.

## Review and merging

- A maintainer reviews each pull request, and may ask for changes. Push them to the same branch.
- Pull requests are squash-merged: the branch's commits become one commit on `main`, titled with the pull request's
  title. Write the title as you'd want it to read in the history, such as "Add the Stripe source's invoices stream".
