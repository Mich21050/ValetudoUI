using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ValetudoUI.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCheckpointUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RunType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StartedBeforeAppStart = table.Column<bool>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    AreaCm2 = table.Column<long>(type: "INTEGER", nullable: true),
                    SegmentIds = table.Column<string>(type: "TEXT", nullable: false),
                    SegmentNames = table.Column<string>(type: "TEXT", nullable: false),
                    SegmentsFromHint = table.Column<bool>(type: "INTEGER", nullable: false),
                    FanPreset = table.Column<string>(type: "TEXT", nullable: true),
                    WaterPreset = table.Column<string>(type: "TEXT", nullable: true),
                    OperationMode = table.Column<string>(type: "TEXT", nullable: true),
                    EndStatus = table.Column<string>(type: "TEXT", nullable: true),
                    EndFlag = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    MapNonce = table.Column<string>(type: "TEXT", nullable: true),
                    PathPointCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PathSegmentCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunMaps",
                columns: table => new
                {
                    CleaningRunId = table.Column<int>(type: "INTEGER", nullable: false),
                    MapJsonGzip = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PathJsonGzip = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunMaps", x => x.CleaningRunId);
                    table.ForeignKey(
                        name: "FK_RunMaps_Runs_CleaningRunId",
                        column: x => x.CleaningRunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_StartedUtc",
                table: "Runs",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_State",
                table: "Runs",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunMaps");

            migrationBuilder.DropTable(
                name: "Runs");
        }
    }
}
