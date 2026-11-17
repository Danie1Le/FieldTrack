using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FieldTrack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RejectedMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rejected_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    topic = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    topic_clipped = table.Column<bool>(type: "boolean", nullable: false),
                    payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    received_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rejected_messages", x => x.id);
                    table.CheckConstraint("ck_rejected_messages_reason", "reason IN ('BadJson', 'MissingField', 'PositionOutOfRange', 'TooFarAhead', 'TooLate', 'UnknownTopicVersion', 'UnknownSerialNumber', 'ArchivedDevice')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_rejected_messages_received_time",
                table: "rejected_messages",
                column: "received_time");

            migrationBuilder.CreateIndex(
                name: "ix_rejected_messages_topic_received_time",
                table: "rejected_messages",
                columns: new[] { "topic", "received_time" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rejected_messages");
        }
    }
}
