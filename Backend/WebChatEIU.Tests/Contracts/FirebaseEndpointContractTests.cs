using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using WebChatEIU.Controllers;
using WebChatEIU.Hubs;

namespace WebChatEIU.Tests.Contracts;

// Metadata checks only: no controller, hub, host, or Firebase client is constructed.
public class FirebaseEndpointContractTests
{
    [Fact]
    public void ContinuationDecisionEndpoint_IsNoLongerExposed()
    {
        var actions = typeof(FirebaseChatRoomsController).GetMethods();
        Assert.DoesNotContain(actions.SelectMany(action => action.GetCustomAttributes<HttpMethodAttribute>()),
            attribute => attribute.Template == "{roomId}/decision");
    }

    public static TheoryData<Type, string, string, string, bool> Endpoints => new()
    {
        { typeof(FirebaseAuthController), nameof(FirebaseAuthController.Register), "POST", "api/firebase-auth/register", false },
        { typeof(FirebaseAuthController), nameof(FirebaseAuthController.SyncVerification), "POST", "api/firebase-auth/sync-verification", true },
        { typeof(FirebaseAuthController), nameof(FirebaseAuthController.ResendVerification), "POST", "api/firebase-auth/resend-verification", false },
        { typeof(FirebaseAuthTestController), nameof(FirebaseAuthTestController.GetMe), "GET", "api/firebase-auth-test/me", true },
        { typeof(FirebaseSessionController), nameof(FirebaseSessionController.GetMe), "GET", "api/firebase-session/me", true },
        { typeof(FirebaseChatMessagesController), nameof(FirebaseChatMessagesController.SendMessage), "POST", "api/firebase-chat-messages", true },
        { typeof(FirebaseChatMessagesController), nameof(FirebaseChatMessagesController.GetMessages), "GET", "api/firebase-chat-messages/{roomId}", true },
        { typeof(FirebaseChatRoomsController), nameof(FirebaseChatRoomsController.TestCreateRoom), "POST", "api/firebase-chat-rooms/test-create", true },
        { typeof(FirebaseChatRoomsController), nameof(FirebaseChatRoomsController.GetRoomById), "GET", "api/firebase-chat-rooms/{roomId}", true },
        { typeof(FirebaseChatRoomsController), nameof(FirebaseChatRoomsController.CreateReport), "POST", "api/firebase-chat-rooms/{roomId}/reports", true },
        { typeof(FirebaseChatRoomsController), nameof(FirebaseChatRoomsController.UnmatchRoom), "POST", "api/firebase-chat-rooms/{roomId}/unmatch", true },
        { typeof(FirebaseChatRoomsController), nameof(FirebaseChatRoomsController.EndTemporaryChat), "POST", "api/firebase-chat-rooms/{roomId}/end-chat", true },
        { typeof(FirebaseChatRoomsController), nameof(FirebaseChatRoomsController.TestCleanup), "POST", "api/firebase-chat-rooms/{roomId}/test-cleanup", true },
        { typeof(FirebaseMatchReservationsController), nameof(FirebaseMatchReservationsController.TestCreate), "POST", "api/firebase-match-reservations/test-create", true },
        { typeof(FirebaseMatchReservationsController), nameof(FirebaseMatchReservationsController.Ready), "POST", "api/firebase-match-reservations/{reservationId}/ready", true },
        { typeof(FirebaseRandomMatchController), nameof(FirebaseRandomMatchController.FindMatch), "POST", "api/firebase-random-match/find", true },
        { typeof(FirebaseRandomMatchController), nameof(FirebaseRandomMatchController.Heartbeat), "POST", "api/firebase-random-match/heartbeat", true },
        { typeof(FirebaseRandomMatchController), nameof(FirebaseRandomMatchController.CancelMatch), "POST", "api/firebase-random-match/cancel", true }
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public void Endpoint_RouteVerbAndAuthorization_MatchContract(
        Type controller, string actionName, string httpMethod, string expectedRoute,
        bool requiresFirebaseUser)
    {
        Assert.NotNull(controller.GetCustomAttribute<ApiControllerAttribute>(inherit: true));
        var route = Assert.Single(controller.GetCustomAttributes<RouteAttribute>(inherit: true));
        var action = controller.GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(action);
        var verb = Assert.Single(action.GetCustomAttributes<HttpMethodAttribute>(inherit: true));
        Assert.Equal(httpMethod, Assert.Single(verb.HttpMethods));
        var fullRoute = string.Join("/", new[] { route.Template, verb.Template }
            .Where(segment => !string.IsNullOrEmpty(segment)));
        Assert.Equal(expectedRoute, fullRoute);

        var authorization = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true)).ToArray();
        if (requiresFirebaseUser)
        {
            var policy = Assert.Single(authorization);
            Assert.Equal("FirebaseUser", policy.Policy);
            Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        }
        else
        {
            // These actions must remain accessible before account verification/login.
            Assert.Empty(authorization);
        }
    }

    [Fact]
    public void ResendVerification_RetainsItsRateLimitPolicy()
    {
        var action = typeof(FirebaseAuthController)
            .GetMethod(nameof(FirebaseAuthController.ResendVerification))!;
        var limiter = action.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(limiter);
        Assert.Equal("ResendVerification", limiter.PolicyName);
        Assert.Null(action.GetCustomAttribute<DisableRateLimitingAttribute>());
        Assert.Null(typeof(FirebaseAuthController).GetCustomAttribute<DisableRateLimitingAttribute>());
    }

    [Fact]
    public void FirebaseChatHub_RequiresFirebaseUserAndDoesNotAllowAnonymousAccess()
    {
        var hub = typeof(FirebaseChatHub);
        var policy = Assert.Single(hub.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        Assert.Equal("FirebaseUser", policy.Policy);
        Assert.Empty(hub.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        foreach (var method in hub.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                     BindingFlags.DeclaredOnly))
        {
            Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        }
    }
}
