# Phase 2D.2B authentication remediation

The clean baseline was `fc6f9e40d79229550a21b42eece66855e7fc893d`.
Implementation is on `codex/auth-security-remediation`.

## Authentication audit and implementation

The API uses the Firebase JwtBearer scheme and the FirebaseUser policy. The same
scheme authenticates the SignalR handshake through its existing hub-only query
token handling. Production uses Firebase HTTPS metadata and its project issuer
and audience. The test host uses the same pipeline with static demo metadata and
an ephemeral RSA key owned by the test assembly.

The added JwtBearer event runs after cryptographic validation. It requires matching
Firebase subject and user ID strings and a positive integer `auth_time` in
Unix seconds that is not in the future. The check reads the validated token's
payload types, avoiding the claim projection that flattens singleton arrays.
It reads the account with the existing Admin client. A deleted or disabled
account fails authentication. The revocation check converts `auth_time` from
Unix seconds to a UTC instant and compares it with the SDK's UTC cutoff.
Earlier authentication times fail and equality passes, as in Firebase Admin Node.
The guard leaves email verification and Firestore profile and membership checks
to the existing endpoint and service rules.

The pinned Firebase Admin .NET 3.7.0 `VerifyIdTokenAsync(checkRevoked: true)`
revalidates the token and compares `iat` in its revocation helper. This phase
requires the session's original authentication time and already has a verified
principal. The new event uses the Admin account lookup with an `auth_time`
comparison, allowing the guarded test host to exercise the same logic using
locally signed JWTs. The production handler's signature, issuer, audience and
lifetime settings remain intact.

- [Firebase Admin Node cutoff implementation](https://github.com/firebase/firebase-admin-node/blob/master/src/auth/base-auth.ts#L1076-L1099)
- [Pinned Firebase Admin .NET implementation](https://github.com/firebase/firebase-admin-dotnet/blob/v3.7.0/FirebaseAdmin/FirebaseAdmin/Auth/AbstractFirebaseAuth.cs#L1167-L1174)

## Status changes

| Scenario | Before | After |
| --- | --- | --- |
| Revoked session calls session endpoint | 200 | 401 |
| Disabled account reads existing message history with its prior token | 200 with messages | 401 with no messages |
| Deleted account uses its prior token on a protected action | Some actions accepted the token, and Session returned 401 | 401 before action execution |
| Account is deleted between bearer account lookup and sync lookup | 500 | Controlled 401 |
| Auth lookup fails during bearer validation | The middleware did not check the account | 503 on protected routes |
| Sync action encounters an unrelated Auth lookup failure | 500 | 500 |
| Enabled but unverified account | Session 403, Find 409, Sync 400 until verified | Same |

The sync action catches only the specific user-not-found condition. Session's
existing missing-account response also catches only that condition. Account
lookup failures at the bearer stage fail closed with a ten-second lookup
budget. Anonymous registration and resend retain their existing policy.

## Lookup costs, timing and SignalR

The bearer stage adds an Admin account lookup for a cryptographically valid
token with valid session claims. ASP.NET Core reuses that authentication result
within the request. Existing action and matching lookups remain, including the
verification sync's second lookup and Ready's participant checks. No
cross-request account cache was added. Each lookup adds a network round trip
and consumes Auth capacity. Sustained requests with a valid token can amplify
quota usage, operational cost and in-flight work, especially when the SDK
retries. Malformed claims and cryptographically invalid tokens do not trigger
lookups. During an outage, protected requests can wait for the lookup budget
and then fail with 503. Production latency, quota headroom and billing were
not measured by the emulator suite. An account can still change after the
lookup and before an action reads or writes Firestore.

The shared check also applies when SignalR authenticates a connection.
Established connections still need a dedicated expiry, revalidation, and
disconnect policy. A connection established before revocation or disablement
can retain its principal and receive messages. Those connections were outside
this phase's test coverage.

## Final review

Reviewed all eight paths against the baseline. The existing signature,
issuer, audience and lifetime settings still run before the account check.
The test signing key and static issuer configuration remain confined to the
integration assembly. Updated invalid-JWT tests force lookup failure to prove
the middleware returns a JWT challenge before an account lookup.

The review found that projected claims could flatten a single-element array
into an apparently valid claim. The guard now requires scalar payload types.
Tests cover singleton arrays, fractional times and millisecond timestamps.
The revocation regression simulates a refreshed token with `iat` one second
after the cutoff and `auth_time` one second before it. It also checks that
equality succeeds. The emulator does not perform a post-revocation refresh
exchange in this case. Controlled upstream 403 and 503 replies travel through
the real Admin client and produce a single empty HTTP 503, with no bearer
challenge header or queue write.

## Changed files

- `WebChatEIU/Program.cs` wires the post-validation and challenge events.
- `WebChatEIU/Services/FirebaseBearerSession.cs` checks claims, account state,
  revocation and lookup availability.
- `WebChatEIU/Controllers/FirebaseAuthController.cs` handles the sync deletion race.
- `WebChatEIU/Controllers/FirebaseSessionController.cs` narrows its missing-account catch.
- `WebChatEIU.IntegrationTests/AuthWorkflows/AuthWorkflowHttpClientFactory.cs`
  and `AuthWorkflowTestData.cs` provide guarded lookup failure and race fixtures.
- `WebChatEIU.IntegrationTests/HttpSecurity/HttpJwtSecurityTests.cs` converts
  characterizations to regression tests and covers claim errors and email flows.
- `PHASE_2D_2B_AUTH_REMEDIATION.md` records this phase.

## Verification

The backend, offline test project, and integration project each built with zero
warnings and errors.

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Offline | 111 | 0 | 0 |
| Existing Auth and Firestore integration outside HTTP security | 230 | 0 | 0 |
| HTTP/JWT security | 41 | 0 | 0 |
| Total | 382 | 0 | 0 |

The guarded runner used demo Auth and Firestore emulators and shut them down
automatically. A listener check confirmed ports 8085, 9099, 4405, 4505 and
9150 were released. The Kestrel error test bound to loopback. No production
Firebase or SMTP was accessed. All eight changes remain unstaged and uncommitted.
