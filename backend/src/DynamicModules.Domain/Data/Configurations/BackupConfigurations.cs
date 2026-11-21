using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

/// <summary>
/// 備份的三張表（備份設計 §10）。時間 UTC、列舉存 camelCase 字串（同維運表）；唯一索引不加 filter
/// （兩種資料庫的唯一索引都允許多個 NULL，<see cref="BackupRun.ActiveSlot"/> 靠這點保證同時只有一份）。
/// </summary>
public class BackupRunConfiguration : IEntityTypeConfiguration<BackupRun>
{
    public void Configure(EntityTypeBuilder<BackupRun> b)
    {
        b.ToTable("Dynamic_BackupRun");
        b.HasKey(x => x.Id);
        b.Property(x => x.RunKey).HasMaxLength(BackupRun.RunKeyMax);
        b.Property(x => x.Trigger).AsCamelText(16);
        b.Property(x => x.ScheduleSlot).HasMaxLength(BackupRun.SlotMax);
        b.Property(x => x.LocationId).HasMaxLength(BackupRun.LocationIdMax);
        b.Property(x => x.Status).AsCamelText(16);
        b.Property(x => x.Stage).AsCamelText(16);
        b.Property(x => x.RequestedUserId).HasMaxLength(BackupRun.UserIdMax);
        b.Property(x => x.QueuedUtc).AsUtc();
        b.Property(x => x.StartedUtc).AsUtc();
        b.Property(x => x.HeartbeatUtc).AsUtc();
        b.Property(x => x.SnapshotUtc).AsUtc();
        b.Property(x => x.FinishedUtc).AsUtc();
        b.Property(x => x.OwnerInstanceId).HasMaxLength(BackupRun.InstanceMax);
        b.Property(x => x.StoreKind).HasMaxLength(BackupRun.StoreKindMax);
        b.Property(x => x.SetName).HasMaxLength(BackupRun.SetNameMax);
        b.Property(x => x.Provider).HasMaxLength(BackupRun.ProviderMax);
        b.Property(x => x.AppVersion).HasMaxLength(BackupRun.AppVersionMax);
        b.Property(x => x.MigrationId).HasMaxLength(BackupRun.MigrationIdMax);
        b.Property(x => x.ErrorCode).HasMaxLength(BackupRun.ErrorCodeMax);
        b.Property(x => x.ErrorDetail).HasMaxLength(BackupRun.ErrorDetailMax);
        b.Property(x => x.PruneError).HasMaxLength(BackupRun.ErrorDetailMax);
        b.HasIndex(x => x.RunKey).IsUnique();
        b.HasIndex(x => x.ActiveSlot).IsUnique();
        b.HasIndex(x => new { x.LocationId, x.Status, x.FinishedUtc });
        b.HasIndex(x => x.ScheduleSlot);
    }
}

public class BackupStateConfiguration : IEntityTypeConfiguration<BackupState>
{
    public void Configure(EntityTypeBuilder<BackupState> b)
    {
        b.ToTable("Dynamic_BackupState");
        b.HasKey(x => x.Kind);
        b.Property(x => x.Kind).HasMaxLength(20);
        b.Property(x => x.LocationId).HasMaxLength(BackupRun.LocationIdMax);
        b.Property(x => x.AdoptedUtc).AsUtc();
        b.Property(x => x.AdoptedByInstanceId).HasMaxLength(BackupRun.InstanceMax);
    }
}

public class OpsLockConfiguration : IEntityTypeConfiguration<OpsLock>
{
    public void Configure(EntityTypeBuilder<OpsLock> b)
    {
        b.ToTable("Dynamic_OpsLock");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(OpsLock.KeyMax);
        b.Property(x => x.Owner).HasMaxLength(OpsLock.OwnerMax);
        b.Property(x => x.UntilUtc).AsUtc();
    }
}
