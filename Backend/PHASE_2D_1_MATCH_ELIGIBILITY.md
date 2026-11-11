# Phase 2D.1 — Random Match Auth eligibility

Approved baseline: 8a375f60a51a9105bca2ef5ca0ad6e858ce2c2e6.
Branch: codex/fix-match-eligibility. The initial working tree was clean.
No commit, push, deployment, or historical-room migration is part of this change.

## Root cause and corrected rules

Queue Join, candidate reservation, direct reservation creation, and Ready previously
checked Firestore profiles only. Session also read Firebase Authentication and
rejected missing, disabled, or unverified accounts. An Active Firestore profile
could therefore allow an ineligible Auth account into matching.

FirebaseMatchEligibilityService now centralizes the predicates used by matching
and Session: Auth account exists, EmailVerified is true, Disabled is false; profile
exists, IsActive is true, IsBanned and IsDeleted are false. Verification synchronization
reuses the Auth predicate but retains its existing activation behavior for inactive
profiles. No Auth claim or profile flag substitutes for the other source of truth.

| Operation | Before | After |
| --- | --- | --- |
| Join/rejoin | Eligible profile | Eligible Auth plus transactional profile checks |
| Reserve queued candidate | Both profiles, queue presence, history and session guards | All existing checks plus both current Auth accounts |
| Direct reservation | Both profiles, history and session guards | All existing checks plus both Auth accounts |
| Ready | Profile/session/reservation checks | Both accounts rechecked before any new Ready/match write, plus all existing transactional checks |
| Existing Matched retry | Returns existing room | Still returns existing room; does not create a new match or require reauthorization of an existing chat |
| Cancellation/expiry | Reservation-owned cleanup/requeue | Unchanged; Auth ineligibility/outages do not prevent these cleanup paths |

Missing Auth returns ineligible. Disabled/unverified Auth rejects Join, direct
reservation, and Ready; candidate reservation returns null so a valid seeker can
continue looking. Other Auth lookup errors fail closed with InvalidOperationException
and no matching transaction. Find/Ready/direct-creation actions retain their
existing conflict handling for these service errors. The heartbeat action now
also returns a conflict on an eligibility lookup failure. Its existing presence
refresh can precede that failure; reservation status and ownership are unchanged.
The action result is tested directly; no HTTP/security middleware is introduced.
Unexpected exceptions also propagate rather than granting eligibility.

Heartbeat-only presence refresh is unchanged. An ineligible historical Waiting
entry may remain Waiting until cancellation or existing stale cleanup, but cannot
be reserved by either candidate path or create a room through Ready. This change
does not purge queues, ban accounts, or close existing chats.

## Transaction boundaries and unavoidable Auth race

Every Firebase Admin lookup runs before RunTransactionAsync, never within its
callback or retries. Queue/reservation transactions still read and validate the
current profiles, queue entries, history and reservation-owned locks before writes.
Ready pre-reads the reservation to identify the participants and checks both Auth
accounts. Its transaction re-reads the reservation and rejects changed participant
IDs or an unvalidated pending reservation before allowing Ready/match writes.
Already-expired reservations can still follow their existing expiration/requeue
path without requiring Auth. The transaction continues checking the 30-second
deadline on each retry.

Auth and Firestore cannot participate in one atomic transaction. An account may be
disabled, deleted, or lose verification after its Auth lookup and before a Firestore
commit (including retries). Rechecking at Join, reservation and every new Ready
limits this window but cannot eliminate it. A candidate scan checks the requester
once and each visited candidate once. The requester check can therefore precede
the commit by the time spent scanning. It is never reused across requests or at
Ready, and transaction retries do not issue extra Auth lookups.
Eliminating this race would require a separately designed cross-system eligibility
protocol, not an Auth call inside a Firestore transaction.

Firestore profile changes remain transactionally guarded. Room/history creation,
reservation completion, queue removal and owned session release remain atomic.
Persistent Active anonymous rooms, multiple rooms, immediate rematching, repeat-pair
prevention, reporting/evidence, moderation, messaging, and Unmatch remain unchanged.
No collection, document shape, serialized field, or future-feature field changed.

## Test strategy and safety

Existing matching/lifecycle fixtures now create enabled, verified, disposable Auth
accounts alongside their isolated profiles. Each MatchTestData owns a guarded named
FirebaseApp and deletes only its own accounts/documents. No DefaultInstance is
created or borrowed by these fixtures; collections remain serialized. Existing
Auth workflow tests retain their separately owned default app.

