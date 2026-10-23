# Project working agreement

## Local and GitHub updates

The user has requested that completed project updates be maintained both locally
and in https://github.com/paciguard/CFLDetectools.

- Implement changes in this local checkout.
- Run `./scripts/verify.ps1` before publishing code changes. It builds the solution
  and runs the executable behavior checks; `dotnet test` does not run those checks.
- Review the changes, commit the files belonging to the requested work, and push
  the current branch to `origin` after local validation succeeds, unless the user
  explicitly asks for local-only work. Do not include unrelated user changes.
- Check the GitHub Actions result for the pushed commit and report any failures
  or blocked publication honestly. Do not claim remote success from local results.
- Use normal pushes; do not force-push or discard remote changes.
- Keep credentials, local machine settings, build output and site-specific PLC
  configuration out of Git. Equipment protection remains in the PLC program.

## Application

The desktop application uses C#/.NET 8 and WPF. The current communication client
is an in-memory simulator. Add a live Siemens driver only after the protocol,
hardware details, signal map and command handshake are known.
