# Phase 2D.2 HTTP/JWT security review

## Audit before implementation

The approved baseline is `6199332e7faa68e66c4bc5052a45c164daaec486`.
Production uses ASP.NET Core's Firebase JwtBearer scheme with HTTPS discovery,
Firebase project issuer and audience, signed-token validation and lifetime
validation. Claims are left unmapped. The FirebaseUser policy requires an
authenticated principal from that scheme. The bearer event accepts query tokens
only under the Firebase chat hub path. Controllers and services apply membership
and account checks after middleware authentication. The standalone Admin token
verification service requests revocation checking but is not wired into JwtBearer.

Registration and verification resend intentionally allow anonymous callers.
Verification sync and the diagnostic identity action require FirebaseUser, as
do the session, rooms, messages, Random Match and reservations controllers and
the chat hub. Swagger in Development/Docker and static files are outside the
controller policy.

The existing harness pins the demo project, requires loopback Auth and Firestore,
rejects credential file configuration, and uses EmulatorOnly Firestore. The
Auth fixture owns the default Admin app where controller code requires one.
The runner starts both emulators and stops them automatically.

## Emulator token blocker and test design

The guarded Auth emulator can issue a real ID token through its local
email/password sign-in endpoint. Firebase documents that those tokens are
unsigned. Strict production JwtBearer therefore cannot accept them. See
[Firebase Auth Emulator documentation](https://firebase.google.com/docs/emulator-suite/connect_auth#id_tokens).

The tests will assert that the real unsigned token receives 401. Successful
HTTP tests will use an ephemeral RSA key to sign the claims obtained from that
emulator token. The test host supplies only a demo issuer, audience, and static
public signing key to the shared JwtBearer registration. This exercises the
production handler, policy, MVC routes and middleware with strict validation.
It does not verify Google's production discovery, signing keys, or token issuance.
No production validator flags, token events, response contracts, or policy rules
need to change. Production startup remains separate from guarded test hosting.

## Implementation

Production startup now calls shared service registration and middleware mapping
methods. The test host calls those same methods and supplies the guarded emulator
clients and a recording email service. Its static OpenID configuration uses only
an ephemeral public key. The production discovery and service startup settings
are unchanged. The in-memory server exercises routing, JWT authentication,
authorization, model binding and rate limiting. One loopback Kestrel case checks
the server's HTTP response to an unhandled controller exception.

| File | Change |
| --- | --- |
| WebChatEIU/Program.cs | Extract shared API registration and pipeline methods |
| WebChatEIU.IntegrationTests/WebChatEIU.IntegrationTests.csproj | Add TestHost 8.0.33 as a test dependency |
| WebChatEIU.IntegrationTests/AuthWorkflows/AuthWorkflowTestData.cs | Allow a disposable password for emulator sign-in |
| WebChatEIU.IntegrationTests/HttpSecurity/HttpSecurityHost.cs | Guarded host, emulator sign-in and local signing |
| WebChatEIU.IntegrationTests/HttpSecurity/HttpJwtSecurityTests.cs | HTTP authentication, authorization and error coverage |
| PHASE_2D_2_HTTP_JWT_REVIEW.md | Audit, findings and verification |

Run the complete emulator suite from the repository root with
`& ./Backend/FirestoreEmulator/Run-Smoke.ps1`. The new cases have the
`Category=HttpSecurity` trait. Each test owns its temporary accounts, Firestore
documents and key material. Fixture teardown removes only those records.

## Verified behavior

| Area | Observed result |
| --- | --- |
| Bearer authentication | Protected routes challenge missing, malformed, unsigned, bad-signature, expired, wrong-issuer and wrong-audience tokens with 401. |
| Successful authentication | A locally signed emulator identity reaches the session and Random Match endpoints. The hub negotiation route accepts a query token and the REST identity route rejects that transport. |
| Account checks | Session returns 401 after Auth deletion and 403 for disabled or unverified accounts. Random Match returns 409 for all three and does not add a queue entry. |
| Resource permissions | Room members can read room details and messages, send messages and report. A non-member gets 403 for room details, history, reporting and Unmatch. A reservation outsider gets 403 and cannot set Ready. |
| Validation and business errors | Invalid registration and report payloads return 400. Missing rooms and history return 404. Missing reservations and duplicate reports on the now-closed room return 409. |
| Anonymous routes | Invalid anonymous registration reaches model validation. Anonymous resend returns 200 for the first three attempts and 429 for the fourth. |
| Unhandled exception | Sync after account deletion returns an empty 500 through Kestrel. |

## Unresolved security and error-handling findings

1. JwtBearer checks token validity without consulting Firebase account state or
   revocation. A token with an authentication time older than the emulator's
   revocation cutoff still reaches the diagnostic and session endpoints. Tests
   verify that the emulator records the cutoff and that ASP.NET Core ignores it
   in the current pipeline. The expected secure result is HTTP 401 before the
   request reaches either action.
2. Account checks vary by endpoint. The diagnostic route still accepts a
   correctly signed token after the account is deleted, disabled or found to be
   unverified. A disabled account with an otherwise eligible Firestore profile
   can still read room history with its previously issued token. Session and
   matching apply live eligibility checks. The expected secure result for the
   disabled member is HTTP 401 with no message data returned.
3. Verification sync lets an Auth user-not-found exception propagate after the
   token has authenticated, resulting in an empty HTTP 500. The expected result
   is a controlled HTTP 401, including if deletion races with the sync action.

These findings characterize the approved baseline. Production changes in this
phase are limited to extracting the shared host composition. The tests cover
local JWT validation and policy behavior. Google discovery and key rotation,
production token issuance, Firebase refresh-token exchange after revocation,
and established SignalR connections are outside the exercised cases.

## Final review and remediation order

The review covers all six changed paths and all 26 HTTP cases. Production
authentication, authorization and endpoint mappings match the baseline. The
test assembly owns the ephemeral private key and supplies only its public key
to the in-memory host's static metadata for the demo project. The tests use real
JWT validation with locally signed emulator claims and explicitly reject an
unsigned emulator token. Invalid JWT cases also verify acceptance of a valid
local signature on the same route. The hub test challenges anonymous negotiation,
accepts a query token on negotiation and rejects that token transport on REST.
The room test checks both a member and a distinct outsider and verifies that
denied requests leave the room and report state intact.

1. **P1 — Enforce live account state and revocation centrally for HTTP.** After
   signature, issuer, audience and expiry validation, reject revoked tokens and
   disabled or missing accounts before protected actions run. Use one shared
   request-level check and Firebase's revocation semantics. Fail closed when Auth
   is unavailable and distinguish a transient dependency failure from an invalid
   credential. Keep verified email and Firestore eligibility as separate access
   checks so verification synchronization retains its existing flow.
   Convert the session and history characterizations to denial regressions once
   the fix lands. [Firebase session management](https://firebase.google.com/docs/auth/admin/manage-sessions)
2. **P2 — Handle deletion races in verification sync.** Map Auth user-not-found
   to a controlled 401 if the account disappears after authentication. Keep
   unexpected dependency failures distinct and add a regression for that
   race. This can ship with the central HTTP check.
3. **P2 — Extend enforcement to SignalR connections.** Apply the same account
   and revocation check at connection establishment. Add expiry handling and
   a defined revalidation or disconnect policy for established connections
   so a disabled or revoked connection cannot continue receiving messages.
   Test actual connect, join, send, receive and disconnect behavior. Negotiation
   coverage alone cannot verify established connections.
   [ASP.NET Core SignalR authentication](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-8.0)

## Verification

All three projects built with zero warnings and errors.

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Offline unit and contract | 111 | 0 | 0 |
| Existing Auth and Firestore integration | 230 | 0 | 0 |
| New HTTP/JWT integration | 26 | 0 | 0 |
| Total | 367 | 0 | 0 |

The guarded demo emulator runner exited successfully. A TCP listener check
confirmed that ports 8085, 9099, 4405, 4505 and 9150 were released. Production
Firebase and SMTP were not accessed. The branch is `codex/http-jwt-security-tests`
and the six changed paths remain unstaged for review.
