using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

// 帳號安全（帳號安全設計 §5）：不設外鍵（同 McpToken）；存 UTC 的欄位名稱都以 Utc 結尾

public class UserRecoveryCodeConfiguration : IEntityTypeConfiguration<UserRecoveryCode>
{
    public void Configure(EntityTypeBuilder<UserRecoveryCode> b)
    {
        b.ToTable("Dynamic_UserRecoveryCode");
        b.HasKey(x => x.Id);
        b.Property(x => x.UserId).HasMaxLength(50);
        b.Property(x => x.CodeHash).HasMaxLength(64);
        b.Property(x => x.CreatedUtc).AsUtc();
        b.Property(x => x.UsedUtc).AsUtc();
        b.HasIndex(x => new { x.UserId, x.CodeHash }).IsUnique();
    }
}

public class TrustedDeviceConfiguration : IEntityTypeConfiguration<TrustedDevice>
{
    public void Configure(EntityTypeBuilder<TrustedDevice> b)
    {
        b.ToTable("Dynamic_TrustedDevice");
        b.HasKey(x => x.Id);
        b.Property(x => x.UserId).HasMaxLength(50);
        b.Property(x => x.TokenHash).HasMaxLength(64);
        b.Property(x => x.SecurityStamp).HasMaxLength(50);
        b.Property(x => x.Label).HasMaxLength(100);
        b.Property(x => x.CreatedUtc).AsUtc();
        b.Property(x => x.ExpiresUtc).AsUtc();
        b.Property(x => x.LastUsedUtc).AsUtc();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.UserId);
        b.HasIndex(x => x.ExpiresUtc);
    }
}

public class AccountTokenConfiguration : IEntityTypeConfiguration<AccountToken>
{
    public void Configure(EntityTypeBuilder<AccountToken> b)
    {
        b.ToTable("Dynamic_AccountToken");
        b.HasKey(x => x.Id);
        b.Property(x => x.UserId).HasMaxLength(50);
        b.Property(x => x.Purpose).HasMaxLength(20);
        b.Property(x => x.TokenHash).HasMaxLength(64);
        b.Property(x => x.Email).HasMaxLength(AppUser.EmailMax);
        b.Property(x => x.CreatedUtc).AsUtc();
        b.Property(x => x.ExpiresUtc).AsUtc();
        b.Property(x => x.UsedUtc).AsUtc();
        b.Property(x => x.RevokedReason).HasMaxLength(20);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => new { x.UserId, x.Purpose });
        b.HasIndex(x => x.ExpiresUtc);
    }
}

public class MailOutboxConfiguration : IEntityTypeConfiguration<MailOutbox>
{
    public void Configure(EntityTypeBuilder<MailOutbox> b)
    {
        b.ToTable("Dynamic_MailOutbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasMaxLength(MailOutbox.KindMax);
        b.Property(x => x.UserId).HasMaxLength(50);
        b.Property(x => x.ToAddress).HasMaxLength(AppUser.EmailMax);
        b.Property(x => x.Locale).HasMaxLength(MailOutbox.LocaleMax);
        b.Property(x => x.ParamsJson).HasMaxLength(MailOutbox.ParamsMax);
        b.Property(x => x.CreatedUtc).AsUtc();
        b.Property(x => x.NotAfterUtc).AsUtc();
        b.Property(x => x.NextAttemptUtc).AsUtc();
        b.Property(x => x.Status).AsCamelText();
        b.Property(x => x.ClaimedBy).HasMaxLength(100);
        b.Property(x => x.ClaimedUntilUtc).AsUtc();
        b.Property(x => x.SentUtc).AsUtc();
        b.Property(x => x.LastError).HasMaxLength(MailOutbox.ErrorMax);
        b.Property(x => x.PreviewBody).HasMaxLength(MailOutbox.PreviewMax);
        b.HasIndex(x => new { x.Status, x.NextAttemptUtc });
        b.HasIndex(x => x.CreatedUtc);
        b.HasIndex(x => new { x.UserId, x.Status });
    }
}
