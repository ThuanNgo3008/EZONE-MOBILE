using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebChatEIU.Migrations
{
    /// <inheritdoc />
    public partial class AddBanSourceToUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BanSource",
                table: "Users",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BanSource",
                table: "Users");
        }
    }
}
