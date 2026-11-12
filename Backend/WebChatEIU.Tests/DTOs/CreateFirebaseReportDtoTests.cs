using WebChatEIU.DTOs;

namespace WebChatEIU.Tests.DTOs;

public class CreateFirebaseReportDtoTests
{
    private static CreateFirebaseReportDto ValidReport() => new()
    {
        ReportType = "User",
        Reason = "Harassment"
    };

    [Theory]
    [InlineData("User")]
    [InlineData("Message")]
    public void SupportedReportType_IsValid(string reportType)
    {
        var dto = ValidReport();
        dto.ReportType = reportType;
        Assert.Empty(DtoValidation.Validate(dto));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("user")]
    [InlineData("message")]
    [InlineData("Other")]
    public void MissingOrUnsupportedReportType_IsInvalid(string? reportType)
    {
        var dto = ValidReport();
        dto.ReportType = reportType!;
        DtoValidation.AssertInvalidMember(dto, nameof(dto.ReportType));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingReason_IsInvalid(string? reason)
    {
        var dto = ValidReport();
        dto.Reason = reason!;
        DtoValidation.AssertInvalidMember(dto, nameof(dto.Reason));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void Reason_LengthLimit_IsEnforced(int length, bool expectedValid)
    {
        var dto = ValidReport();
        dto.Reason = new string('a', length);
        Assert.Equal(expectedValid, DtoValidation.Validate(dto).Count == 0);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void OptionalDescription_LengthLimit_IsEnforced(int length, bool expectedValid)
    {
        var dto = ValidReport();
        dto.Description = new string('a', length);
        Assert.Equal(expectedValid, DtoValidation.Validate(dto).Count == 0);
    }

    [Fact]
    public void OptionalDescription_CanBeNull() =>
        Assert.Empty(DtoValidation.Validate(ValidReport()));

    // Message-ID requirements are enforced by the service, not this DTO.
    [Fact]
    public void MessageReport_WithoutMessageId_PassesDtoValidation()
    {
        var dto = ValidReport();
        dto.ReportType = "Message";
        Assert.Null(dto.ReportedMessageId);
        Assert.Empty(DtoValidation.Validate(dto));
    }
}
