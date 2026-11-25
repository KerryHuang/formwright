namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 範本套件（kind: template）的測試套件：虛構的範本 tpl-a、tpl-r 與一般應用程式 app-*。
/// id 都不是內建的，不放進 backend/apps/。<see cref="Template"/> 與 <see cref="App"/> 產出只有一個模組 item 的最小套件。
/// </summary>
internal static class AppTestPackagesTemplate
{
    private const string AllPerms = """
        { "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": true, "approve": true }
        """;

    /// <summary>範本 tpl-a：requires.masters partner-lite（見 <see cref="AppTestPackagesV6.Master"/>），兩個模組 item 與 record，record 參照 item。</summary>
    public static readonly string TplA = $$"""
        {
          "format": "formwright-app", "formatVersion": 8,
          "manifest": {
            "id": "tpl-a", "kind": "template", "version": "1.0.0", "name": { "zh-TW": "範本甲（測試）" },
            "requires": { "apps": [], "entities": [], "masters": [ { "id": "partner-lite", "modules": ["partner"] } ] }
          },
          "groups": [ { "code": "staff", "name": { "zh-TW": "人員" } } ],
          "modules": [
            { "code": "item", "key": "tpl-a-item", "name": { "zh-TW": "品項" }, "kind": "standalone", "titleField": "name",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
                { "code": "name", "type": "text", "label": { "zh-TW": "名稱" }, "config": { "required": true } }
              ] } ],
              "permissions": [ { "group": "staff", "scope": "all", "privileges": {{AllPerms}} } ] },
            { "code": "record", "key": "tpl-a-record", "name": { "zh-TW": "紀錄" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "表頭" }, "fields": [
                { "code": "no", "type": "text", "label": { "zh-TW": "單號" }, "config": { "required": true } },
                { "code": "item", "type": "reference", "label": { "zh-TW": "品項" },
                  "config": { "onDelete": "restrict", "refTarget": { "kind": "module", "key": "item" } } }
              ] } ],
              "permissions": [ { "group": "staff", "scope": "all", "privileges": {{AllPerms}} } ] }
          ]
        }
        """;

    /// <summary>範本 tpl-r：requires.apps crm-sales，有欄位參照 CRM 的 opportunity。</summary>
    public static readonly string TplR = $$"""
        {
          "format": "formwright-app", "formatVersion": 8,
          "manifest": {
            "id": "tpl-r", "kind": "template", "version": "1.0.0", "name": { "zh-TW": "範本乙（測試）" },
            "requires": { "apps": [ { "id": "crm-sales", "modules": ["opportunity"] } ], "entities": [] }
          },
          "groups": [ { "code": "staff", "name": { "zh-TW": "人員" } } ],
          "modules": [
            { "code": "followup", "key": "tpl-r-followup", "name": { "zh-TW": "追蹤" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "表頭" }, "fields": [
                { "code": "no", "type": "text", "label": { "zh-TW": "單號" }, "config": { "required": true } },
                { "code": "opportunity", "type": "reference", "label": { "zh-TW": "商機" },
                  "config": { "onDelete": "restrict", "refTarget": { "kind": "module", "key": "opportunity", "app": "crm-sales" } } }
              ] } ],
              "permissions": [ { "group": "staff", "scope": "all", "privileges": {{AllPerms}} } ] }
          ]
        }
        """;

    /// <summary>只有模組 item 的最小套件；<paramref name="withApp"/> 有值就帶一個整合宣告，夥伴是該套件的 item。</summary>
    private static string Minimal(string id, string version, string kindJson, string? requiresApp, string? withApp, int formatVersion)
    {
        var apps = requiresApp is null ? "" : $$"""{ "id": "{{requiresApp}}", "modules": ["item"] }""";
        var integrations = withApp is null ? "" : $$"""
            "integrations": [ { "code": "link", "name": { "zh-TW": "串接" }, "with": [ { "id": "{{withApp}}", "modules": ["item"] } ] } ],
            """;
        return $$"""
            {
              "format": "formwright-app", "formatVersion": {{formatVersion}},
              "manifest": {
                "id": "{{id}}", {{kindJson}}"version": "{{version}}", "name": { "zh-TW": "{{id}}（測試）" },
                "requires": { "apps": [ {{apps}} ], "entities": [] }
              },
              "groups": [ { "code": "staff", "name": { "zh-TW": "人員" } } ],
              {{integrations}}
              "modules": [
                { "code": "item", "key": "{{id}}-item", "name": { "zh-TW": "品項" }, "kind": "standalone", "titleField": "name",
                  "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
                    { "code": "name", "type": "text", "label": { "zh-TW": "名稱" }, "config": { "required": true } }
                  ] } ],
                  "permissions": [ { "group": "staff", "scope": "all", "privileges": {{AllPerms}} } ] }
              ]
            }
            """;
    }

    /// <summary><paramref name="kind"/> 原樣放進 manifest.kind（測試近似值用）；傳 null 則省略 kind。</summary>
    public static string Template(string id, string version = "1.0.0", string? requiresApp = null, string? withApp = null,
        int formatVersion = 8, string? kind = "template") =>
        Minimal(id, version, kind is null ? "" : $"\"kind\": \"{kind}\", ", requiresApp, withApp, formatVersion);

    /// <summary>一般應用程式（省略 kind）。</summary>
    public static string App(string id, string version = "1.0.0", string? requiresApp = null, string? withApp = null) =>
        Minimal(id, version, "", requiresApp, withApp, 8);
}
