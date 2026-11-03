using WebChatEIU.DTOs;

namespace WebChatEIU.Tests.DTOs;

public class SubmitRoomDecisionDtoTests
{
    [Fact]
    public void MissingDecision_IsInvalid() =>
        DtoValidation.AssertInvalidMember(new SubmitRoomDecisionDto(),
            nameof(SubmitRoomDecisionDto.ContinueChat));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitDecision_IsValid(bool continueChat) =>
        Assert.Empty(DtoValidation.Validate(new SubmitRoomDecisionDto
        {
            ContinueChat = continueChat
        }));
}
