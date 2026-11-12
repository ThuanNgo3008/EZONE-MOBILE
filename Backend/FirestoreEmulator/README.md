# Isolated Firebase regression environment

This tooling is test-only. Never run the production API or Program.Main for these
checks. No Firebase login, service-account key, or Application Default Credentials
are required. Only local Firestore and Auth emulators are started; no API or hosted
background worker is started.

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
and Firebase CLI token variables temporarily, sets both emulator hosts, starts
Firestore and Auth, runs only the separate integration
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
$env:FIREBASE_AUTH_EMULATOR_HOST = '127.0.0.1:9099'
$env:GOOGLE_APPLICATION_CREDENTIALS = $null
dotnet test Backend/WebChatEIU.IntegrationTests/WebChatEIU.IntegrationTests.csproj --no-build --no-restore
# Close this dedicated terminal after the run.
```

No Firebase login/deploy/export/import commands are needed. Never substitute the
production project. No emulator data is exported; smoke documents and accounts are
deleted in finally, and emulator state is disposable on shutdown. Do not reset a
shared emulator database; fixtures delete only their uniquely owned test data.

## Safety boundaries

- Dedicated project ID must exactly equal demo-ezone-regression. Missing IDs,
  ezone-afa80, and all other projects are rejected before RPCs.
- FIRESTORE_EMULATOR_HOST is mandatory. Only literal 127.0.0.1:port and [::1]:port
  endpoints with valid ports are accepted; DNS names (including localhost), remote
  IPs, wildcard addresses, URL schemes, paths, and userinfo are rejected.
- Client construction revalidates environment and explicitly uses
  EmulatorDetection.EmulatorOnly. There is no production fallback or ADC lookup.
- The Firestore SDK uses an unauthenticated emulator transport; no credentials are assigned.
- CLI Firestore binds 127.0.0.1:8085; hub 127.0.0.1:4405; logging 127.0.0.1:4505.
  Its auxiliary Firestore websocket also uses 127.0.0.1:9150 even with UI disabled.
  Auth binds 127.0.0.1:9099. All five ports must be free before starting.
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

The integration project now includes 22 safety cases, one disposable smoke test,
81 Random Match/reservation cases, and 39 lifecycle/reporting/cleanup cases.
Run-Smoke.ps1 runs the complete integration project, including these cases. They instantiate the production queue and
reservation services directly with the guarded emulator database; no API is started.

Matching coverage includes queue presence, pagination beyond 20 candidates,
reservation/session ownership, the 30-second deadline, two-sided Ready, retries,
cancellation, controlled expiration and requeue rules, and replacement-session
protection. Each test owns unique users and deletes only documents referring to
those users. No business collections are renamed or given extra fixture fields.

Each of the three race scenarios (competing reservations, concurrent Ready, and
Ready versus Cancel) has three separately seeded trials with a shared start gate
and a 90-second wait bound. Transaction calls are not mocked. Because current
service methods expose no cancellation token, the wait bound does not cancel an
in-flight SDK operation; stop the disposable emulator if a run times out, and do
not continue using that test process. No delays simulate the 30/60-second rules;
fixtures seed past timestamps instead. Unrelated cases remain serialized.

Lifecycle coverage uses real matched pairs creating immediate Active anonymous
rooms, released reservation sessions, immediate rematching into multiple rooms,
Ready retries and Ready-versus-expiration transactions, unmatch with read-only
message history, and report isolation. Historical cleanup uses explicitly seeded
legacy rooms, evidence snapshots, multiple batches (401 messages), and interrupted
cleanup retry fixtures. Nested messages, historical decisions, reports, and
evidence are deleted only for each test's owned rooms before the existing matching
fixture removes its users/sessions/rooms/history. No global emulator reset is used.

Continuation decisions and automatic chat timers are removed. Active rooms have
no mandatory expiration, and stale deadline fields cannot enable temporary cleanup.
The cleanup service leaves CleanupPending set; clearing it belongs to the untested
worker. Empty User reports may have EvidenceCaptured=false; historical temporary
cleanup permits these only when no messages remain. Reservation deadline races use past/future
fixtures, not real-time boundary waits. Race waits retain the 90-second SDK
cancellation limitation documented above.

Auth setup now adds 38 offline configuration/transport safety cases and one
serialized disposable-account smoke test. The existing 213 cases are preserved.
Registration, verification, resend, and service/action-level eligibility coverage
is described below. JWT acceptance, real SMTP, SignalR, and hosted worker
orchestration are not exercised.
The emulator does not prove production index requirements or contention behavior.

References:
- https://docs.cloud.google.com/dotnet/docs/reference/help/emulators
- https://firebase.google.com/docs/emulator-suite/connect_firestore

## Auth emulator isolation

- `FIREBASE_AUTH_EMULATOR_HOST` is mandatory and must be an explicit loopback IP
  with a valid port, without `http://`. The configured endpoint is 127.0.0.1:9099.
