# Phase 2D.3B SignalR security remediation

Baseline `78693590b5c1b79168aab75acef10e21c8251dcb`, branch
`codex/signalr-security-remediation`.

## Connection and delivery enforcement

- The Firebase JWT handler still checks signature, issuer, audience and
  lifetime before the shared Firebase account lookup runs. The hub stores
  the validated UID, original UTC `auth_time`, and JWT `exp` per connection.
  Hub negotiation and upgrade reject the token at `exp`, including within
  the normal bearer clock-skew window. The dispatcher closes idle expired
  sockets through `CloseOnAuthenticationExpiration` on this hub endpoint.
  Invocation and fan-out checks independently exclude expired sockets.
- Join, send and leave revalidate the current Firebase Auth account and
  Firestore profile. A fresh lookup is also required before fan-out for each
  distinct UID being considered, including the sender. One result applies
  to that UID's observed local sockets. Disablement, deletion, ineligible
  profiles and unavailable checks remove their subscriptions and abort
  those sockets. A revocation cutoff is compared against each socket's own
  `auth_time`. A later authenticated session can continue. Ping returns a
  constant and grants no chat access.
- Final review found that a caller could disconnect during the Firestore
  profile lookup and skip application of a revocation already returned by
  Firebase Auth. Auth denials now remove subscriptions before the profile
  lookup. Only the path that removes a socket from the registry aborts it,
  so disconnect and overlapping revalidation have one cleanup owner.
- A hosted sweep checks idle connections and tracked rooms on a 15-second
  timer, with at most eight concurrent checks in each phase. Account
  revalidation has an overall 10-second budget, including time waiting for a
  shared slot. Individual Firestore profile and room reads have a five-second
  budget. Sweep observation can take longer than 15 seconds if the previous
  sweep is still running or there is a backlog. Chat operations require
  fresh checks even when the sweep is delayed.
- The hub no longer uses SignalR groups as the source of room recipients.
  A process-local registry tracks unique connection and room subscriptions.
  Fan-out checks the current room document after checking accounts, removes
  every observed ineligible subscription for that room, and targets only
  the connection IDs still authorized for that room. An unavailable room
  read suppresses delivery. Unmatch, Report and direct membership changes
  are observed at the next room check, whether during join, fan-out or the
  idle sweep. Other room subscriptions are retained. A reconnect gets a
  fresh connection ID and must explicitly rejoin each room.
- Local room operations serialize join, room validation and recipient
  selection. The registry lock orders leave, disconnect and invalidation
  against selection and queuing. Firestore still transactionally checks the
  sender's membership and room state when persisting a message. Connection
  cancellation is passed to the transaction for hub sends.

## Timing and deployment limitations

An external Auth or room change can occur after its last read and before
Firestore commit or WebSocket enqueue. Firebase Auth, Firestore and
SignalR share no transaction. Once the server observes invalidation, a
stale successful lookup cannot restore removed subscriptions. A frame
already queued can still arrive, and cancellation cannot roll back a
committed Firestore write. If a write succeeds and delivery is suppressed,
the message remains available through the existing history authorization.

The registry and ordering are local to one API process. Clients connected
to another instance will miss this instance's fan-out. A SignalR backplane
alone cannot make the local recipient registry global. The per-send checks
fail closed for local recipients, but cluster-wide delivery and eviction
guarantees have not been established. Run the chat backend with one active
instance until distributed coordination is implemented and verified. Multi-instance
deployment would require routing each room to a common delivery owner or
publishing to every connection-owning node for its own fresh authorization.

Each protected operation adds a live Auth and Firestore profile read.
Fan-out adds one pair of reads per distinct UID considered and a fresh room
read. The idle sweep adds periodic reads per connected UID and subscribed
room. This adds latency, consumes Firebase capacity and incurs Firestore
read charges. A lookup outage can disconnect otherwise valid sockets and
require a new connection and room join after recovery. Eight shared lookup
slots bound simultaneous checks within this process. Heavy traffic can
still consume the budget or increase waiting time. Room serialization uses
fixed lock stripes, so unrelated rooms that share a stripe may queue behind
one another. Production throughput and quota behavior were outside the
emulator verification.

On the happy path, Join uses one Auth lookup, one profile read and one
room read. Leave uses one Auth lookup and one profile read. Sending also
uses the Firestore message transaction, then one Auth lookup and profile
read per distinct candidate UID (including the sender) plus one room read
when subscribers are present. Sockets sharing a UID share a fan-out lookup.
Memory grows with live connections and their distinct room subscriptions.
Disconnect, account invalidation and room eviction remove empty index
entries. Sweep snapshots also consume temporary memory proportional to
tracked users and rooms.

## Presence compatibility

`PartnerDisconnected` still has no server producer. A future presence
event should describe temporary, room-scoped offline state after the last
eligible subscribed socket disconnects. Explicit Unmatch or room closure
should have a separate event contract. The current React Native handler
treats `PartnerDisconnected` as closure, so a presence event requires a
coordinated client change.

## Verification environment

The guarded integration host uses real loopback Kestrel and WebSockets with
the production bearer handler and authorization policy. Firebase Auth and
Firestore calls use the `demo-ezone-regression` emulators. The Auth emulator
issues unsigned tokens. Tests use its identity claims signed with the
existing ephemeral test key. The expiration test uses production clock skew.
Production startup, credentials, room-expiration worker and React Native
were outside this verification.

The backend and both test projects built with zero warnings and errors.
The final full run passed 111 offline and 295 emulator integration tests,
including 24 SignalR transport cases. Total results were 406 passed, zero
failed and zero skipped. Coverage includes concurrent disconnect and broadcast,
room closure and reconnect, and an unaffected sibling socket.

The unchanged Random Match competing-reservation test intermittently returned
`Aborted: Transaction lock timeout` during the candidate document read.
The failure also occurred in a focused run that constructs the matching
services directly and never starts SignalR or its hosted sweep. This rules out
contention from the new sweep in that reproduction. The result is consistent
with the documented emulator locking behavior when transactions contend for
the same document. The precise contribution of emulator scheduling and fixture
timing remains undetermined. Three focused runs returned 8 passed, 1 failed
and zero skipped across nine trials. No matching assertions or timeouts were
changed.
[Firestore Emulator transaction limitations](https://firebase.google.com/docs/emulator-suite/connect_firestore#transactions)

Both emulators shut down automatically. After the final focused run, all
five emulator listener ports were released and no EZone test host remained.

## Changed paths

- `Backend/WebChatEIU/Hubs/FirebaseChatConnections.cs`
- `Backend/WebChatEIU/Hubs/FirebaseChatHub.cs`
- `Backend/WebChatEIU/Program.cs`
- `Backend/WebChatEIU/Services/FirebaseBearerSession.cs`
- `Backend/WebChatEIU/Services/FirebaseChatMessageService.cs`
- `Backend/WebChatEIU/Services/FirebaseChatRoomService.cs`
- `Backend/WebChatEIU/Services/FirebaseUserService.cs`
- `Backend/WebChatEIU.IntegrationTests/HttpSecurity/HttpSecurityHost.cs`
- `Backend/WebChatEIU.IntegrationTests/SignalRSecurity/SignalRScenario.cs`
- `Backend/WebChatEIU.IntegrationTests/SignalRSecurity/SignalRTransportTests.cs`
- `Backend/PHASE_2D_3B_SIGNALR_SECURITY_REVIEW.md`
