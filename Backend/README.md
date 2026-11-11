# EZone 2.0 backend

ASP.NET Core 8 Web API using Firebase Authentication, Google Cloud Firestore,
SignalR, MailKit, and Swagger. Firebase ID tokens are validated by the `Firebase`
JWT bearer scheme and `FirebaseUser` authorization policy.

## Configuration

Use `WebChatEIU/appsettings.example.json` as the non-secret configuration template.
Provide `Gmail:Email` and `Gmail:AppPassword` through local configuration,
user-secrets, or environment variables (`Gmail__Email`, `Gmail__AppPassword`).
Firebase Admin and Firestore use Application Default Credentials. The current
project ID is `ezone-afa80` in Program.cs. Never commit service-account credentials.
Local configuration and existing uploaded avatars are preserved.

## Build and local tests

```powershell
dotnet build Backend/WebChatEIU/WebChatEIU.csproj
dotnet build Backend/WebChatEIU.Tests/WebChatEIU.Tests.csproj
dotnet test Backend/WebChatEIU.Tests/WebChatEIU.Tests.csproj --no-build --no-restore
```

These local tests do not start the application or worker. Starting the API also
starts its Firestore worker; use an explicitly isolated environment for runtime
verification. No Firebase end-to-end verification is implied by the local tests.
Swagger is enabled in Development and Docker environments.

## API surface

| Base route | Operations |
| --- | --- |
| `/api/firebase-auth` | register, sync-verification, resend-verification |
| `/api/firebase-session` | me |
| `/api/firebase-chat-rooms` | room detail, reports, unmatch, historical end-chat |
| `/api/firebase-chat-messages` | send and cursor-paginated history |
| `/api/firebase-random-match` | find, heartbeat, cancel |
| `/api/firebase-match-reservations` | two-sided ready confirmation |
| `/api/firebase-auth-test` | authenticated diagnostic identity |

Development-only room creation, manual cleanup, and reservation creation endpoints
remain available. SignalR is mapped at `/firebaseChatHub`; it retains Ping,
JoinRoom, SendMessage, LeaveRoom, JoinedRoom, and ReceiveMessage behavior.
REST sending persists a message; the hub send path also broadcasts it.

## Persistence and lifecycle

Collections remain `users`, `chatRooms`, `matchReservations`, `matchSessions`,
`randomMatchQueue`, `matchHistory`, `roomDecisions`, and `reports`. Messages remain
under `chatRooms/{roomId}/messages`; evidence remains under
`reports/{reportId}/evidence`. Serialized fields and pair-ID hashing are unchanged.

Reservations have a 30-second ready deadline. Both Ready atomically creates one
persistent Active anonymous room, records match history, removes owned queue
entries, and releases the reservation-owned matchSessions. Active chats do not
block another Random Match: each user can maintain multiple independent rooms.
There is no chat deadline or continuation decision. Unmatch and Report explicitly
close only the selected room; persistent messages remain available as history.
Moderation and email-verification delivery are retained.

Random Match requires both an existing, enabled Firebase Auth account with verified
email and an active/non-banned/non-deleted Firestore profile. Both users are checked
before reservation and again before new Ready/match writes. Auth lookups run outside
Firestore transactions; profiles and ownership remain checked inside them. See
[eligibility review and remaining race](PHASE_2D_1_MATCH_ELIGIBILITY.md).

FirebaseRoomExpirationWorker now has three stages: reservation expiration, stale
waiting cleanup, and historical closed-temporary message cleanup. Its delay
remains ten seconds. Reporting captures evidence; cleanup rejects Active and
persistent rooms and protects unconfirmed evidence. No automatic room closure or
Temporary-to-AwaitingDecision transition remains.

Existing Temporary/AwaitingDecision documents are not automatically converted.
Their fields, explicit end-chat/report handling, and closed-temporary cleanup are
retained for historical compatibility. The continuation decision endpoint is
removed; development test-create now creates only persistent Active rooms.
See [lifecycle refactor review and migration plan](CHAT_LIFECYCLE_REFACTOR.md).

## Scope

The existing frontend sources require rebuilding against this API. Profile editing,
account deletion, admin report management, and identity reveal do not currently
have equivalent Firebase API implementations. Do not infer these features from
model fields. Phase 2A baseline documentation is historical checkpoint evidence.
