# Phase 2B removal and verification report

Date: 2026-11-03 (Asia/Saigon).
Checkpoint retained: dc28bd6539ba62d42574123ef60f5c31f8ab434b.
Branch: feature/thuan-update. No new commit or push was created.

## Result

Removed the SQL-only controllers, context, entity models, migrations, hub,
in-memory matchmaking, custom JWT issuance/validation, activation-code email
support, static admin/login pages, and SQL-only tests. Removed BCrypt and all
EF package references, test InMemory provider, and unused Moq. JwtBearer remains.
Firebase is now the default authentication scheme. Removed obsolete authorization
policies only after their SQL-only consumers were removed. Updated current backend
and root documentation, the sample configuration, and the safe HTTP example.
Marked the older mobile planning document historical; frontend source is untouched.

## Remaining architecture

ASP.NET Core 8 controllers -> Firebase services -> Firebase Authentication and
Firestore. FirebaseChatHub remains mapped at /firebaseChatHub. Program retains
all 11 scoped Firebase services, the Firestore singleton, FirebaseUser policy,
resend-verification limiter, singleton ModerationService, scoped email delivery,
and FirebaseRoomExpirationWorker. The worker retains all five processing stages.
All Firebase diagnostic/development endpoints remain intact.

Firebase controller/hub/service constructor dependencies were checked against
source registrations and framework-provided services; no missing dependency was
found. This is source verification, not a running host/container validation.

31 protected Firebase/shared files were compared with the checkpoint using Git
line-ending normalization: no content changes. The Firebase bearer configuration,
active registration DTO definitions, and verification-link email method were also
compared and preserved. Firestore collections, fields, transaction boundaries,
match-history hashing, pagination, heartbeat, readiness, timers, moderation,
reporting/evidence, and worker behavior were not edited.

## Verification

- Backend build: PASSED. Initial restore/build emitted NU1900 because NuGet
  vulnerability metadata was unavailable. Retrying outside the sandbox did not
  resolve metadata availability. Subsequent full backend Rebuild with --no-restore:
  0 warnings, 0 errors.
- Test-project build: PASSED, 0 warnings, 0 errors, using
  `dotnet build Backend/WebChatEIU.Tests/WebChatEIU.Tests.csproj --nologo -v:q -p:NuGetAudit=false`.
  No package version upgrades were made. Vulnerability auditing remains unverified.
- Local tests: PASSED, 16/16 ModerationServiceTests cases, 0 failures.
  The sandboxed runner stalled and was interrupted. An outside-sandbox retry with
  `dotnet test Backend/WebChatEIU.Tests/WebChatEIU.Tests.csproj --no-build --no-restore --nologo -v:normal --blame-hang-timeout 30s`
  completed successfully. No API startup is involved in these tests.
- Existing UsersControllerTests compilation blocker disappeared with removal of
  that SQL-only test class; shared moderation tests were unchanged.
- Source search of active backend/test .cs and .csproj files found no EF,
  ApplicationDbContext, SQL provider, BCrypt, custom JWT generation/configuration,
  legacy ChatHub mapping, legacy matchmaking interface, or activation-code email references.
- Both restored dependency graphs contain no EntityFramework, SqlClient, BCrypt,
  or Moq packages.
- git diff --check: passed.
- Firebase end-to-end, Firestore transactions, actual JWT validation, SMTP delivery,
  real-time integration, and worker execution were NOT tested at runtime.

## Remaining legacy material and safety

No active SQL architecture dependency remains in backend source or restored package
graphs. Local ignored appsettings.json/appsettings.Docker.json may retain obsolete
SQL/JWT settings; they were deliberately left untouched. Phase 2A documentation
and Git history retain historical references. Existing frontend sources and older
frontend planning examples still describe the previous architecture; migration of
those sources is outside this phase.

No application/worker startup, live Firebase call, database deletion, credential
change, local configuration deletion, or asset deletion was performed. All three
existing avatar assets remain. No files under Mobile/ or Frontend/ were modified.
The next phase requires user review and approval.

## Deleted files (52)

