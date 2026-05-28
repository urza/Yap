using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yap.Migrations
{
    /// <inheritdoc />
    public partial class gifs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecentGifs",
                table: "Users",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GifAttachments",
                table: "Messages",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "GifEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceProviderId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Mp4Url = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    WebmUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    GifUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    RemoteMp4Url = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    RemoteWebmUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    RemoteGifUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    UploadedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UseCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ReferenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OriginalContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TranscodeStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GifEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GifEntries_Users_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "FavoriteGifs",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GifEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FavoriteGifs", x => new { x.UserId, x.GifEntryId });
                    table.ForeignKey(
                        name: "FK_FavoriteGifs_GifEntries_GifEntryId",
                        column: x => x.GifEntryId,
                        principalTable: "GifEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FavoriteGifs_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FavoriteGifs_GifEntryId",
                table: "FavoriteGifs",
                column: "GifEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_GifEntries_LastUsedAt",
                table: "GifEntries",
                column: "LastUsedAt");

            migrationBuilder.CreateIndex(
                name: "IX_GifEntries_SourceProviderId_SourceId",
                table: "GifEntries",
                columns: new[] { "SourceProviderId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_GifEntries_UploadedByUserId",
                table: "GifEntries",
                column: "UploadedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FavoriteGifs");

            migrationBuilder.DropTable(
                name: "GifEntries");

            migrationBuilder.DropColumn(
                name: "RecentGifs",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "GifAttachments",
                table: "Messages");
        }
    }
}
