using System.Diagnostics;
using DynamicModules.WebAPI.Setup;
using FluentAssertions;

namespace DynamicModules.WebAPITests.Setup;

/// <summary>
/// 啟動順序與命令列約定（in1a 審查必修 1、2）：
/// 動詞旗標（<c>--install-init</c>、<c>--backup-now</c>）放前面或最後都要成立；設定模式主機也要掛服務管理員的生命週期。
/// </summary>
public class StartupOrderTests
{
    internal static async Task<(int Code, string Stdout, string Stderr)> RunProgram(params string[] args)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "DynamicModules.WebAPI.dll"));
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(cts.Token);
        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary>--install-init 在命令列的位置：最前面、中間、最後面。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task InstallInit_FlagPosition_DoesNotMatter(int index)
    {
        var root = Path.Combine(Path.GetTempPath(), $"fw-verb-{Guid.NewGuid():N}");
        var configFile = Path.Combine(root, "etc", "formwright.json");
        try
        {
            var args = new List<string>
            {
                $"--{InstallConfig.ConfigFileKey}={configFile}",
                $"--{InstallConfig.DataDirectoryKey}={Path.Combine(root, "data")}",
                $"--{InstallConfig.BackupDirectoryKey}={Path.Combine(root, "backup")}",
            };
            args.Insert(index, "--install-init");

            var (code, stdout, stderr) = await RunProgram([.. args]);

            code.Should().Be(0, stdout + stderr);
            stdout.Should().Contain("FORMWRIGHT_SETUP_CODE=");
            File.Exists(configFile).Should().BeTrue();
        }
        finally { try { Directory.Delete(root, true); } catch { /* 暫存 */ } }
    }

    /// <summary>
    /// 設定模式在 <c>IsPending</c> 就 return，所以 <c>AddWindowsService</c>／<c>AddSystemd</c> 必須排在它前面，否則 Windows 服務
    /// 回報不了已啟動（SCM 1053）、systemd 的 <c>Type=notify</c> 收不到 READY。兩者只在服務管理員底下才生效，測試機上無法實際觸發，
    /// 所以守的是 <c>Program.cs</c> 裡的順序。
    /// </summary>
    [Fact]
    public void ServiceLifetimes_AreRegistered_BeforeSetupModeReturns()
    {
        var program = File.ReadAllText(ProgramFile());
        var pending = program.IndexOf("InstallConfig.IsPending(", StringComparison.Ordinal);
        var windows = program.IndexOf("AddWindowsService(", StringComparison.Ordinal);
        var systemd = program.IndexOf("AddSystemd(", StringComparison.Ordinal);

        pending.Should().BeGreaterThan(0);
        windows.Should().BeGreaterThan(0).And.BeLessThan(pending, "設定模式主機也要向 Windows 服務管理員回報啟動");
        systemd.Should().BeGreaterThan(0).And.BeLessThan(pending, "設定模式主機也要向 systemd 送 READY=1");
    }

    private static string ProgramFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DynamicModules.WebAPI", "Program.cs");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到 src/DynamicModules.WebAPI/Program.cs");
    }
}
