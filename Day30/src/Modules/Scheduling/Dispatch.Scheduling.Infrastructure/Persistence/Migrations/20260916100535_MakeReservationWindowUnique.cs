using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dispatch.Scheduling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MakeReservationWindowUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reservations_Technician_Window",
                schema: "scheduling",
                table: "Reservations");

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_Technician_Window",
                schema: "scheduling",
                table: "Reservations",
                columns: new[] { "TechnicianId", "Start", "End" },
                unique: true,
                filter: "[IsReleased] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reservations_Technician_Window",
                schema: "scheduling",
                table: "Reservations");

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_Technician_Window",
                schema: "scheduling",
                table: "Reservations",
                columns: new[] { "TechnicianId", "Start", "End" },
                filter: "[IsReleased] = 0");
        }
    }
}
