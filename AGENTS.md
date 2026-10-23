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
is an in-memory simulator. The user confirmed the target PLC family is S7-200
SMART, programmed with STEP 7-Micro/WIN SMART. S7-PLCSIM V14 is not a simulator
for this CPU family. See `plc/S7-200-SMART.md` for the integration plan.

The user plans to develop and debug hardware on another Windows laptop later.
Keep project paths relative and document setup from a clean Git clone. Do not
assume this machine's Siemens installation or network settings exist there.

Add a live Siemens driver only after the protocol, exact CPU model, connection
details, signal map and command handshake are known. Start hardware verification
with reads; do not infer authorization to start physical pumps from permission
to try the installed engineering software.
