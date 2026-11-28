using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class ReportConfiguration : IEntityTypeConfiguration<Report>
{
    public void Configure(EntityTypeBuilder<Report> b)
    {
        // 沒有擁有者的列一定是內建報表（看板設計 §8.3）
        b.ToTable("Dynamic_Report", t => t.HasCheckConstraint("CK_Report_OwnerOrBuiltIn", "\"OwnerUserId\" IS NOT NULL OR \"BuiltInCode\" IS NOT NULL"));
        b.HasKey(x => x.Id);
        b.Property(x => x.OwnerUserId).HasMaxLength(50);
        b.Property(x => x.BuiltInCode).HasMaxLength(64);
        b.HasIndex(x => x.BuiltInCode).IsUnique().HasFilter("\"BuiltInCode\" IS NOT NULL");
        b.Property(x => x.NameI18n).HasJsonConversion();
        b.Property(x => x.DefinitionJson).IsRequired();
        b.Property(x => x.RowVersion).HasMaxLength(36).IsRequired().IsConcurrencyToken();
        b.HasIndex(x => x.OwnerUserId);
        b.HasMany(x => x.Shares).WithOne().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
        b.ConfigureAudit();
    }
}

public class ReportShareConfiguration : IEntityTypeConfiguration<ReportShare>
{
    public void Configure(EntityTypeBuilder<ReportShare> b)
    {
        b.ToTable("Dynamic_ReportShare");
        b.HasKey(x => new { x.ReportId, x.GroupId });
        b.HasIndex(x => x.GroupId);
    }
}
