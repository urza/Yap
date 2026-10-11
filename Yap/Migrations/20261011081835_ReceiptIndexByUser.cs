using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yap.Migrations
{
    /// <inheritdoc />
    public partial class ReceiptIndexByUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TextSendReceipts_UserId_AcceptedAt_OperationId",
                table: "TextSendReceipts");

            migrationBuilder.CreateIndex(
                name: "IX_TextSendReceipts_UserId",
                table: "TextSendReceipts",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TextSendReceipts_UserId",
                table: "TextSendReceipts");

            migrationBuilder.CreateIndex(
                name: "IX_TextSendReceipts_UserId_AcceptedAt_OperationId",
                table: "TextSendReceipts",
                columns: new[] { "UserId", "AcceptedAt", "OperationId" });
        }
    }
}
