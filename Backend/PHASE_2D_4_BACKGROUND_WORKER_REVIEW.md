# Phase 2D.4 Background Worker integration tests

Branch `codex/background-worker-tests` starts at
`c14b775ae013308e0b1b0baf4d18de822671bedc`.

## Worker responsibilities

The production host registers `FirebaseRoomExpirationWorker` once per
process. It runs a cycle immediately, then waits ten seconds after each
cycle. Each stage queries up to 100 documents and resolves its cleanup
service in a separate dependency injection scope.

1. Expire overdue PendingReady reservations. The transaction rechecks
   status, deadline and ownership before releasing sessions or changing
   queue entries. A Ready participant can return to Waiting.
2. Remove Waiting entries whose last heartbeat is stale. The transaction
   rechecks both status and presence before deletion.
3. Remove messages from eligible Closed historical temporary rooms.
   Cleanup checks the temporary and persistent markers and report evidence
   before deleting messages. A successful run clears the room's pending flag.

The worker has no phase that transitions or closes open Temporary or
AwaitingDecision rooms. It does not enforce the former five-minute or
two-minute rules on Active rooms. Historical open rooms are left as stored.
Explicit closure can still mark eligible legacy rooms for cleanup.

## Hosted coverage and isolation

The tests start the real worker through a Generic Host with real scoped
cleanup services. The production startup method and SignalR sweep are
outside this host. Test data is owned and removed by the existing guarded
matching and lifecycle fixtures. A test-owned gRPC channel uses only the
validated loopback Firestore endpoint and demo project, with proxies and
redirects disabled. The worker host disposes the channel after shutdown.
The SDK uses an explicit invoker for this channel. Existing setup and
assertion clients still use `EmulatorDetection.EmulatorOnly`.

A gRPC hook pauses the worker after selected emulator scan results so
tests can put operations between the scan and transactional recheck. The
tests use bounded waits and actual emulator reads and writes. Reservation
deadlines are checked for the 30-second interval and moved into the past
with controlled fixture data. These tests cover ordered interleavings.
They do not force overlapping commits on the same document.

- Shutdown cancels a paused reservation scan without applying its results.
  The worker execution task completes and opened stage scopes are disposed.
  The test compares Firestore update timestamps for the reservation and both
  participants' session and queue documents before startup and after shutdown.
- A transient unavailable response during the Waiting scan leaves prior
  reservation work committed. The next cycle clears the stale entry and
  reaches message cleanup.
- Ready and the worker each handle an expired reservation first in separate
  cases. Neither ordering creates a room or revives the reservation.
  Older expired records cannot release newer session and queue ownership.
- A stale Waiting scan cannot remove a refreshed entry, recreate a canceled
  entry, or delete a user reserved after the scan. A genuinely stale entry
  is removed and a recently seen user remains Waiting.
- Mutual Ready creates persistent Active rooms before timeout. Immediate
  rematching during the worker's scan creates a second room. Two worker
  cycles leave both rooms and their messages intact even with old timer
  fields and a stale cleanup flag. Explicit Unmatch closes only its room.
- Unconfirmed report evidence blocks one room without blocking an eligible
  room. After confirmation the next cycle retries cleanup and retains both
  evidence sets. A room already cleaned keeps its existing update timestamp.

## Remaining limitations

- The worker passes cancellation to scans and its delay. The per-item
  expiration, stale removal and message cleanup calls, and the final
  cleanup flag update, do not receive the stopping token. Shutdown during
  those operations depends on the outstanding Firestore calls completing
  or on the host's shutdown deadline. This phase tests cancellation during
  a scan and stopping between cycles. It leaves this production limitation
  for separate review.
- A scan-level error skips the stages still pending in that cycle. Later
  cycles retry the sequence. An individual room failure leaves its pending
  flag set and allows the other rooms in the same scan to proceed. A
  permanently blocked item can occupy one of the 100 selected slots.
- The transport failure was injected on the worker channel after a real
  scan. The emulator process was kept running for independent state checks.
  A production outage and shutdown during a multi-batch delete were outside
  this verification.
- Message batches and the final pending-flag update are separate writes.
  Cleanup checks eligibility and report evidence before deleting batches.
  Closed rooms have no supported transition back to Active. Manual room
  or report edits during cleanup fall outside that guarantee.
- Successful emulator queries do not verify that the corresponding
  production composite indexes are deployed.
- Single-instance deployment remains required under the SignalR security
  review. The cleanup worker also has no cross-instance scheduler or lease.

## Historical data

No migration ran. Before retiring compatibility code, inventory old
Temporary, AwaitingDecision and Closed pending-cleanup records, and check
report evidence and any lingering reservation ownership. Decide explicitly
which open rooms should close or be retained. Any approved migration should
use guarded, idempotent updates, preserve persistent history and confirmed
evidence, and verify the resulting room and cleanup states on a copy first.

## Verification

The backend and both test projects built with zero warnings and errors.
The complete run passed 111 offline and 303 guarded integration tests,
including eight hosted-worker cases. Total results were 414 passed,
zero failed and zero skipped.

Two focused runs of the final worker harness passed all eight cases each.
An initial harness run was rejected by the emulator rules. The custom
loopback channel needed the emulator's fixed Admin marker. The final
fixture supplies it only on the guarded emulator channel, and the
emulator rules were preserved.

The unchanged competing-reservation test passed all nine trials across
three focused runs in Phase 2D.4. The prior review recorded eight passes
and one Firestore lock timeout in the same focused test, which constructs
the matching services directly without either hosted worker. The timeout
remains unresolved. Emulator transaction contention is consistent with
the prior symptom, but the exact scheduling or fixture contribution is
undetermined. No matching assertions or timeouts were changed.
[Firestore Emulator transaction limitations](https://firebase.google.com/docs/emulator-suite/connect_firestore#transactions)

All test hosts stopped their worker and disposed their owned channel.
Both emulators shut down automatically. The final check found zero
listeners on the five emulator ports and zero EZone test-host processes.

## Final review

The final review strengthened the cancellation assertion to detect writes
that preserve the visible status. All three projects rebuilt with zero
warnings and errors. The complete guarded rerun passed 414 tests, with
zero failures and zero skips. The focused Worker rerun passed 8 of 8,
and the focused Random Match rerun passed 3 of 3. The prior intermittent
timeout remains unresolved. After the last run, no emulator listeners or
EZone test hosts remained. The documented production limits do not block
the test-only commit.

## Changed paths

- `Backend/WebChatEIU.IntegrationTests/Workers/HostedCleanupWorker.cs`
- `Backend/WebChatEIU.IntegrationTests/Workers/BackgroundWorkerTests.cs`
- `Backend/PHASE_2D_4_BACKGROUND_WORKER_REVIEW.md`
