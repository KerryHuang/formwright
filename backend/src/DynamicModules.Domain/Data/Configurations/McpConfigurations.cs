using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

/// <summary>
/// MCP 的兩張表（MCP 設計 §13.3；計畫判斷 2）。寫法照 <see cref="AuditLogEntryConfiguration"/>：時間 UTC、列舉存 camelCase 文字、
/// 不設外鍵、唯一索引不用部分索引。
/// </summary>
public class McpTokenConfiguration : IEntityTypeConfiguration<McpToken>
{
    public void Configure(EntityTypeBuilder<McpToken> b)
    {
        b.ToTable("Dynamic_McpToken");
        b.HasKey(x => x.Id);
        b.Property(x => x.UserId).HasMaxLength(50);
        b.Property(x => x.Name).HasMaxLength(McpToken.NameMax);
        b.Property(x => x.TokenHash).HasMaxLength(64);
        b.Property(x => x.Last4).HasMaxLength(4);
        b.Property(x => x.Scopes).HasMaxLength(McpToken.ScopesMax);
        b.Property(x => x.CreatedUtc).AsUtc();
        b.Property(x => x.ExpiresUtc).AsUtc();
        b.Property(x => x.LastUsedUtc).AsUtc();
        b.Property(x => x.LastUsedIp).HasMaxLength(45);
        b.Property(x => x.RevokedUtc).AsUtc();
        b.Property(x => x.RevokedBy).HasMaxLength(50);
        b.Property(x => x.RevokeReason).AsCamelText();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.UserId);
    }
}

public class McpConfirmationConfiguration : IEntityTypeConfiguration<McpConfirmation>
{
    public void Configure(EntityTypeBuilder<McpConfirmation> b)
    {
        b.ToTable("Dynamic_McpConfirmation");
        b.HasKey(x => x.Id);
        b.Property(x => x.CodeHash).HasMaxLength(64);
        b.Property(x => x.UserId).HasMaxLength(50);
        b.Property(x => x.ToolName).HasMaxLength(McpConfirmation.ToolNameMax);
        b.Property(x => x.ArgumentsHash).HasMaxLength(64);
        b.Property(x => x.TargetVersion).HasMaxLength(McpConfirmation.TargetVersionMax);
        b.Property(x => x.PreviewSummary).HasMaxLength(McpConfirmation.PreviewSummaryMax);
        b.Property(x => x.CreatedUtc).AsUtc();
        b.Property(x => x.ExpiresUtc).AsUtc();
        b.Property(x => x.UsedUtc).AsUtc();
        b.Property(x => x.VoidedUtc).AsUtc();
        b.Property(x => x.DeclinedUtc).AsUtc();
        // 既有列（mcp2b 之前）都是確認碼；預設值讓 migration 套上去時舊列讀得回來
        // 哨兵值設成不存在的列舉值：新列一律送出程式給的值，資料庫預設只給 migration 之前的舊列
        b.Property(x => x.Method).AsCamelText().HasDefaultValueSql("'code'").HasSentinel((McpConfirmationMethod)(-1));
        b.HasIndex(x => x.CodeHash).IsUnique();
        b.HasIndex(x => new { x.TokenId, x.CreatedUtc });
        b.HasIndex(x => x.ExpiresUtc);
    }
}
