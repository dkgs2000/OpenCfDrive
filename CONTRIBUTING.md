# Contributing to OpenCfDrive

Thanks for helping. This page covers how to report a problem and what a pull
request needs.

## Reporting a bug

Open an issue with the **Bug report** form. It asks for:

- the OpenCfDrive and rclone versions (the app's About page shows both);
- the server: its software and version, the storage behind it, and anything
  between OpenCfDrive and the server, such as a reverse proxy, a CDN or a load
  balancer;
- what you did, what happened, and what you expected;
- the lines of the sync service log around the problem, from
  `%LOCALAPPDATA%\OpenCfDrive\logs\OpenCfDrive.log`. Remove any names you
  don't want public.

Most sync problems depend on the server, so the server details matter most.

## Security problems

Please don't open a public issue. Report it privately from the repository's
**Security** tab (**Report a vulnerability**).

## Before you write code

For anything bigger than a small fix, open an issue first and describe the
problem, so we can agree on the cause before working on a fix.

## What a pull request needs

- **One change per pull request.**
- **The problem first.** Describe what went wrong, with an example (steps, a
  log excerpt or a linked issue), then how the change fixes it. The pull
  request template asks for both.
- **Tests that ran.**
  - `dotnet test .\OpenCfDrive.slnx` passes on your machine. It needs
    Windows, the .NET 10 SDK and rclone (see [Development](docs/development.md)).
  - A change to how files sync comes with a test that fails without it: a unit
    or contract test, or an end-to-end scenario (see [Testing](docs/testing.md)).
  - CI runs the end-to-end suites on WebDAV and S3 for every pull request. For
    a first-time contributor, it runs once a maintainer approves it. Running
    the suites yourself is welcome but not required.
  - Say in the pull request what you ran.
- **Docs.** When behavior changes, update the [user guide](docs/user-guide.md)
  or [architecture](docs/architecture.md) to match.

## Rules every change keeps

[Architecture](docs/architecture.md) explains these in detail.

- **Sync behaves like Nextcloud's and OneDrive's clients.** A change to how
  conflicts, deletes, renames or offline edits are handled says what those
  clients do in the same case, and matches it.
- **No data is lost.** When it's unclear which version is the right one, both
  are kept.
- **Only rclone talks to the storage.** All server access goes through rclone's
  remote control API. There's no code for one particular server or protocol,
  and a change has to work on WebDAV and on S3.
- **Few server requests.** Every end-to-end scenario declares the most it may
  cost: uploads, downloads, renames, deletes and lookups. A change that costs
  more raises the limit and says why.
- **No sleeps.** Code and tests wait for an event or a state, never for a fixed
  time or in a sleep-and-check loop.

## AI-assisted contributions

Much of OpenCfDrive was written with an AI coding tool, and contributions made
that way are welcome. Say so in the pull request. You're responsible for the
change as if you had written it by hand: you've run the tests, and you can
explain what it does and why.

## License

OpenCfDrive is released under the [MIT License](LICENSE). By opening a pull
request, you agree that your contribution is released under the same license,
and you confirm that you have the right to release it.
