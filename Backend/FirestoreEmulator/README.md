# Isolated Firestore regression environment

This tooling is test-only. Never run the production API or Program.Main for these
checks. No Firebase login, service-account key, or Application Default Credentials
are required. No Auth emulator or background worker is started.

## Install and build (from repository root)

Requirements: .NET 8 runtime and a compatible SDK, Node.js (tested with 24), Java 21.

```powershell
npm ci --prefix Backend/FirestoreEmulator
dotnet build Backend/WebChatEIU.IntegrationTests/WebChatEIU.IntegrationTests.csproj -p:NuGetAudit=false
```

The CLI is pinned to 15.34.2 in package.json/package-lock.json. Initial installation
requires npm downloads. The first emulator run downloads a Firestore emulator
binary to the Firebase CLI's local cache. Neither operation uses production data.
Installed dependencies, emulator caches/JARs, logs, and emulator-data/export folders
are ignored by Git. Test results are directed into the ignored integration-project
`obj/TestResults` directory. Only package manifests and the lockfile are versioned.
NuGetAudit=false avoids the previously unavailable package vulnerability feed;
it does not upgrade package versions. Vulnerability auditing is separate.

## Recommended: start, verify, and stop automatically

```powershell
& ./Backend/FirestoreEmulator/Run-Smoke.ps1
```

This sets process-scoped test environment variables, clears inherited service-account
and Firebase CLI token variables temporarily, runs only the separate integration
project via emulators:exec, then shuts the emulator down and restores environment
variables. It does not run the production solution or existing offline project.
Do not interrupt the wrapper unless necessary; ensure the CLI reports shutdown.

## Manual operation

Terminal A:

```powershell
cd Backend/FirestoreEmulator
npm start
# Stop with Ctrl+C; wait for the emulator shutdown messages.
```

Terminal B (repository root, dedicated disposable terminal):

```powershell
$env:EZONE_TEST_PROJECT_ID = 'demo-ezone-regression'
$env:FIRESTORE_EMULATOR_HOST = '127.0.0.1:8085'
$env:GOOGLE_APPLICATION_CREDENTIALS = $null
dotnet test Backend/WebChatEIU.IntegrationTests/WebChatEIU.IntegrationTests.csproj --no-build --no-restore
# Close this dedicated terminal after the run.
```

No Firebase login/deploy/export/import commands are needed. Never substitute the
production project. No emulator data is exported; smoke documents are deleted in
finally, and emulator state is disposable on shutdown. Do not reset a shared
emulator database; the fixture owns only its unique regressionSmoke document.

## Safety boundaries

- Dedicated project ID must exactly equal demo-ezone-regression. Missing IDs,
  ezone-afa80, and all other projects are rejected before RPCs.
- FIRESTORE_EMULATOR_HOST is mandatory. Only literal 127.0.0.1:port and [::1]:port
  endpoints with valid ports are accepted; DNS names (including localhost), remote
  IPs, wildcard addresses, URL schemes, paths, and userinfo are rejected.
- Client construction revalidates environment and explicitly uses
  EmulatorDetection.EmulatorOnly. There is no production fallback or ADC lookup.
- The SDK uses an unauthenticated emulator transport; no credentials are assigned.
- CLI Firestore binds 127.0.0.1:8085; hub 127.0.0.1:4405; logging 127.0.0.1:4505.
  Its auxiliary Firestore websocket also uses 127.0.0.1:9150 even with UI disabled.
  All four ports must be free before starting.
- firestore.rules denies client SDK access. The server Firestore SDK bypasses
  rules, as expected; this suite tests server persistence, not security rules.
- Global emulator environment is not mutated by tests. The collection disables
  parallel execution for shared emulator fixtures.
- Smoke requests have timeouts and cleanup has a separate timeout. A failed or
  unavailable emulator must fail the suite, never silently skip or use production.
- Do not add this project to normal offline solution execution unintentionally.

## Run independently

Existing offline suite (111 cases, no emulator):

```powershell
dotnet test Backend/WebChatEIU.Tests/WebChatEIU.Tests.csproj --no-build --no-restore
```

Safety validation only (no emulator, no client construction):

```powershell
dotnet test Backend/WebChatEIU.IntegrationTests/WebChatEIU.IntegrationTests.csproj --no-build --no-restore --filter Category=Safety
```

Integration coverage in this phase is deliberately limited to endpoint/project
safety guards and one disposable create/read/delete smoke test. Matching,
reservations, transactions, SignalR, Auth, and workers are not exercised yet.
The emulator does not prove production index requirements or contention behavior.

References:
- https://docs.cloud.google.com/dotnet/docs/reference/help/emulators
- https://firebase.google.com/docs/emulator-suite/connect_firestore
