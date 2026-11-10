using System.Net;
using System.Text.Json;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.WebUtilities;
using WebChatEIU.DTOs;
using WebChatEIU.IntegrationTests.Auth;
using WebChatEIU.Models.Firestore;
using WebChatEIU.Services;

namespace WebChatEIU.IntegrationTests.AuthWorkflows;

// Both this collection and the existing named-app Auth collection run without parallelism.
[CollectionDefinition("Auth workflows", DisableParallelization = true)]
public sealed class AuthWorkflowCollection;

internal sealed class AuthWorkflowTestData : IAsyncDisposable
{
    private readonly AuthEmulatorSettings settings;
    private readonly FirebaseApp app;
    private readonly AuthWorkflowHttpClientFactory factory;
    private readonly FirebaseAuthService service;

    public AuthWorkflowTestData()
    {
        settings = AuthEmulatorSettings.FromEnvironment();
        if (FirebaseApp.DefaultInstance is not null)
            throw new InvalidOperationException("Refusing to replace or reuse an existing default Firebase app.");
        Database = FirestoreEmulatorSettings.FromEnvironment().CreateDatabase();
        factory = new AuthWorkflowHttpClientFactory(settings);
        app = FirebaseApp.Create(new AppOptions
        {
            ProjectId = settings.ProjectId,
            Credential = GoogleCredential.FromAccessToken("emulator-only-placeholder"),
            HttpClientFactory = factory
        }); // Deliberately owned default app: existing production services require DefaultInstance.
        Users = new FirebaseUserService(Database);
        service = new FirebaseAuthService(Users, Email);
        Assert.Same(FirebaseAuth.GetAuth(app), FirebaseAuth.DefaultInstance);
    }

    public FirestoreDb Database { get; }
    public FirebaseUserService Users { get; }
    public RecordingEmailService Email { get; } = new();
    public FirebaseAuthService Service { get { AssertOwnership(); return service; } }
    public FirebaseAuth Auth { get { AssertOwnership(); return FirebaseAuth.GetAuth(app); } }
    public Func<string, Task>? AfterAccountCreated { set => factory.AfterAccountCreated = value; }

    public static RegisterDto Registration() => new()
    {
        Email = "workflow-" + Guid.NewGuid().ToString("N") + "@eiu.edu.vn",
        Fullname = "Offline Workflow Student", Password = "Synthetic-fixture-password",
        MajorCode = "CSE", Cohort = "K24", BirthYear = 2000,
        Interests = new() { "Music", "Reading", "Coding" }
    };

    public async Task<string> AccountWithProfileAsync(bool verified = true, bool active = true,
        bool banned = false, bool deleted = false, bool disabled = false)
    {
        var dto = Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var account = await Auth.CreateUserAsync(new UserRecordArgs
        {
            Email = dto.Email, DisplayName = dto.Fullname, EmailVerified = verified, Disabled = disabled
        }, timeout.Token);
        await Users.CreateAsync(new FirebaseUser
        {
            UserId = account.Uid, Email = dto.Email, Fullname = dto.Fullname,
            IsActive = active, IsBanned = banned, IsDeleted = deleted,
            MajorCode = dto.MajorCode, Cohort = dto.Cohort, BirthYear = dto.BirthYear,
            Interests = dto.Interests
        });
        return account.Uid;
    }

    public async Task<string> MissingAccountWithProfileAsync()
    {
        var uid = "firestore-only-" + Guid.NewGuid().ToString("N");
        factory.OwnedAccounts.TryAdd(uid, 0);
        await Users.CreateAsync(new FirebaseUser { UserId = uid, IsActive = true });
        return uid;
    }

    public static Uri ValidateVerificationLink(string link, AuthEmulatorSettings settings)
    {
        AuthEmulatorSettings.Validate(settings.ProjectId, settings.FirestoreHost, settings.AuthHost);
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.Authority != settings.AuthHost || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/emulator/action")
            throw new InvalidOperationException("Only the configured emulator verification action is permitted.");
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (!query.TryGetValue("mode", out var mode) || mode.Count != 1 || mode[0] != "verifyEmail" ||
            !query.TryGetValue("apiKey", out var key) || key.Count != 1 || key[0] != "fake-api-key" ||
            !query.TryGetValue("oobCode", out var code) || code.Count != 1 || string.IsNullOrWhiteSpace(code[0]) ||
            query.ContainsKey("continueUrl") || query.ContainsKey("tenantId"))
            throw new InvalidOperationException("Unexpected verification action or redirect/tenant parameters.");
        return uri;
    }

    public async Task VerifyEmailAsync(VerificationEmail email)
    {
        AssertOwnership();
        var uri = ValidateVerificationLink(email.Link, settings);
        using var transport = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(10) };
        // A single, prevalidated loopback GET. Never follow the captured URL to a remote site.
        using var response = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(email.Address, body.RootElement.GetProperty("authEmulator").GetProperty("email").GetString());
    }

    private void AssertOwnership()
    {
        settings.AssertEnvironmentUnchanged();
        if (!ReferenceEquals(FirebaseApp.DefaultInstance, app))
            throw new InvalidOperationException("Default Firebase app ownership changed; refusing to use it.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            AssertOwnership();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            foreach (var uid in factory.OwnedAccounts.Keys)
            {
                try { await FirebaseAuth.GetAuth(app).DeleteUserAsync(uid, timeout.Token); }
                catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound) { }
                await Database.Collection("randomMatchQueue").Document(uid).DeleteAsync(cancellationToken: timeout.Token);
                await Database.Collection("users").Document(uid).DeleteAsync(cancellationToken: timeout.Token);
            }
        }
        finally
        {
            // Delete our exact app reference only. Never DeleteAll or an unrelated DefaultInstance.
            app.Delete();
            Assert.Null(FirebaseApp.DefaultInstance);
        }
    }
}
