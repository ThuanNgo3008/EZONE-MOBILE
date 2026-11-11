# Phase 2F authenticated chat room listing

Branch `codex/chat-room-listing` starts at `3684b858c73524cacdba4cf4a79d27c3ea3bdc3c`.

## HTTP contract

`GET /api/firebase-chat-rooms?pageSize=20&cursor=<nextCursor>`
requires a Firebase ID token in the Authorization bearer header.
`pageSize` defaults to 20 and accepts 1–50. Omit `cursor` on the first page.
Pass the returned token unchanged on the next request. A final or empty
page has `nextCursor: null`.

```json
{
  "rooms": [
    {
      "roomId": "example-room",
      "status": "Active",
      "createdAt": "2026-01-01T10:00:00Z",
      "lastActivityAt": "2026-01-01T10:02:00Z",
      "myRole": "User1",
      "partnerNickname": "Anonymous Panda"
    }
  ],
  "nextCursor": null
}
```

The query uses the authenticated UID and matches either participant
field. The payload exposes anonymous roles and fixed role-based labels.
A User1 caller sees Anonymous Panda and a User2 caller sees Anonymous Fox.
Stored nickname text is omitted even if a historical or imported document
contains private identity information. Identity reveal
has no implemented workflow, so there is no mode or identity payload.
Participant UIDs, private room fields, report evidence and message
previews are omitted. A member can discover multiple Active rooms and
can still discover a Closed room after Unmatch or Report. Closed history
remains readable under the existing message endpoint. Historical open
rooms are returned with their stored status. The current backend has no
Hide action, visibility field or private-room-list filter to apply.

The Firebase bearer guard handles missing, invalid, disabled, deleted
or revoked credentials with 401 and unavailable Auth verification with
503. An authenticated caller with a missing, inactive, banned or deleted
Firestore profile gets 403. Invalid pagination returns 400. Firestore RPC
errors in the action return a generic 503 without returning room data or
internal query details.

## Ordering and activity

The service issues one Firestore OR query with a limit of page size plus
one. Order is `CreatedAt DESC, document ID DESC`. The cursor encodes that
ordering position. It needs no cursor document read and still works if
that document is deleted. It is not an authorization token. Each page
reapplies the authenticated membership filter. Pagination is not a
point-in-time snapshot. Rooms created above a cursor appear on refresh.
Firestore documents without `CreatedAt` are excluded by the ordering.
The cursor accepts up to 2048 base64url characters and validates both
the timestamp and document ID before issuing the query.

`lastActivityAt` reads the existing room `UpdatedAt` field and falls
back to `CreatedAt` when absent. Going forward, accepted REST and SignalR
sends advance `UpdatedAt` in the same transaction as message persistence.
The update compares against the timestamp on the room read for authorization.
Firestore transaction conflicts recheck the current room on retry, and an
older send timestamp leaves newer activity intact. This adds a write to that
room when activity advances. Room lifecycle actions retain their existing updates.
Existing message activity is not backfilled. The field is display metadata.
New messages cannot reorder a cursor traversal.

## Deployment indexes

Provision or verify collection-scope composite indexes for the two OR
branches of `chatRooms` before deployment.

| Collection | Equality field | Order field | Document ID |
| --- | --- | --- | --- |
| `chatRooms` | `User1Id ASC` | `CreatedAt DESC` | `__name__ DESC` |
| `chatRooms` | `User2Id ASC` | `CreatedAt DESC` | `__name__ DESC` |

Firestore's implicit document ID direction follows the last ordered
index field, so descending `__name__` need not be explicitly configured
when `CreatedAt DESC` is last. The emulator does not verify deployed
composite indexes. Validate both pages against an isolated staging
project with these indexes before rollout. The existing single-instance
SignalR deployment restriction still applies.
[Firestore index modes and ordering](https://firebase.google.com/docs/firestore/query-data/index-overview) ·
[Firestore emulator differences](https://firebase.google.com/docs/emulator-suite/connect_firestore#how_the_cloud_firestore_emulator_differs_from_production)

## Test coverage

The HTTP cases use the existing guarded host and actual Auth and
Firestore emulators. They verify anonymous rejection, empty and populated
pages, role-independent ownership, response field privacy, timestamp ties,
continuation after the cursor room is deleted, safe cursor reuse across
accounts, invalid pagination, disabled/deleted/revoked accounts, profile
eligibility, unavailable Auth, Unmatch with preserved history, and room
discovery after mutual Ready. Hide coverage is inapplicable to the current
model. The Firestore RPC failure mapping is covered by source review.
Final review strengthened coverage for unsafe stored nicknames, oversized
cursors, room insertion between pages, missing historical activity, older
message timestamps, and rejected writes leaving activity unchanged.

All three projects built with zero warnings and errors. The final full run
passed 111 offline tests and 320 guarded integration tests, including
17 listing cases. Combined totals were 431 passed, zero failed and
zero skipped. The known Random Match emulator lock timeout did not recur
and remains unresolved. [Phase 2E audit](EZONE_BACKEND_COMPREHENSIVE_AUDIT.md)

## Changed paths

- `Backend/WebChatEIU/Controllers/FirebaseChatRoomsController.cs`
- `Backend/WebChatEIU/Services/FirebaseChatRoomService.cs`
- `Backend/WebChatEIU/Services/FirebaseChatMessageService.cs`
- `Backend/WebChatEIU.IntegrationTests/HttpSecurity/ChatRoomListingTests.cs`
- `Backend/PHASE_2F_CHAT_ROOM_LISTING.md`
