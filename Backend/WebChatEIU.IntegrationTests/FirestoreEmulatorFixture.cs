using Google.Cloud.Firestore;

namespace WebChatEIU.IntegrationTests;

[CollectionDefinition("Firestore emulator", DisableParallelization = true)]
public sealed class FirestoreEmulatorCollection : ICollectionFixture<FirestoreEmulatorFixture>;

public sealed class FirestoreEmulatorFixture
{
    // Construction fails before any RPC when project/host guards are not satisfied.
    public FirestoreDb Database { get; } = FirestoreEmulatorSettings.FromEnvironment().CreateDatabase();

    public DocumentReference NewSmokeDocument() => Database
        .Collection("regressionSmoke")
        .Document(Guid.NewGuid().ToString("N"));
}
