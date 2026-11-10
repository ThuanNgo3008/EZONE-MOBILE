using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;

namespace WebChatEIU.IntegrationTests.Auth;

[CollectionDefinition("Auth emulator", DisableParallelization = true)]
public sealed class AuthEmulatorCollection : ICollectionFixture<AuthEmulatorFixture>;

public sealed class AuthEmulatorFixture : IDisposable
{
    private readonly AuthEmulatorSettings settings;
    private readonly FirebaseApp app;
    private readonly AuthEmulatorHttpClientFactory factory;

    public AuthEmulatorFixture()
    {
        settings = AuthEmulatorSettings.FromEnvironment();
        if (FirebaseApp.DefaultInstance is not null)
            throw new InvalidOperationException("Auth tests require an untouched default FirebaseApp; refusing to reuse or delete it.");

        factory = new AuthEmulatorHttpClientFactory(settings);
        app = FirebaseApp.Create(new AppOptions
        {
            ProjectId = settings.ProjectId,
            // Synthetic, non-refreshable placeholder; never ADC, a key file, or real credentials.
            // The Admin SDK's emulator mode substitutes its local emulator authorization.
            Credential = GoogleCredential.FromAccessToken("emulator-only-placeholder"),
            HttpClientFactory = factory
        }, "regression-auth-" + Guid.NewGuid().ToString("N"));
    }

    public FirebaseAuth Auth
    {
        get
        {
            settings.AssertEnvironmentUnchanged();
            return FirebaseAuth.GetAuth(app);
        }
    }

    public string ProjectId => app.Options.ProjectId;
    public IReadOnlyCollection<Uri> Requests => factory.Requests.ToArray();

    public void Dispose()
    {
        var name = app.Name;
        app.Delete(); // Delete only this fixture's named SDK app, never DefaultInstance/DeleteAll.
        Assert.Null(FirebaseApp.GetInstance(name));
        Assert.Null(FirebaseApp.DefaultInstance);
    }
}
