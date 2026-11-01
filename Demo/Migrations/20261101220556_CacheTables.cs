using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Demo.Migrations
{
    /// <inheritdoc />
    public partial class CacheTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AppointmentItem_Appointments_AppointmentId",
                table: "AppointmentItem");

            migrationBuilder.DropTable(
                name: "AppointmentPrice");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AppointmentItem",
                table: "AppointmentItem");

            migrationBuilder.RenameTable(
                name: "AppointmentItem",
                newName: "AppointmentItems");

            migrationBuilder.RenameIndex(
                name: "IX_AppointmentItem_AppointmentId",
                table: "AppointmentItems",
                newName: "IX_AppointmentItems_AppointmentId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AppointmentItems",
                table: "AppointmentItems",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "AppointmentPrices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppointmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    IsStale = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentPrices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppointmentPrices_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentPrices_AppointmentId",
                table: "AppointmentPrices",
                column: "AppointmentId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AppointmentItems_Appointments_AppointmentId",
                table: "AppointmentItems",
                column: "AppointmentId",
                principalTable: "Appointments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AppointmentItems_Appointments_AppointmentId",
                table: "AppointmentItems");

            migrationBuilder.DropTable(
                name: "AppointmentPrices");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AppointmentItems",
                table: "AppointmentItems");

            migrationBuilder.RenameTable(
                name: "AppointmentItems",
                newName: "AppointmentItem");

            migrationBuilder.RenameIndex(
                name: "IX_AppointmentItems_AppointmentId",
                table: "AppointmentItem",
                newName: "IX_AppointmentItem_AppointmentId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AppointmentItem",
                table: "AppointmentItem",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "AppointmentPrice",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppointmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsStale = table.Column<bool>(type: "INTEGER", nullable: false),
                    TotalPrice = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentPrice", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppointmentPrice_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentPrice_AppointmentId",
                table: "AppointmentPrice",
                column: "AppointmentId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AppointmentItem_Appointments_AppointmentId",
                table: "AppointmentItem",
                column: "AppointmentId",
                principalTable: "Appointments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