- Both emulator hosts and the exact demo project are validated before Admin app
  initialization; inherited `GOOGLE_APPLICATION_CREDENTIALS` paths are rejected.
- `AuthEmulatorFixture` creates a unique **named** FirebaseApp with an explicit
  demo project and synthetic non-refreshable placeholder credential. It never
  loads ADC or a service-account file. Firebase Admin's Auth emulator mode supplies
  its local emulator authorization. The placeholder is not a real credential.
- A test-only HTTP handler revalidates environment before every SDK request and
  permits only HTTP requests to the selected loopback host and
  `/identitytoolkit.googleapis.com/v1/projects/demo-ezone-regression/` path.
  Proxy use and automatic transport redirects are disabled; a redirected request
  must also pass the guard. A production/wrong-project SDK fallback is rejected
  before transport. Safety tests use an injected fake terminal handler, no sockets.
- No test mutates emulator environment variables. The wrapper restores its
  process-scoped environment in finally, including the Auth host variable.
- The Auth collection disables parallelization. It refuses to reuse an existing
  default FirebaseApp and never creates, replaces, or deletes DefaultInstance.
  Named-app deletion is verified during teardown. The separate workflow fixture
  described below owns a default app for services that require DefaultInstance.
- The smoke test creates a unique UID and `example.invalid` email without sending
  mail or testing sign-in. It creates/reads/deletes through Admin SDK, confirms
  UserNotFound after deletion, checks recorded request destinations, and retries
  deletion in finally with its own timeout. Only its own account is deleted.
- Auth emulator tokens are unsigned. This setup is not a test of production JWT
  signature validation. Never set emulator environment variables in deployment.

Run Auth safety cases alone without starting either emulator:

```powershell
dotnet test Backend/WebChatEIU.IntegrationTests/WebChatEIU.IntegrationTests.csproj --no-build --no-restore --filter Category=AuthSafety
```

`npm start` starts both configured emulators. `Run-Smoke.ps1` executes the full
integration suite and automatically stops both. Auth uses the existing pinned CLI;
no package upgrades, global CLI installation, or Firebase login are required.

Reference: https://firebase.google.com/docs/emulator-suite/connect_auth

## Registration, verification, and session workflows

`AuthWorkflows` uses the real Auth/User services and guarded emulator clients.
It adds 38 workflow cases and 10 offline link-safety cases. After the persistent-chat
refactor and Auth eligibility fix the complete run contains 230 integration-project
cases and 111 offline unit cases.
Its nonparallel collection refuses any existing default FirebaseApp, creates an
owned default app with the explicit demo project and synthetic non-refreshable
credential, verifies ownership before use, and deletes that exact app at teardown.
It cannot overlap the existing nonparallel named-app Auth collection.

The email service is an in-memory recorder with optional simulated delivery
failure; SMTP is never instantiated. Captured verification links are validated
before a single HTTP GET: only the configured loopback Auth host, `/emulator/action`,
verifyEmail mode, fake emulator API key, and a nonempty code are accepted.
Redirects, proxies, continuation URLs, and tenant parameters are disallowed.
Offline negative-link tests exercise this boundary without opening sockets.

Registration rollback uses a test-only HTTP response observer: after the real
emulator creates an account, the fixture seeds a conflicting profile before the
service attempts profile creation. The real Firestore AlreadyExists error then
exercises account rollback. SDK responses and transactions are not mocked.
Every created UID is tracked even when registration throws; cleanup removes only
owned Auth accounts, user profiles, and queue entries. No global reset is used.

Workflow tests characterize duplicate/invalid registration, profile preservation,
delivery failure and recovery, emulator email verification, repeated activation,
resend behavior, and eligibility for missing, disabled, unverified, inactive,
banned, and deleted accounts. Controller actions receive an explicitly constructed
principal; these assertions do not test JWT validation, authorization middleware,
HTTP status translation for unhandled errors, or middleware rate limits.

Current behavior is preserved: school-email validation belongs to DTO validation,
not the registration service; resend delivery exceptions propagate from the action.
Phase 2D.1 fixes the former Random Match eligibility inconsistency. Queue Join,
reservation creation, and Ready now check enabled/verified Auth as well as the
transactional profile rules. The former characterization asserts rejection.
Matching/lifecycle fixtures provision disposable verified Auth accounts through
guarded named Admin apps and remove only their own accounts during teardown.
New cases cover Auth changes after queue entry, both participant positions,
restored eligibility, and fail-closed Auth lookup errors. A controlled 403 lookup
response is injected after destination validation; no guard is bypassed. See
`../PHASE_2D_1_MATCH_ELIGIBILITY.md` for the unavoidable cross-service race and
deferred HTTP/JWT coverage. Historical phase reports describe their original baseline.

Run-Smoke.ps1 includes these tests automatically. No additional downloads or
dependency changes are required beyond the existing emulator setup.
