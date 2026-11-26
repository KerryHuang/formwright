using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class NameI18n : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 手寫（名稱多語設計 §5.2 T2，前例 SharedMailSettings）：EF 產生的是「刪 Name → 加 NameI18n」，順序改成「加 NameI18n → 搬資料 → 刪 Name」（操作本身沒改），
            // 預設值由 "" 改成 "{}"：空字串反序列化會丟 JsonException。舊名稱一律放繁中（不猜原本的語言）；空白或 NULL 存 {}。
            // JSON 由資料庫函式組（名稱裡的引號、反斜線、換行由資料庫跳脫）。PostgreSQL 用 json_build_object（欄型別是 text，不是 jsonb）；識別字加雙引號。
            // 去頭尾的字元集：半形空白、tab、LF、CR、全形空白（chr(9)、chr(10)、chr(13)、chr(12288)），和 C# Trim() 對齊到這幾種（審查 🟢7）。
            migrationBuilder.AddColumn<string>(
                name: "NameI18n",
                table: "Dynamic_TaskView",
                type: "text",
                nullable: false,
                defaultValue: "{}",
                collation: "C");

            migrationBuilder.AddColumn<string>(
                name: "NameI18n",
                table: "Dynamic_Report",
                type: "text",
                nullable: false,
                defaultValue: "{}",
                collation: "C");

            migrationBuilder.AddColumn<string>(
                name: "NameI18n",
                table: "Dynamic_Dashboard",
                type: "text",
                nullable: false,
                defaultValue: "{}",
                collation: "C");

            migrationBuilder.Sql(
                "UPDATE \"Dynamic_TaskView\" SET \"NameI18n\" = CASE WHEN \"Name\" IS NULL OR btrim(\"Name\", ' ' || chr(9) || chr(10) || chr(13) || chr(12288)) = '' THEN '{}' ELSE json_build_object('zh-TW', btrim(\"Name\", ' ' || chr(9) || chr(10) || chr(13) || chr(12288)))::text END;");

            migrationBuilder.Sql(
                "UPDATE \"Dynamic_Report\" SET \"NameI18n\" = CASE WHEN \"Name\" IS NULL OR btrim(\"Name\", ' ' || chr(9) || chr(10) || chr(13) || chr(12288)) = '' THEN '{}' ELSE json_build_object('zh-TW', btrim(\"Name\", ' ' || chr(9) || chr(10) || chr(13) || chr(12288)))::text END;");

            migrationBuilder.Sql(
                "UPDATE \"Dynamic_Dashboard\" SET \"NameI18n\" = CASE WHEN \"Name\" IS NULL OR btrim(\"Name\", ' ' || chr(9) || chr(10) || chr(13) || chr(12288)) = '' THEN '{}' ELSE json_build_object('zh-TW', btrim(\"Name\", ' ' || chr(9) || chr(10) || chr(13) || chr(12288)))::text END;");

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
                type: "text",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                collation: "C");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Dynamic_Report",
                type: "text",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                collation: "C");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Dynamic_Dashboard",
                type: "text",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                collation: "C");

            migrationBuilder.Sql(
                "UPDATE \"Dynamic_TaskView\" SET \"Name\" = coalesce(\"NameI18n\"::json ->> 'zh-TW', '');");

            migrationBuilder.Sql(
                "UPDATE \"Dynamic_Report\" SET \"Name\" = coalesce(\"NameI18n\"::json ->> 'zh-TW', '');");

            migrationBuilder.Sql(
                "UPDATE \"Dynamic_Dashboard\" SET \"Name\" = coalesce(\"NameI18n\"::json ->> 'zh-TW', '');");

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
