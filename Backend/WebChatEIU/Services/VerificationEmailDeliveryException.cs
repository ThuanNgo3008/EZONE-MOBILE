namespace WebChatEIU.Services
{
    public sealed class VerificationEmailDeliveryException : Exception
    {
        public string UserId { get; }

        public VerificationEmailDeliveryException(
            string userId,
            Exception innerException)
            : base(
                "Account created, but verification email could not be sent.",
                innerException)
        {
            UserId = userId;
        }
    }
}