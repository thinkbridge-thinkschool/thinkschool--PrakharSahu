using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dispatch.WorkManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReservationTimeout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReservationConfirmedAt",
                schema: "workmanagement",
                table: "WorkOrders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReservationDeadline",
                schema: "workmanagement",
                table: "WorkOrders",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReservationConfirmedAt",
                schema: "workmanagement",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "ReservationDeadline",
                schema: "workmanagement",
                table: "WorkOrders");
        }
    }
}
