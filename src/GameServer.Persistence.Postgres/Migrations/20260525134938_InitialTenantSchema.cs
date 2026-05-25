using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GameServer.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialTenantSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_records",
                columns: table => new
                {
                    AuditId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CallerId = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Target = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_records", x => x.AuditId);
                });

            migrationBuilder.CreateTable(
                name: "game_versions",
                columns: table => new
                {
                    GameId = table.Column<string>(type: "text", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_versions", x => new { x.GameId, x.SchemaVersion });
                });

            migrationBuilder.CreateTable(
                name: "games",
                columns: table => new
                {
                    GameId = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    CurrentSchemaVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_games", x => x.GameId);
                });

            migrationBuilder.CreateTable(
                name: "room_events",
                columns: table => new
                {
                    RoomId = table.Column<string>(type: "text", nullable: false),
                    Tick = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    PlayerId = table.Column<string>(type: "text", nullable: false),
                    Command = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_events", x => new { x.RoomId, x.Tick, x.Ordinal });
                });

            migrationBuilder.CreateTable(
                name: "room_snapshots",
                columns: table => new
                {
                    RoomId = table.Column<string>(type: "text", nullable: false),
                    Tick = table.Column<long>(type: "bigint", nullable: false),
                    Seed = table.Column<int>(type: "integer", nullable: false),
                    GameSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_snapshots", x => x.RoomId);
                });

            migrationBuilder.CreateTable(
                name: "rooms",
                columns: table => new
                {
                    RoomId = table.Column<string>(type: "text", nullable: false),
                    GameId = table.Column<string>(type: "text", nullable: false),
                    GameSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rooms", x => x.RoomId);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    PlayerId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.SessionId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_records");

            migrationBuilder.DropTable(
                name: "game_versions");

            migrationBuilder.DropTable(
                name: "games");

            migrationBuilder.DropTable(
                name: "room_events");

            migrationBuilder.DropTable(
                name: "room_snapshots");

            migrationBuilder.DropTable(
                name: "rooms");

            migrationBuilder.DropTable(
                name: "sessions");
        }
    }
}
