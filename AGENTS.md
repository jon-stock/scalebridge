# Agent instructions for ScaleBridge

## Local builds do not work in this environment

This machine's Group Policy blocks the Android workload installer's `dotnet.exe` subprocess, and
the Android SDK/JDK are not installed here. `dotnet build`/`dotnet publish` on
`src/ScaleBridge/ScaleBridge.csproj` will always fail here with `NETSDK1147` ("workloads must be
installed: android") - this is expected, not a regression to chase. See
`docs/PROTOCOL_CONFIRMATION.md` ("Build environment limitations") and `docs/SETUP.md` ("Building
via CI") for the full history.

## Push to GitHub after every code change, to actually get a build

The only way to compile this app (and catch real compile errors, especially anything touching the
Health Connect binding surface in `src/ScaleBridge/Health/`) is `.github/workflows/build-apk.yml`,
which runs on a GitHub-hosted runner unaffected by the local Group Policy restriction.

**After making any code change to this project (not just docs), commit and push it so CI actually
builds it** - do not treat "the code looks correct" as equivalent to "it builds". This project's
own history (`docs/PROTOCOL_CONFIRMATION.md`) shows the Health Connect/Kotlin binding surface in
particular has repeatedly compiled fine by inspection but failed in real CI/device runs for
non-obvious binding-generator reasons; assume the same risk for any change there or elsewhere
until CI has actually confirmed it.

Concretely, once implementation work is done in a session:

1. Review the diff (`git status`/`git diff`) as usual before committing.
2. Commit and push to the branch CI builds from (check `build-apk.yml`'s `on:` trigger - currently
   `main`, or trigger it manually from the repo's **Actions** tab if working on another branch).
3. Point the user at the repo's **Actions** tab to watch the run and download the signed
   `ScaleBridge-apk` artifact once it finishes.

This does not override the general rule of asking before force-pushing, rewriting history, or
touching git config - it only means "push a normal commit" should be treated as a required,
expected step for finishing code-change work on this specific project, not an optional follow-up
suggestion, since there is no other way to verify the change compiles at all.

## Health Connect binding changes specifically

If a change touches `src/ScaleBridge/Health/` (or otherwise calls into
`androidx.health.connect:connect-client`), do not guess at the .NET binding's generated member
names/signatures from Kotlin source alone - `docs/PROTOCOL_CONFIRMATION.md` documents many rounds
of wrong guesses there. Prefer, in order:

1. Download and read the exact pinned version's `-sources.jar` from
   `https://maven.google.com/<group-path>/<artifact>/<version>/<artifact>-<version>-sources.jar`
   (the version is pinned in `ScaleBridge.csproj`'s `AndroidMavenLibrary` reference) to confirm the
   real Kotlin API shape before writing C# against it.
2. After a CI run, download the `health-connect-binding-dump` artifact from `build-apk.yml` (copies
   out the actual generated C# binding source) to confirm exact C# member names/signatures if the
   Kotlin source alone leaves ambiguity (e.g. `@JvmStatic`/`@JvmOverloads` bridging behaviour).

Wrap genuinely new/unverified Health Connect calls defensively (try/catch with a graceful
fallback and `CrashLog.Record`) until confirmed working end-to-end on a real device, consistent
with the existing pattern in `HealthConnectWriter.cs`/`MainActivity.cs`.
