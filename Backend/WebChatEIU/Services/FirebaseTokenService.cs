using FirebaseAdmin.Auth;

namespace WebChatEIU.Services
{
    public class FirebaseTokenService
    {
        public async Task<string> VerifyTokenAsync(string idToken)
        {
            if (string.IsNullOrWhiteSpace(idToken))
                throw new ArgumentException("Token is required.");

            var decodedToken = await FirebaseAuth.DefaultInstance
                .VerifyIdTokenAsync(idToken, true);

            return decodedToken.Uid;
        }
    }
}
