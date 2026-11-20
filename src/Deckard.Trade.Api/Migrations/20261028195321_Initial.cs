using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Deckard.Trade.Api.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blocks",
                columns: table => new
                {
                    BlockerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockedId = table.Column<Guid>(type: "uuid", nullable: false),
                    Created = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blocks", x => new { x.BlockerId, x.BlockedId });
                });

            migrationBuilder.CreateTable(
                name: "feedback",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Diagnostics = table.Column<string>(type: "text", nullable: true),
                    Screenshots = table.Column<int>(type: "integer", nullable: false),
                    Created = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IssueNumber = table.Column<int>(type: "integer", nullable: true),
                    IssueUrl = table.Column<string>(type: "text", nullable: true),
                    IssueClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedback", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "feedback_mutes",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedback_mutes", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "flags",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Created = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flags", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "patrons",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PatreonUserId = table.Column<string>(type: "text", nullable: false),
                    Patron = table.Column<bool>(type: "boolean", nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EarlyAccess = table.Column<bool>(type: "boolean", nullable: false),
                    CreditName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patrons", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "telemetry",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstallId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AppVersion = table.Column<string>(type: "text", nullable: false),
                    Windows = table.Column<string>(type: "text", nullable: false),
                    Channel = table.Column<string>(type: "text", nullable: false),
                    Props = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telemetry", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "telemetry_daily",
                columns: table => new
                {
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    AppVersion = table.Column<string>(type: "text", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telemetry_daily", x => new { x.Day, x.Name, x.AppVersion });
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BattleTag = table.Column<string>(type: "text", nullable: false),
                    BattleNetId = table.Column<long>(type: "bigint", nullable: true),
                    Created = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Banned = table.Column<bool>(type: "boolean", nullable: false),
                    LastSeen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    InGame = table.Column<bool>(type: "boolean", nullable: false),
                    ShowOnline = table.Column<bool>(type: "boolean", nullable: false),
                    Zone = table.Column<string>(type: "text", nullable: true),
                    ZoneAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "listings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Realm = table.Column<int>(type: "integer", nullable: false),
                    Hardcore = table.Column<bool>(type: "boolean", nullable: false),
                    Season = table.Column<int>(type: "integer", nullable: false),
                    ItemType = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Rarity = table.Column<string>(type: "text", nullable: false),
                    ItemPower = table.Column<int>(type: "integer", nullable: false),
                    Ancestral = table.Column<bool>(type: "boolean", nullable: false),
                    AspectId = table.Column<long>(type: "bigint", nullable: true),
                    ClassMask = table.Column<int>(type: "integer", nullable: false),
                    PriceGold = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<short>(type: "smallint", nullable: false),
                    Created = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Expires = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    SoldPriceGold = table.Column<long>(type: "bigint", nullable: true),
                    SoldAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_listings_users_SellerId",
                        column: x => x.SellerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wanted",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuyerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Realm = table.Column<int>(type: "integer", nullable: false),
                    Hardcore = table.Column<bool>(type: "boolean", nullable: false),
                    Season = table.Column<int>(type: "integer", nullable: false),
                    ItemType = table.Column<string>(type: "text", nullable: true),
                    Want = table.Column<string>(type: "text", nullable: false),
                    PriceGold = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Created = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Expires = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OfferId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wanted", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wanted_users_BuyerId",
                        column: x => x.BuyerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "listing_affixes",
                columns: table => new
                {
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    AffixId = table.Column<long>(type: "bigint", nullable: false),
                    Value = table.Column<double>(type: "double precision", nullable: false),
                    Greater = table.Column<bool>(type: "boolean", nullable: false),
                    Slot = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listing_affixes", x => new { x.ListingId, x.Index });
                    table.ForeignKey(
                        name: "FK_listing_affixes_listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "offers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuyerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PriceGold = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Created = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Updated = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CounterGold = table.Column<long>(type: "bigint", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    BuyerDone = table.Column<bool>(type: "boolean", nullable: false),
                    SellerDone = table.Column<bool>(type: "boolean", nullable: false),
                    BuyerReport = table.Column<string>(type: "text", nullable: true),
                    SellerReport = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_offers_listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_offers_users_BuyerId",
                        column: x => x.BuyerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_blocks_BlockedId",
                table: "blocks",
                column: "BlockedId");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_UserId_Created",
                table: "feedback",
                columns: new[] { "UserId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_listing_affixes_AffixId_Value",
                table: "listing_affixes",
                columns: new[] { "AffixId", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_listings_Realm_Hardcore_Season_Status_ItemType_PriceGold",
                table: "listings",
                columns: new[] { "Realm", "Hardcore", "Season", "Status", "ItemType", "PriceGold" });

            migrationBuilder.CreateIndex(
                name: "IX_listings_SellerId",
                table: "listings",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_offers_BuyerId_Updated",
                table: "offers",
                columns: new[] { "BuyerId", "Updated" });

            migrationBuilder.CreateIndex(
                name: "IX_offers_ListingId_Status",
                table: "offers",
                columns: new[] { "ListingId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_patrons_PatreonUserId",
                table: "patrons",
                column: "PatreonUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_telemetry_At",
                table: "telemetry",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_users_BattleNetId",
                table: "users",
                column: "BattleNetId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_BattleTag",
                table: "users",
                column: "BattleTag",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wanted_BuyerId",
                table: "wanted",
                column: "BuyerId");

            migrationBuilder.CreateIndex(
                name: "IX_wanted_Realm_Hardcore_Season_Status_ItemType_PriceGold",
                table: "wanted",
                columns: new[] { "Realm", "Hardcore", "Season", "Status", "ItemType", "PriceGold" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blocks");

            migrationBuilder.DropTable(
                name: "feedback");

            migrationBuilder.DropTable(
                name: "feedback_mutes");

            migrationBuilder.DropTable(
                name: "flags");

            migrationBuilder.DropTable(
                name: "listing_affixes");

            migrationBuilder.DropTable(
                name: "offers");

            migrationBuilder.DropTable(
                name: "patrons");

            migrationBuilder.DropTable(
                name: "telemetry");

            migrationBuilder.DropTable(
                name: "telemetry_daily");

            migrationBuilder.DropTable(
                name: "wanted");

            migrationBuilder.DropTable(
                name: "listings");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
