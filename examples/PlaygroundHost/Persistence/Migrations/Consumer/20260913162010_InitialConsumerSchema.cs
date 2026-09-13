using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlaygroundHost.Persistence.Migrations.Consumer
{
    /// <inheritdoc />
    public partial class InitialConsumerSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InboxMessageEntity",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SerializedProperties = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    TransportName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessageEntity", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ManagementOperationEntity",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Operation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Actor = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    FilterJson = table.Column<string>(type: "text", nullable: true),
                    ProcessedCount = table.Column<long>(type: "bigint", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagementOperationEntity", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessageEntity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCount = table.Column<short>(type: "smallint", nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    FailedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsPoisoned = table.Column<bool>(type: "boolean", nullable: false),
                    ProcessingStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    RequeuedCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    SerializedProperties = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    TransportName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessageEntity", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboxHandlerStatusEntity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<string>(type: "character varying(200)", nullable: false),
                    HandlerKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ErrorCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ProcessingStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsPoisoned = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    RequeuedCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxHandlerStatusEntity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboxHandlerStatusEntity_InboxMessageEntity_MessageId",
                        column: x => x.MessageId,
                        principalTable: "InboxMessageEntity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboxHandlerStatuses_Processing",
                table: "InboxHandlerStatusEntity",
                columns: new[] { "CompletedAt", "IsPoisoned", "NextAttemptAt", "ProcessingStartedAt", "MessageId" },
                filter: "\"CompletedAt\" IS NULL AND \"IsPoisoned\" = false");

            migrationBuilder.CreateIndex(
                name: "UX_InboxHandlerStatuses_MessageId_HandlerKey",
                table: "InboxHandlerStatusEntity",
                columns: new[] { "MessageId", "HandlerKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManagementOperations_CompletedAt",
                table: "ManagementOperationEntity",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Processing",
                table: "OutboxMessageEntity",
                columns: new[] { "ProcessedAt", "IsPoisoned", "ScheduledAt", "NextAttemptAt", "ProcessingStartedAt", "CreatedAt" },
                filter: "\"ProcessedAt\" IS NULL AND \"IsPoisoned\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxHandlerStatusEntity");

            migrationBuilder.DropTable(
                name: "ManagementOperationEntity");

            migrationBuilder.DropTable(
                name: "OutboxMessageEntity");

            migrationBuilder.DropTable(
                name: "InboxMessageEntity");
        }
    }
}
