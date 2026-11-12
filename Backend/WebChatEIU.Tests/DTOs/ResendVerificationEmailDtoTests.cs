using WebChatEIU.DTOs;

namespace WebChatEIU.Tests.DTOs;

public class ResendVerificationEmailDtoTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("student@gmail.com")]
    [InlineData("student@sub.eiu.edu.vn")]
    [InlineData("student@eiu.edu.vn.example.com")]
    public void MissingMalformedOrNonSchoolEmail_IsInvalid(string? email) =>
        DtoValidation.AssertInvalidMember(new ResendVerificationEmailDto { Email = email! },
            nameof(ResendVerificationEmailDto.Email));

    [Theory]
    [InlineData("student@eiu.edu.vn")]
    [InlineData("student.name+tag@eiu.edu.vn")]
    public void ValidSchoolEmail_IsValid(string email) =>
        Assert.Empty(DtoValidation.Validate(new ResendVerificationEmailDto { Email = email }));
}
