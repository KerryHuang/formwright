using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NameI18n : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 手寫（名稱多語設計 §5.2 T2，前例 SharedMailSettings）：EF 產生的是「刪 Name → 加 NameI18n」，順序改成「加 NameI18n → 搬資料 → 刪 Name」（操作本身沒改），
            // 預設值由 "" 改成 "{}"：空字串反序列化會丟 JsonException。舊名稱一律放繁中（不猜原本的語言）；空白或 NULL 存 {}。
            // JSON 由資料庫函式組（名稱裡的引號、反斜線、換行由資料庫跳脫）。SQLite 用 json_object／json_extract。
            // 去頭尾的字元集：半形空白、tab、LF、CR、全形空白（char(32, 9, 10, 13, 12288)），和 C# Trim() 對齊到這幾種（審查 🟢7）。
            migrationBuilder.AddColumn<string>(
                name: "NameI18n",
                table: "Dynamic_TaskView",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "NameI18n",
                table: "Dynamic_Report",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "NameI18n",
                table: "Dynamic_Dashboard",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.Sql(
                "UPDATE \"Dynamic_TaskView\" SET \"NameI18n\" = CASE WHEN \"Name\" IS NULL OR trim(\"Name\", char(32, 9, 10, 13, 12288)) = '' THEN '{}' ELSE json_object('zh-TW', trim(\"Name\", char(32, 9, 10, 13, 12288))) END;");

            migrationBuilder.Sql(
                "UPDATE \"Dynamic_Report\" SET \"NameI18n\" = CASE WHEN \"Name\" IS NULL OR trim(\"Name\", char(32, 9, 10, 13, 12288)) = '' THEN '{}' ELSE json_object('zh-TW', trim(\"Name\", char(32, 9, 10, 13, 12288))) END;");

            migrationBuilder.Sql(
                "UPDATE \"Dynamic_Dashboard\" SET \"NameI18n\" = CASE WHEN \"Name\" IS NULL OR trim(\"Name\", char(32, 9, 10, 13, 12288)) = '' THEN '{}' ELSE json_object('zh-TW', trim(\"Name\", char(32, 9, 10, 13, 12288))) END;");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Dynamic_TaskView");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Dynamic_Report");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Dynamic_Dashboard");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 手寫（§5.2）：順序改成「加 Name → 還原繁中 → 刪 NameI18n」；只還原繁中，其他語系的譯名會遺失（降版不保證無損）。
            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Dynamic_TaskView",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Dynamic_Report",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Dynamic_Dashboard",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                "UPDATE \"Dynamic_TaskView\" SET \"Name\" = coalesce(json_extract(\"NameI18n\", '$.\"zh-TW\"'), '');");

            migrationBuilder.Sql(
                "UPDATE \"Dynamic_Report\" SET \"Name\" = coalesce(json_extract(\"NameI18n\", '$.\"zh-TW\"'), '');");

            migrationBuilder.Sql(
                "UPDATE \"Dynamic_Dashboard\" SET \"Name\" = coalesce(json_extract(\"NameI18n\", '$.\"zh-TW\"'), '');");

            migrationBuilder.DropColumn(
                name: "NameI18n",
                table: "Dynamic_TaskView");

            migrationBuilder.DropColumn(
                name: "NameI18n",
                table: "Dynamic_Report");

            migrationBuilder.DropColumn(
                name: "NameI18n",
                table: "Dynamic_Dashboard");
        }
    }
}