Auth eligibility tests cover both participant positions at reservation and Ready,
missing/disabled/unverified accounts, ineligible profiles, restoration of eligibility,
and lookup failure. A test-only transport injects a 403 Auth lookup error *after*
the existing destination/environment guard. It uses the real Admin SDK error
handling, cannot route outside the emulator, and does not simulate successful Auth.
Tests assert no room/history/session creation or unauthorized mutation on failure.
The former eligibility-gap characterization now asserts rejection, retaining its
Session and verification-sync assertions. Existing multi-room/concurrency tests
run against real disposable Auth accounts rather than bypassing the new checks.

Only demo-ezone-regression and loopback Auth 127.0.0.1:9099 / Firestore 127.0.0.1:8085
are used. Firestore EmulatorOnly and all project/credential/transport guards remain.
No Program.cs, hosted worker, production Firebase, ADC, SMTP, or frontend is run.
HTTP/JWT middleware, token revocation and SignalR transport are not exercised;
service/action-level tests do not prove production authentication security.

## Changed files

Application:

- WebChatEIU/Services/FirebaseMatchEligibilityService.cs (new)
- WebChatEIU/Services/FirebaseRandomMatchQueueService.cs
- WebChatEIU/Services/FirebaseMatchReservationService.cs
- WebChatEIU/Services/FirebaseAuthService.cs
- WebChatEIU/Controllers/FirebaseSessionController.cs
- WebChatEIU/Controllers/FirebaseRandomMatchController.cs (heartbeat error handling)
- WebChatEIU/Program.cs

Tests/infrastructure:

- WebChatEIU.IntegrationTests/Matching/AuthEligibilityTests.cs (new)
- WebChatEIU.IntegrationTests/Matching/MatchTestData.cs
- WebChatEIU.IntegrationTests/Auth/AuthEmulatorFixture.cs
- WebChatEIU.IntegrationTests/Auth/AuthEmulatorHttpClientFactory.cs
- WebChatEIU.IntegrationTests/AuthWorkflows/AuthWorkflowTestData.cs
- WebChatEIU.IntegrationTests/AuthWorkflows/RegistrationTests.cs
- WebChatEIU.IntegrationTests/AuthWorkflows/SessionEligibilityTests.cs

Documentation:

- README.md
- FirestoreEmulator/README.md
- PHASE_2D_1_MATCH_ELIGIBILITY.md (this report)

## Final review findings and fixes

Reviewed the 16 submitted paths against the approved baseline. Two issues introduced
by this phase were fixed: a candidate scan no longer repeats the requester lookup
for every candidate, and the heartbeat action handles a failed eligibility lookup
the same way as Find. Three added regression cases count actual guarded Admin
requests for both search entry points and exercise the heartbeat conflict result
without a reservation or lock being created. Existing assertions remain intact;
stale-Waiting cases now also assert that an ineligible requester cannot rejoin.

## Verification and review results

All three projects build successfully with zero warnings and zero errors:
WebChatEIU, WebChatEIU.Tests, and WebChatEIU.IntegrationTests.

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Offline unit/contract tests | 111 | 0 | 0 |
| Emulator integration project | 230 | 0 | 0 |
| Total | 341 | 0 | 0 |

The 34 new integration cases supplement all 307 previous cases; the two former
eligibility-gap cases now assert the corrected rejection rule. Matching has 81
cases; all 39 lifecycle/reporting/cleanup cases and the existing 110 other
integration cases pass. No concurrency failures were observed in this run;
bounded trials do not establish exhaustive concurrency correctness.

Review confirmed that no Firebase Admin lookup is made inside a Firestore
transaction callback. Ready's new preflight checks do not remove any ownership,
deadline, profile, history, or atomic room/session-release assertions. Session's
status codes and verification synchronization's activation rules remain unchanged.
Failure cases assert unchanged queue/reservation/session state and no room/history.
Multi-room, immediate-rematch, cancellation, expiration, reporting, and persistent
message protections pass with real Auth eligibility checks enabled.

No frontend, production configuration/credentials, package versions, model/schema,
historical documents, or unrelated features were changed. No production API or
hosted worker was started. Both emulators reported automatic shutdown after the
integration run. The Git whitespace check passes; all 17 paths remain unstaged and
uncommitted on codex/fix-match-eligibility for review.
An independent system TCP listener query confirmed ports 8085, 9099, 4405, 4505,
and 9150 are released. The diff was checked for credentials, sensitive configuration,
production access changes, and unrelated paths; none were found. Ready for commit
review, with no commit or push performed.

Remaining risks: Auth/Firestore timing race described above; additional Auth request
latency and dependence on Auth availability; HTTP/JWT middleware, error translation,
revocation handling, and SignalR network transport still need separate host tests.
No Firebase production or emulator result proves production contention/index behavior.
