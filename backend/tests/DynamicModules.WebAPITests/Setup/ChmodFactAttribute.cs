namespace DynamicModules.WebAPITests.Setup;

/// <summary>
/// 要靠檔案權限位元才成立的案例：Windows 沒有 chmod、root 不受權限位元限制，這兩種環境明確標成略過（顯示在測試報告），不靜默通過。
/// </summary>
public sealed class ChmodFactAttribute : FactAttribute
{
    public ChmodFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Windows 沒有 Unix 檔案權限位元";
        else if (Environment.UserName == "root") Skip = "root 不受檔案權限位元限制";
    }
}

/// <summary>只需要 Unix 權限位元可讀（不要求非 root）的案例：只在 Windows 略過。</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Windows 靠資料目錄的 ACL，沒有 Unix 檔案權限位元";
    }
}
