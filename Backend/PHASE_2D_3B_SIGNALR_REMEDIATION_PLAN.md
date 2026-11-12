# Phase 2D.3B proposed SignalR security remediation

This is a follow-up plan. Phase 2D.3 reproduces the current behavior in
loopback WebSocket tests. The production fixes require a separate change.

## 1. Bind connection lifetime to authentication

Enable `CloseOnAuthenticationExpiration` on the existing
`/firebaseChatHub` mapping using ASP.NET Core 8's dispatcher options.
Track the validated token's expiration for protected hub invocations and
outbound delivery as well, so the dispatcher shutdown interval does not
extend application access. Keep the existing Firebase scheme and policy.
On reconnect, require a fresh valid token and explicit room rejoin.
[ASP.NET Core 8 SignalR configuration](https://learn.microsoft.com/en-us/aspnet/core/signalr/configuration?view=aspnetcore-8.0#advanced-http-configuration-options)

## 2. Revalidate account and session state

Reuse Phase 2D.2B's Admin account lookup and revocation comparison for the
connection's validated UID and original `auth_time`. Preserve per-connection
expiration and session age. A UID-level disabled or deleted result invalidates
all of that account's connections. A revocation cutoff invalidates only
connections whose authentication time precedes it. A new session on another
device may still be eligible.

Gate protected invocations and recipient selection on current account
eligibility. Use a single account lookup per UID per operation where several
recipient connections share that UID, and evaluate their individual session
claims against the result. Add a bounded idle sweep to remove invalid
connections that never invoke a method. A missing, disabled or revoked result
blocks traffic and removes subscriptions before disconnecting the connection.
An unavailable or timed-out Auth lookup suppresses sends and delivery for
the unverified connection and triggers the same cleanup. Emit generic
failures to clients and record sanitized failure categories internally.
Bound lookup concurrency and budget for Auth latency, quotas and retries.
Any future decision to reuse successful account checks across operations
must specify and accept the resulting stale-access window.

## 3. Authorize delivery and evict room subscriptions

Track subscriptions by connection ID, Firebase UID and room ID. Add and
remove each connection-room entry when it joins, leaves or disconnects.
After Unmatch, Report or membership removal, evict all affected subscriptions
for that room and preserve the user's subscriptions to other Active rooms.
Keep the existing Firestore transaction checks when persisting messages.
Authorize recipients against current room state and account eligibility
before enqueueing each broadcast, since an old SignalR group alone can
still contain a former member. If the current room or recipient state
cannot be established, suppress delivery. [SignalR group authorization](https://learn.microsoft.com/en-us/aspnet/core/signalr/groups?view=aspnetcore-8.0)

Sequence joins, invalidations and fan-out so an in-flight join or lookup
cannot restore an evicted subscription. Use generation checks or equivalent
ordering for each affected connection and room. Recheck the generation after
asynchronous work and before enqueueing data. Treat a disconnect as cleanup
for that connection ID only. A reconnect gets a new ID and authorizes its
subscriptions afresh. Verify deployment topology before relying on an
in-memory registry. If multiple server instances are supported, invalidation
and delivery decisions must reach each instance that owns a connection.
Firestore commits and WebSocket sends are separate operations. Frames
already queued cannot be withdrawn, and external account or membership
changes can race with the last lookup. Document that boundary explicitly.

## 4. Keep disconnect and room closure distinct

The current hub has no server-side `PartnerDisconnected` producer. If a
future client needs it, define it as room-scoped presence after the last
eligible subscribed connection for that participant leaves, with a reconnect
grace period if desired. Treat explicit room closure as its own event.
The existing mobile handler treats PartnerDisconnected as conversation
closure, so emitting a temporary presence event requires a separately
coordinated client contract change. Account invalidation, reconnect and
transport loss leave the persistent room and its history available to
eligible participants.

## Acceptance tests

- Flip the existing established-WebSocket characterizations to require no
  protected sends, Firestore writes or passive delivery after enforcement
  observes disablement, deletion, revocation, expiry or lookup failure.
- Test two connections for one UID in the same room and across rooms. Verify
  account disablement cuts both off, while revocation distinguishes old and
  newly authenticated sessions.
- Remove a room participant with two joined connections. Verify neither
  receives that room's broadcasts and the still-authorized room remains
  usable. Cover explicit Unmatch and Report eviction as separate paths.
- Test expiry-driven shutdown, a rejected reconnect with an expired or
  revoked token, and successful reconnect and rejoin with a valid token.
- Coordinate races with explicit barriers around join, lookup, close and
  fan-out. Cover duplicate joins, repeated eviction, disconnect during
  lookup, and reconnect overlapping old-connection cleanup. Verify no
  send or subscription survives an invalidation already applied by the server.
- Rerun existing HTTP/JWT, Auth, Firestore, matching and multi-room suites
  against the guarded demo emulators.
