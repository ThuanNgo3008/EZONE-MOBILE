# Persistent anonymous chat lifecycle review

Base commit: f7a73be7afbeeab0f91c402392e511f42481d606.
Implementation branch: codex/persistent-active-chat. No deployment or data migration
is part of this change.

## New lifecycle

Random Match -> PendingReady reservation (30 seconds) -> mutual Ready -> Active
persistent anonymous chat. Room creation, match history, reservation completion,
owned queue removal, and deletion of both verified reservation-owned sessions
remain in one Firestore transaction. Concurrent Ready retries return the same room.
Existing rooms do not lock users out of matching; only unexpired PendingReady
sessions block conflicting reservations. Cancellation and expiration retain their
reservation-ID/status guards. Old Ready retries return before touching newer locks.

New rooms set the existing BecamePersistentAt field at creation and leave all
temporary/decision deadlines null. IsRevealed remains false. No field or collection
is renamed. Future affinity/reveal fields are retained unchanged.

The continuation action and its service/DI registration are removed. Temporary
room creation and both timer transition methods are removed. The worker retains
reservation expiry, stale waiting cleanup, and historical closed-room cleanup;
it no longer scans or transitions Temporary/AwaitingDecision rooms.

## Compatibility and retained legacy references

- POST /api/firebase-chat-rooms/{roomId}/decision no longer exists.
- Development test-create creates only persistent Active rooms; IsTemporary is no
  longer a request option. Ready still returns Matched and the room ID, but the
  room is Active immediately and matchSessions no longer persist for chat rooms.
- Existing temporal fields, FirebaseRoomDecision model, SubmitRoomDecisionDto,
  and its offline validation tests remain for historical compatibility. New code
  does not write roomDecisions. Historical audit/checkpoint documents remain intact.
- SignalR JoinRoom and message persistence retain historical state checks for
  existing documents. Active room access has no deadline. Authentication,
  moderation, messages, evidence capture, and authorization are unchanged.
- Historical end-chat, reporting of historical rooms, room-owned historical
  session release, and closed-temporary cleanup remain supported. Cleanup requires
  Closed + CleanupPending + TemporaryStartedAt + no BecamePersistentAt, and refuses
  deletion when report evidence is unconfirmed. New persistent rooms never qualify,
  including after Unmatch or Report. Existing client code must stop calling
  continuation decisions and stop treating an Active room as a matchmaking lock.
- The previously documented Auth/Firestore eligibility inconsistency remains
  unresolved: profile-only queue checks can accept an active profile whose Auth
  account is unverified or disabled. This change does not alter authentication.

## Separate historical migration proposal (not executed)

1. Before deploying broadly, inventory Temporary/AwaitingDecision rooms, related
   roomDecisions, reports/evidence, history, and legacy sessions in a read-only
   review. Take an approved backup/export and agree whether each open legacy room
   should become persistent or be explicitly closed. No implicit policy is assumed.
2. Prepare a separately approved dry-run migration with an explicit project guard,
   document allowlist, expected versions, and per-room output. Exclude Closed and
   already Active rooms. Deploying this code alone does not convert old rooms;
   historical deadline checks can leave them non-writable until addressed.
3. For approved conversions, use a transaction to re-read the selected room and
   expected state, set Active and BecamePersistentAt, disable CleanupPending, and
   clear obsolete deadlines if approved. Preserve messages, evidence, participant
   IDs, anonymity, existing history, and historical decision documents. Only release
   sessions still owned by that historical room/reservation; preserve newer locks.
4. Reconcile and verify history and room/session ownership, retained evidence and
   message counts. Rollback must be conditional on unchanged state, never blindly
   overwrite newer reservations or newly closed/reported rooms. Review emulator
   dry-run results before authorizing any real migration.

No migration script, production database access, frontend changes, package upgrades,
affinity computation, identity reveal, media, or other new features are included.

## Regression verification

Updated lifecycle tests replace timer/YES/NO assumptions with immediate persistence,
absent deadlines, independent shared-user rooms, rematching, and explicit closure.
Existing matching races retain real transactions and bounded shared-start gates.
Additional coverage checks Ready/expiration races, late Ready retries preserving
new sessions, historical session compatibility, cancellation ownership, and reports
preserving other active rooms/new reservations. Legacy cleanup/evidence tests seed
historical documents explicitly and retain multi-batch/retry protections.

Tests use only guarded demo-ezone-regression Auth/Firestore emulators, never start
Program.cs or the hosted worker, and capture mail in memory. HTTP/JWT middleware
and SignalR network transport are not claimed as end-to-end tested.

Final verification: backend, offline test project, and integration test project
all build successfully with zero warnings and zero errors. Offline: 111 passed,
0 failed, 0 skipped. Integration project: 196 passed, 0 failed, 0 skipped (47
matching, 39 lifecycle/reporting/cleanup, 110 other existing safety/Auth cases).
Total: 307 passed. No concurrency failures were observed in either full run;
bounded repeated trials do not prove absence of all possible races.

Both emulators shut down automatically. A system TCP listener query confirmed
ports 8085, 9099, 4405, 4505, and 9150 released. Emulator guards/configuration,
dependency versions, Auth workflows, production credentials/local configuration,
uploaded assets, and frontend files are unchanged. No API or worker was started.
Git diff whitespace checks pass. Changes are unstaged and uncommitted for review.

## Changed-file manifest

Application code (modified unless marked deleted):

