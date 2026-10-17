using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Gorodki.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CachesAndInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "caches_failures",
                schema: "app",
                table: "runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "caches_processed_at",
                schema: "app",
                table: "runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "caches_retry_at",
                schema: "app",
                table: "runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "caches",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<short>(type: "smallint", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    rarity = table.Column<short>(type: "smallint", nullable: false),
                    hint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    latitude = table.Column<double>(type: "double precision", nullable: false),
                    longitude = table.Column<double>(type: "double precision", nullable: false),
                    set_version = table.Column<int>(type: "integer", nullable: false),
                    proposal = table.Column<bool>(type: "boolean", nullable: false),
                    osm_ref = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_caches", x => x.id);
                    table.CheckConstraint("ck_caches_coordinates", "latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180");
                    table.CheckConstraint("ck_caches_rarity", "rarity BETWEEN 0 AND 3");
                    table.CheckConstraint("ck_caches_set_version", "set_version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "inventory_items",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    remaining_meters = table.Column<double>(type: "double precision", nullable: true),
                    source_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    game_day = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventory_items", x => x.id);
                    table.CheckConstraint("ck_inventory_items_expires", "expires_at > received_at");
                    table.CheckConstraint("ck_inventory_items_kind", "kind BETWEEN 0 AND 4");
                    table.CheckConstraint("ck_inventory_items_remaining", "remaining_meters IS NULL OR remaining_meters >= 0");
                    table.ForeignKey(
                        name: "fk_inventory_items_runs_source_run_id",
                        column: x => x.source_run_id,
                        principalSchema: "app",
                        principalTable: "runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_inventory_items_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cache_finds",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cache_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    found_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    visible_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    set_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cache_finds", x => x.id);
                    table.ForeignKey(
                        name: "fk_cache_finds_caches_cache_id",
                        column: x => x.cache_id,
                        principalSchema: "app",
                        principalTable: "caches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_cache_finds_runs_run_id",
                        column: x => x.run_id,
                        principalSchema: "app",
                        principalTable: "runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_cache_finds_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "app",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "app",
                table: "caches",
                columns: new[] { "id", "hint", "latitude", "longitude", "name", "number", "osm_ref", "proposal", "rarity", "set_version" },
                values: new object[,]
                {
                    { new Guid("0042143d-accd-4a67-b486-49b70d3e38bb"), "Фонтан носит имя знаменитого скульптора. Ищи его к востоку от политеха, у большой улицы.", 52.10004, 23.768896999999999, "Фонтан мастера", (short)3, "way/415068481", true, (short)0, 1 },
                    { new Guid("01ace59d-63e5-469e-b89b-7a369657f353"), "На нём не сыграешь ни гамму, ни вальс: он из стали и стоит под открытым небом недалеко от политеха.", 52.097786999999997, 23.751828, "Стальной рояль", (short)2, "node/9197166787", true, (short)0, 1 },
                    { new Guid("1f12677e-2c74-4397-bae4-79c3ee1cb14a"), "Старое военное слово дало имя тихому скверу к северу от центра.", 52.097808000000001, 23.701319999999999, "Сквер интенданта", (short)16, "way/875741269", true, (short)1, 1 },
                    { new Guid("2f77ffea-e9a9-4ed1-ac30-a923f234e862"), "Небольшой сквер к югу от политеха, где тише, чем на Московской.", 52.091813999999999, 23.761987000000001, "Тихий Гузнянский", (short)15, "way/991879991", true, (short)1, 1 },
                    { new Guid("3629fb3c-32ec-45f2-9bcc-bb41c9d1d7a4"), "На севере города есть сквер, названный по стороне света. Тропинка в нём без асфальта.", 52.105231000000003, 23.676822000000001, "Северный ветер", (short)19, "way/539306275", true, (short)2, 1 },
                    { new Guid("3f54d96a-75e9-4ff0-901f-071751fb7b0f"), "Здесь славят тех, кто привозил Брестчине олимпийские медали.", 52.093108999999998, 23.733629000000001, "Олимпийская слава", (short)5, "node/11186552014", true, (short)0, 1 },
                    { new Guid("53a87233-46c7-4b1a-bfd5-c5ebe7b758e8"), "Шесть граней будущего у самой длинной улицы города.", 52.095897999999998, 23.742808, "Шесть граней", (short)13, "node/9200336648", true, (short)1, 1 },
                    { new Guid("678e10dd-89d3-4acd-8747-5a7cebad2f71"), "Парк назван в честь круглой даты города. Ищи в его глубине, подальше от улиц.", 52.064715999999997, 23.709900999999999, "Тысячелетний", (short)17, "way/832988462", true, (short)2, 1 },
                    { new Guid("6de69465-5981-4444-9250-f39104685039"), "Парк с самым мирным названием. Его середина — вдали от обеих улиц.", 52.110857000000003, 23.761596999999998, "Голубь мира", (short)18, "way/1000038416", true, (short)2, 1 },
                    { new Guid("71912e6d-d752-4fd2-b97e-2e7f7f9492c8"), "В старом городском саду есть скамья, на которой удобно мириться.", 52.085341, 23.682334000000001, "Скамья примирения", (short)9, "node/9867293107", true, (short)0, 1 },
                    { new Guid("73243c38-80a2-4ff9-9e29-4c12bee39c29"), "На пешеходной улице стоит сапог, который, по легенде, приносит удачу в денежных делах.", 52.093995999999997, 23.692077000000001, "Сапог удачи", (short)6, "node/12426233051", true, (short)0, 1 },
                    { new Guid("95b5675f-46c7-4d85-a1cc-0c7d1606f186"), "Парк назван по стороне света, противоположной Северному скверу. Тайник — там, где дорожки уходят от домов.", 52.059950000000001, 23.735268000000001, "Южная звезда", (short)20, "way/370233804", true, (short)3, 1 },
                    { new Guid("9b370932-a5c8-41f9-b8e5-e41dd6da9c1e"), "В сквере на Орловской камни лежат так, будто их расставили нарочно.", 52.105443000000001, 23.761761, "Каменный сад", (short)4, "node/9200345712", true, (short)0, 1 },
                    { new Guid("a47d3c03-c44c-4b40-a285-b98d574a165b"), "У реки бьёт вода, которая в реку не течёт.", 52.083022, 23.701407, "Брызги Мухавца", (short)8, "node/4238890791", true, (short)0, 1 },
                    { new Guid("a5729c38-7dc2-4cbe-8a2f-22c1d3e0651b"), "Её имя значит «светлая». Ищи у Волгоградской улицы, недалеко от гребного канала.", 52.081395999999998, 23.752575, "Ясна", (short)10, "node/12308889337", true, (short)0, 1 },
                    { new Guid("ab57c689-518b-48e5-8792-e3fbb0d1ccd9"), "На улице, где фонари зажигают вручную, кто-то потерял очень большую пуговицу.", 52.088799999999999, 23.695076, "Пуговица фонарщика", (short)7, "node/13049500844", true, (short)0, 1 },
                    { new Guid("ca344498-93f6-4119-802a-e0f92fea4dbd"), "Эти груша и яблоко не для еды: они из бетона и растут у улицы имени поэта.", 52.105956999999997, 23.740051999999999, "Бетонный сад", (short)12, "node/10937167443", true, (short)1, 1 },
                    { new Guid("e7b9f178-2812-4941-89f0-54f9d5862cbf"), "У главного корпуса БрГТУ расцвёл цветок, которому не нужен полив.", 52.097078000000003, 23.759373, "Цветок у политеха", (short)1, "node/10019033249", true, (short)0, 1 },
                    { new Guid("ee7240e7-d6f0-455f-a14a-e948d328044b"), "Они гребут без лодки и никуда не плывут, хотя вода совсем рядом.", 52.082050000000002, 23.739260999999999, "Вёсла на суше", (short)11, "node/5026997171", true, (short)1, 1 },
                    { new Guid("f9584e11-f813-4688-b015-4b47234f9766"), "Сквер носит имя писательницы, которая писала о Немане. Пройди его по дорожке.", 52.092995999999999, 23.682727, "Сквер писательницы", (short)14, "way/114575106", true, (short)1, 1 }
                });

            migrationBuilder.CreateIndex(
                name: "ix_cache_finds_cache_id_user_id",
                schema: "app",
                table: "cache_finds",
                columns: new[] { "cache_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cache_finds_run_id",
                schema: "app",
                table: "cache_finds",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_cache_finds_user_id",
                schema: "app",
                table: "cache_finds",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_caches_number",
                schema: "app",
                table: "caches",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inventory_items_source_run_id",
                schema: "app",
                table: "inventory_items",
                column: "source_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_inventory_items_user_id_received_at",
                schema: "app",
                table: "inventory_items",
                columns: new[] { "user_id", "received_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cache_finds",
                schema: "app");

            migrationBuilder.DropTable(
                name: "inventory_items",
                schema: "app");

            migrationBuilder.DropTable(
                name: "caches",
                schema: "app");

            migrationBuilder.DropColumn(
                name: "caches_failures",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "caches_processed_at",
                schema: "app",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "caches_retry_at",
                schema: "app",
                table: "runs");
        }
    }
}
