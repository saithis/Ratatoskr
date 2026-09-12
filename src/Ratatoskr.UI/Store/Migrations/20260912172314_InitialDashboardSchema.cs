// Provider-neutral by construction. The generated file carried SQLite store types
// (`type: "TEXT"`, `.HasColumnType("TEXT")`); those were removed so each provider's type mapper
// decides — otherwise a Guid primary key would become a text column on PostgreSQL. Re-apply the
// same edit if this migration is ever regenerated.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

// EF invokes these with a builder it constructed; a null guard here would be noise, and
// would be regenerated away anyway.
#pragma warning disable CA1062

namespace Ratatoskr.UI.Store.Migrations
{
    /// <inheritdoc />
    public partial class InitialDashboardSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RatatoskrDashboardAudit",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    OperationId = table.Column<Guid>(nullable: false),
                    TransportName = table.Column<string>(maxLength: 100, nullable: false),
                    ServiceName = table.Column<string>(maxLength: 200, nullable: false),
                    InstanceId = table.Column<string>(maxLength: 200, nullable: true),
                    Resource = table.Column<string>(maxLength: 200, nullable: true),
                    Operation = table.Column<string>(maxLength: 100, nullable: false),
                    Actor = table.Column<string>(maxLength: 400, nullable: true),
                    ActorDisplayName = table.Column<string>(maxLength: 400, nullable: true),
                    RequestJson = table.Column<string>(nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(nullable: false),
                    Outcome = table.Column<string>(maxLength: 50, nullable: false),
                    ErrorCode = table.Column<string>(maxLength: 100, nullable: true),
                    ResultJson = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RatatoskrDashboardAudit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RatatoskrDashboardServices",
                columns: table => new
                {
                    TransportName = table.Column<string>(maxLength: 100, nullable: false),
                    ServiceName = table.Column<string>(maxLength: 200, nullable: false),
                    InstanceId = table.Column<string>(maxLength: 200, nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(nullable: false),
                    AnnouncedAt = table.Column<DateTimeOffset>(nullable: false),
                    AnnouncementJson = table.Column<string>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RatatoskrDashboardServices", x => new { x.TransportName, x.ServiceName, x.InstanceId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_RatatoskrDashboardAudit_OperationId",
                table: "RatatoskrDashboardAudit",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_RatatoskrDashboardAudit_StartedAt",
                table: "RatatoskrDashboardAudit",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RatatoskrDashboardServices_LastSeenAt",
                table: "RatatoskrDashboardServices",
                column: "LastSeenAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RatatoskrDashboardAudit");

            migrationBuilder.DropTable(
                name: "RatatoskrDashboardServices");
        }
    }
}
#pragma warning restore CA1062
