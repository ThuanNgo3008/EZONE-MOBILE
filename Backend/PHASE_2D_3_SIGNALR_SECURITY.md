# Phase 2D.3 SignalR security and transport verification

The initial working tree was clean at baseline
`02c635ea79787aa6ccb3dd374d52ba21e4d36260`. The test branch is
`codex/signalr-security-tests`.

## Contract and test scope

`FirebaseChatHub` is mapped at `/firebaseChatHub` under the FirebaseUser policy.
The shared Firebase JwtBearer scheme validates the signature, issuer, audience,
lifetime and live account/session on a new authenticated request. The hub accepts
string room IDs. It exposes Ping, JoinRoom, SendMessage and LeaveRoom and emits
JoinedRoom and ReceiveMessage. Join checks the Firestore profile, room status
and membership. Send checks the profile and moderation, persists through the
transactional message service and then publishes to the room group.

The mobile Firebase client supplies a bearer token through its token factory
and explicitly rejoins on reconnect. Its screen also subscribes to
PartnerDisconnected, which the hub does not emit. The screen compares
`senderId` for ownership while the hub currently sends `senderRole`. These
contract differences were identified by source inspection. The mobile UI
was outside the transport suite.

The suite uses real .NET HubConnection clients and JSON over TCP loopback
WebSockets against the shared production pipeline on Kestrel. Most clients
negotiate WebSockets and send bearer credentials. One client skips negotiation
and exercises a direct WebSocket upgrade with a query token, paired with a
REST rejection check. The reconnect case aborts a real ClientWebSocket and
lets the client reconnect automatically. No SignalR server calls are mocked.
SSE, long polling, MessagePack and remote proxy or TLS behavior are outside
the exercised cases.

The Auth and Firestore clients use the existing guarded demo environment.
Only the test host trusts its ephemeral signing key. The expiry case uses
zero clock skew in that test host so the comparison with a new connection
is deterministic. The application settings retain their original skew.
Fixtures dispose connections, stop and dispose Kestrel, and remove only
their own Auth accounts, room documents, messages and related history.

## Cases

| Cases | Coverage |
| ---: | --- |
| 7 | New connection rejects anonymous, malformed, expired, disabled, deleted and revoked credentials with 401. Auth lookup failure returns 503. |
| 1 | A valid query token opens a direct WebSocket on the hub and does not authenticate the REST identity endpoint. |
| 1 | A member joins two Active rooms, sends and receives room-specific persisted messages, leaves a group, and retains the other room after Unmatch. Outsider join and send fail. Closed room join and send fail. |
| 4 | Established sockets continue sending and receiving after disablement, deletion, revocation or a simulated Auth lookup failure. New connections fail. |
| 1 | An established socket continues sending and receiving after token expiration, while a new connection using that token fails. |
| 1 | A removed room member cannot join or send again from either of two existing sockets. Both still receive the current member's messages through stale group subscriptions, while the still-authorized room works. |
| 1 | An interrupted WebSocket reconnects with a different connection ID. It receives room messages only after explicit rejoin. No PartnerDisconnected arrives during the observed cycle. |

## Open security defects and proposed fixes

1. **P1 — Established connections retain Auth access.** The shared bearer guard
   rejects a new connection after disablement, deletion or revocation, while
   an existing socket can still send and receive if its Firestore profile and
   membership remain eligible. The same gap allows existing traffic while new
   account lookups fail. Define a bounded revalidation interval and failure
   policy. Reuse account status and revocation checks for connection state,
   deny protected invocations when stale, and evict or abort affected sockets
   so passive subscribers lose receive access as well.
2. **P1 — Group membership can outlive room authorization.** The suite changes
   membership on a disposable Firestore room directly. The former member's
   sends and rejoins are denied, but group delivery still reaches both old
   sockets for that UID.
   Track room subscriptions and remove only the affected connection-room
   membership when access is removed or a room is closed. Keep transactional
   Firestore authorization for sends and preserve access to other rooms.
3. **P2 — Established sockets outlive JWT expiry.** ASP.NET Core 8 supports
   `CloseOnAuthenticationExpiration`, which defaults to false and is not set
   on the current mapping. Enable it in a follow-up and test that the client
   must reconnect with a fresh token. The option handles expiry and still
   requires separate account/revocation revalidation. No in-place auth refresh
   API is proposed. [ASP.NET Core 8 SignalR configuration](https://learn.microsoft.com/en-us/aspnet/core/signalr/configuration?view=aspnetcore-8.0#advanced-http-configuration-options)

An ordinary disconnect leaves a persistent room available for rejoin.
Proposed future PartnerDisconnected semantics are room-scoped presence
after the last subscribed connection of that participant leaves. Explicit
closure needs its own event. The existing mobile handler interprets the
event as closure, so emitting a presence event would require a coordinated
client contract change. [SignalR authentication behavior](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-8.0#bearer-token-authentication)

## Final review

The five implementation paths and all 16 transport cases were reviewed
against the approved baseline. The production hub mapping, Firebase policy,
and JWT validation settings have no diff. The integration host is the only
consumer of the ephemeral key and test clock-skew setting. The earlier
regression assertions are unchanged. The final review strengthened the
room-removal case to cover two simultaneous sockets for the same UID and
prove that a separate authorized room remains available.

The current Send path persists transactionally, then broadcasts to the
group. A room or account change can race with the later group delivery.
This timing gap was identified by source review. The stale subscriptions
were reproduced directly. The separate
[Phase 2D.3B plan](PHASE_2D_3B_SIGNALR_REMEDIATION_PLAN.md) covers ordering,
per-connection revocation, fail-closed delivery and expiry. All security
findings remain unresolved in this test phase.

## Changed paths

- `WebChatEIU.IntegrationTests/WebChatEIU.IntegrationTests.csproj` adds the .NET 8 SignalR client package.
- `WebChatEIU.IntegrationTests/HttpSecurity/HttpSecurityHost.cs` exposes the guarded loopback address, supports a strict expiry test and explicitly stops the host.
- `WebChatEIU.IntegrationTests/SignalRSecurity/SignalRScenario.cs` owns connections, transport fixtures and cleanup.
- `WebChatEIU.IntegrationTests/SignalRSecurity/SignalRTransportTests.cs` covers handshake, messaging, connection state and reconnect behavior.
- `PHASE_2D_3_SIGNALR_SECURITY.md` records the findings.

## Verification

All three builds succeeded with zero warnings and errors.

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Offline unit and contract | 111 | 0 | 0 |
| Existing Auth, Firestore and HTTP/JWT integration | 271 | 0 | 0 |
| SignalR transport integration | 16 | 0 | 0 |
| Total | 398 | 0 | 0 |

The emulator runner exited successfully and stopped both emulators. A system
check confirmed that ports 8085, 9099, 4405, 4505 and 9150 were released and
that no EZone integration test host remained. No production Firebase access
occurred. The five phase paths and the separate remediation plan are unstaged
and uncommitted for review.