- `Backend/WebChatEIU.Tests/Controllers/AuthControllerTests.cs`
- `Backend/WebChatEIU.Tests/Controllers/ChatReportsControllerTests.cs`
- `Backend/WebChatEIU.Tests/Controllers/RevealControllerTests.cs`
- `Backend/WebChatEIU.Tests/Controllers/UsersControllerTests.cs`
- `Backend/WebChatEIU.Tests/Models/UsersValidationTests.cs`
- `Backend/WebChatEIU.Tests/Services/MatchmakingServiceTests.cs`
- `Backend/WebChatEIU/Controllers/AuthController.cs`
- `Backend/WebChatEIU/Controllers/ChatReportsController.cs`
- `Backend/WebChatEIU/Controllers/ChatRoomsController.cs`
- `Backend/WebChatEIU/Controllers/EmailTestController.cs`
- `Backend/WebChatEIU/Controllers/MessagesController.cs`
- `Backend/WebChatEIU/Controllers/RevealController.cs`
- `Backend/WebChatEIU/Controllers/UsersController.cs`
- `Backend/WebChatEIU/DTOs/ChatReportDtos.cs`
- `Backend/WebChatEIU/DTOs/LoginRequest.cs`
- `Backend/WebChatEIU/Data/ApplicationDbContext.cs`
- `Backend/WebChatEIU/Hubs/ChatHub.cs`
- `Backend/WebChatEIU/Interfaces/IMatchmakingService.cs`
- `Backend/WebChatEIU/Migrations/20260523112724_InitialCreate.Designer.cs`
- `Backend/WebChatEIU/Migrations/20260523112724_InitialCreate.cs`
- `Backend/WebChatEIU/Migrations/20260523140832_AddRoomStatusAndMessagesToUsers.Designer.cs`
- `Backend/WebChatEIU/Migrations/20260523140832_AddRoomStatusAndMessagesToUsers.cs`
- `Backend/WebChatEIU/Migrations/20260523143009_AddRoomStatus.Designer.cs`
- `Backend/WebChatEIU/Migrations/20260523143009_AddRoomStatus.cs`
- `Backend/WebChatEIU/Migrations/20260523194454_AddAnonymousNicknamesToChatRoom.Designer.cs`
- `Backend/WebChatEIU/Migrations/20260523194454_AddAnonymousNicknamesToChatRoom.cs`
- `Backend/WebChatEIU/Migrations/20260523201051_AddExpiredRoomStatus.Designer.cs`
- `Backend/WebChatEIU/Migrations/20260523201051_AddExpiredRoomStatus.cs`
- `Backend/WebChatEIU/Migrations/20260528113820_AddRole.Designer.cs`
- `Backend/WebChatEIU/Migrations/20260528113820_AddRole.cs`
- `Backend/WebChatEIU/Migrations/20260528144718_AddRoleAndUserRoleTables.Designer.cs`
- `Backend/WebChatEIU/Migrations/20260528144718_AddRoleAndUserRoleTables.cs`
- `Backend/WebChatEIU/Migrations/20260528152849_UpdateRoleTable.Designer.cs`
- `Backend/WebChatEIU/Migrations/20260528152849_UpdateRoleTable.cs`
- `Backend/WebChatEIU/Migrations/20260528160137_UpdateRoleAndName.Designer.cs`
- `Backend/WebChatEIU/Migrations/20260528160137_UpdateRoleAndName.cs`
- `Backend/WebChatEIU/Migrations/20260529070650_RemoveRole.Designer.cs`
- `Backend/WebChatEIU/Migrations/20260529070650_RemoveRole.cs`
- `Backend/WebChatEIU/Migrations/20260805102845_AddSoftDeleteToUsers.Designer.cs`
- `Backend/WebChatEIU/Migrations/20260805102845_AddSoftDeleteToUsers.cs`
- `Backend/WebChatEIU/Migrations/20260819094006_AddBanSourceToUsers.Designer.cs`
- `Backend/WebChatEIU/Migrations/20260819094006_AddBanSourceToUsers.cs`
- `Backend/WebChatEIU/Migrations/ApplicationDbContextModelSnapshot.cs`
- `Backend/WebChatEIU/Models/ChatReports.cs`
- `Backend/WebChatEIU/Models/ChatRooms.cs`
- `Backend/WebChatEIU/Models/Messages.cs`
- `Backend/WebChatEIU/Models/Role.cs`
- `Backend/WebChatEIU/Models/UserRole.cs`
- `Backend/WebChatEIU/Models/Users.cs`
- `Backend/WebChatEIU/Services/MatchmakingService.cs`
- `Backend/WebChatEIU/wwwroot/admin-reports.html`
- `Backend/WebChatEIU/wwwroot/login.html`

## Modified files (11)

- `Backend/README.md`
- `Backend/WebChatEIU.Tests/WebChatEIU.Tests.csproj`
- `Backend/WebChatEIU/DTOs/UserDtos.cs`
- `Backend/WebChatEIU/Program.cs`
- `Backend/WebChatEIU/Services/EmailService.cs`
- `Backend/WebChatEIU/Services/IEmailService.cs`
- `Backend/WebChatEIU/WebChatEIU.csproj`
- `Backend/WebChatEIU/WebChatEIU.http`
- `Backend/WebChatEIU/appsettings.example.json`
- `README.md`
- `README_Mobile.md`

## Added files

- `Backend/PHASE_2B_REPORT.md` (this report).
