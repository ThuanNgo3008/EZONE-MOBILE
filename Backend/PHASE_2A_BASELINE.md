# Phase 2A: source checkpoint and baseline verification

Date: 2026-11-03 (Asia/Saigon).

## Scope and safety

Inspected tracked, modified, deleted, and untracked files. Preserved the existing working-tree application changes, including the pre-existing WeatherForecastController deletion. No application functionality was removed by this phase. No application startup, worker execution, Firebase access, email delivery, database operation, push, or history rewrite was performed.

The checkpoint includes backend and mobile application changes. Local appsettings.json and appsettings.Docker.json contained sensitive values and were removed from the Git index only; both local files remain intact. The placeholder-only appsettings.example.json remains tracked. A restored checkout requires local configuration/credentials to run.

Expanded .gitignore to exclude local appsettings and environment files, common service-account/ADC credential files, private-key/certificate files, secrets files, local database files, IDE user settings, and Xcode local environment settings. Ignore behavior for the two local configs and representative credential/environment filenames was verified.

Scanned current tracked/untracked text and the full staged index for private keys, service-account signatures, common token signatures, and sensitive assignments, without printing credential values. Reviewed configuration fields and assignment matches; test passwords and template placeholders are non-production fixtures. Mobile Firebase web API configuration is a public client identifier, not an Admin service-account credential. No credential findings remained in the scanned checkpoint tree. Pattern scanning cannot guarantee absence of every possible secret.

IMPORTANT: Sensitive configuration was already tracked in the parent commit. This checkpoint excludes those files from its tree but does not remove secrets from prior Git history. Do not treat the repository history as sanitized. Credential rotation/history remediation is separate work and was not attempted.

## Baseline verification

SDK used: .NET SDK 9.0.320; project target: net8.0. Existing restored dependencies were used; no fresh restore or package upgrade was performed.

- `dotnet build Backend/WebChatEIU/WebChatEIU.csproj --no-restore --nologo`: PASSED, 34 warnings, 0 errors. Warnings are nullable-reference issues in legacy models, DTOs, controllers, hub, and matchmaking service (CS8618, CS8600, CS8601, CS8602, CS8604).
- `dotnet build Backend/WebChatEIU.Tests/WebChatEIU.Tests.csproj --no-restore --nologo -v:q`: FAILED, 1 warning, 1 error.
- Existing blocker: Controllers/UsersControllerTests.cs:49, CS1729: EmailService no longer has the two-argument constructor used by the test.
- Additional test warning: Services/MatchmakingServiceTests.cs:14, CS8625: null passed to a non-nullable constructor parameter.
- Tests were not executed because the current test sources cannot compile. No stale test assembly was used. Test failures beyond compilation remain unknown.
- No fixes to business code or tests were made. A clean restore, runtime startup, and Firebase regression verification remain unverified.

## Previously untracked source captured

The following 38 source files were untracked at the start and are included in this checkpoint (including Firebase support and diagnostics):

- `Backend/WebChatEIU/Controllers/EmailTestController.cs`
- `Backend/WebChatEIU/Controllers/FirebaseAuthController.cs`
- `Backend/WebChatEIU/Controllers/FirebaseAuthTestController.cs`
- `Backend/WebChatEIU/Controllers/FirebaseChatMessagesController.cs`
- `Backend/WebChatEIU/Controllers/FirebaseChatRoomsController.cs`
- `Backend/WebChatEIU/Controllers/FirebaseMatchReservationsController.cs`
- `Backend/WebChatEIU/Controllers/FirebaseRandomMatchController.cs`
- `Backend/WebChatEIU/Controllers/FirebaseSessionController.cs`
- `Backend/WebChatEIU/DTOs/CreateFirebaseReportDto.cs`
- `Backend/WebChatEIU/DTOs/SubmitRoomDecisionDto.cs`
- `Backend/WebChatEIU/Hubs/FirebaseChatHub.cs`
- `Backend/WebChatEIU/Models/Firestore/FirebaseChatMessage.cs`
- `Backend/WebChatEIU/Models/Firestore/FirebaseChatRoom.cs`
- `Backend/WebChatEIU/Models/Firestore/FirebaseMatchHistory.cs`
- `Backend/WebChatEIU/Models/Firestore/FirebaseMatchReservation.cs`
- `Backend/WebChatEIU/Models/Firestore/FirebaseMatchSession.cs`
- `Backend/WebChatEIU/Models/Firestore/FirebaseRandomMatchQueueEntry.cs`
- `Backend/WebChatEIU/Models/Firestore/FirebaseReport.cs`
- `Backend/WebChatEIU/Models/Firestore/FirebaseRoomDecision.cs`
- `Backend/WebChatEIU/Models/Firestore/FirebaseUser.cs`
- `Backend/WebChatEIU/Services/FirebaseAuthService.cs`
- `Backend/WebChatEIU/Services/FirebaseChatMessageService.cs`
- `Backend/WebChatEIU/Services/FirebaseChatRoomService.cs`
- `Backend/WebChatEIU/Services/FirebaseMatchHistoryService.cs`
- `Backend/WebChatEIU/Services/FirebaseMatchReservationService.cs`
- `Backend/WebChatEIU/Services/FirebaseRandomMatchQueueService.cs`
- `Backend/WebChatEIU/Services/FirebaseReportService.cs`
- `Backend/WebChatEIU/Services/FirebaseRoomDecisionService.cs`
- `Backend/WebChatEIU/Services/FirebaseRoomExpirationWorker.cs`
- `Backend/WebChatEIU/Services/FirebaseTemporaryMessageCleanupService.cs`
- `Backend/WebChatEIU/Services/FirebaseTokenService.cs`
- `Backend/WebChatEIU/Services/FirebaseUserService.cs`
- `Backend/WebChatEIU/Services/VerificationEmailDeliveryException.cs`
- `Mobile/src/screens/FirebaseChatRoomScreen.js`
- `Mobile/src/services/firebaseChatApi.js`
- `Mobile/src/services/firebaseSession.js`
- `Mobile/src/services/firebaseSignalR.js`
- `Mobile/src/services/firebaseStorage.js`