- WebChatEIU/Controllers/FirebaseChatRoomsController.cs
- WebChatEIU/Models/Firestore/FirebaseChatRoom.cs (comments only)
- WebChatEIU/Models/Firestore/FirebaseMatchSession.cs (comments only)
- WebChatEIU/Program.cs (remove decision service registration only)
- WebChatEIU/Services/FirebaseChatRoomService.cs
- WebChatEIU/Services/FirebaseMatchReservationService.cs
- WebChatEIU/Services/FirebaseRandomMatchQueueService.cs
- WebChatEIU/Services/FirebaseRoomDecisionService.cs (deleted)
- WebChatEIU/Services/FirebaseRoomExpirationWorker.cs

Regression tests:

- WebChatEIU.Tests/Contracts/FirebaseEndpointContractTests.cs
- WebChatEIU.IntegrationTests/Matching/MatchTestData.cs
- WebChatEIU.IntegrationTests/Matching/MatchReservationTests.cs
- WebChatEIU.IntegrationTests/Lifecycle/LifecycleTestData.cs
- WebChatEIU.IntegrationTests/Lifecycle/ChatRoomLifecycleTests.cs
- WebChatEIU.IntegrationTests/Lifecycle/RoomDecisionConcurrencyTests.cs (replaced)
- WebChatEIU.IntegrationTests/Lifecycle/ChatRoomConcurrencyTests.cs (new replacement)
- WebChatEIU.IntegrationTests/Lifecycle/ReportingTests.cs
- WebChatEIU.IntegrationTests/Lifecycle/TemporaryMessageCleanupTests.cs

Documentation:

- README.md
- FirestoreEmulator/README.md
- CHAT_LIFECYCLE_REFACTOR.md (this report, new)

## Final code review against the approved baseline

Reviewed all 21 paths above, including the two untracked additions and the deleted
service/tests, against f7a73be7afbeeab0f91c402392e511f42481d606. No defect introduced
by the refactor was identified. No application or test changes were needed during
this final review; only this review record was extended.

| Area | Review finding and evidence |
| --- | --- |
| Mutual Ready | Room reference is allocated once per call; the reservation is transactionally read and completed with the room/history writes. Concurrent retries observe Matched and return the existing room. Matching concurrency cases assert one room/history. |
| Session ownership | Both unexpired PendingReady session IDs/statuses must belong to the reservation before matching. Deletion happens in the same transaction. Old Ready retries return before reading/deleting replacement sessions. |
| Multiple rooms | Matching checks reservation locks, not active rooms. Shared-user AB/AC tests exercise FindMatch immediately, independent messages, pair-history rejection, and newer pending reservations surviving Unmatch/Report. |
| Cancel and expiry | All session removals retain reservation-ID and PendingReady checks. Queue mutations retain reservation ownership checks. Ready/Cancel and Ready/expiry trials use real transactions, shared start gates, and a 90-second wait bound. |
| Repeat-match prevention | Canonical pair IDs and history reads/creates are unchanged; history persists across explicit closure. Both direct and queue reservation paths reject prior pairs. |
| Timers | No temporary-room factory, continuation service/action, or automatic room-transition method remains. The worker has only reservation expiration, stale waiting cleanup, and historical closed-room cleanup. |
| Message protection | New rooms have BecamePersistentAt and no temporary deadline. Active status rejects cleanup; persistent markers reject cleanup after closure, including a mistakenly set pending flag. Tests cover stale deadline fields and retained messages. |
| Unmatch and reports | Room membership and transaction checks remain. Reporting evidence snapshots and historical multi-batch cleanup/retry assertions remain intact. Shared-user tests verify room and replacement-session isolation. |
| REST and SignalR | Controller/hub FirebaseUser metadata and Program's Firebase JWT scheme, policy, service registrations, and /firebaseChatHub mapping remain. Both send paths use the unchanged membership-checking transactional persistence service; Active access does not inspect legacy deadlines. |
| Scope and schema | Model changes are comments only; future fields and serialized names remain. No reveal, affinity calculation, media, calls, location, or editing/recall behavior was introduced. |
| Historical compatibility | Legacy fields, model/DTO, explicit end-chat/report support, and historical cleanup remain. Old open rooms are not migrated or automatically closed; expired historical rooms may be non-writable pending a separate approved migration. |

Test review: the obsolete decision route metadata case is replaced with a route
absence assertion. Old YES/NO and decision-timer tests are replaced with immediate
Active/persistence, reservation-expiry, retry, and explicit-closure assertions.
The original simultaneous Ready and Ready/Cancel cases remain intact apart from
their shared postcondition helper now requiring Active and absent sessions.
Legacy report eligibility, evidence content, foreign-message rejection, multi-batch
cleanup, and retry protections remain asserted using explicit historical fixtures.
No tests were skipped, muted, or changed to ignore failures. Unrelated Auth,
moderation, DTO, safety, queue presence, and pagination tests remain unchanged.

Final-review rerun: all three projects build with 0 warnings/errors; offline tests
111 passed/0 failed/0 skipped; integration tests 196 passed/0 failed/0 skipped.
Both demo emulators reported automatic shutdown. HTTP/JWT middleware, SignalR
network transport, hosted-worker orchestration, production index behavior, and
exhaustive concurrency correctness are not claimed as tested.

## Separate pre-existing issue: Auth and queue eligibility disagree

Status: unresolved, outside this lifecycle refactor. FirebaseRandomMatchQueueService
checks Firestore profile eligibility only. With an active/non-banned/non-deleted
profile but unverified or disabled Firebase Auth, JoinQueue accepts Waiting while
FirebaseSessionController rejects access and verification synchronization returns
false. Existing AuthWorkflows characterization cases reproduce both conditions.
The same behavior existed in the approved baseline; this review does not establish
an HTTP/JWT authentication bypass. No production fix or policy change was made.
Resolving the eligibility contract requires a separately approved authentication
change and suitable HTTP-level security tests.
