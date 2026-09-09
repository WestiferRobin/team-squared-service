using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Service.Api.Models;

namespace Service.Api.Infrastructure.Database.Configurations;

public sealed class ActionConfiguration : IEntityTypeConfiguration<ActionModel>
{
    public void Configure(EntityTypeBuilder<ActionModel> builder)
    {
        builder.ToTable("Actions", table =>
        {
            table.HasCheckConstraint("CK_Actions_Name_NotBlank", "\"Name\" ~ '[^[:space:]]'");
            table.HasCheckConstraint("CK_Actions_Type", "\"Type\" IN (0, 1, 2)");
        });
        builder.HasKey(action => action.Id);
        builder.Property(action => action.Id).ValueGeneratedNever();
        builder.Property(action => action.Name).HasMaxLength(200).IsRequired();
        builder.Property(action => action.Type).HasConversion<int>().IsRequired();
        builder.Property(action => action.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(action => action.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.HasOne(action => action.Item).WithMany(item => item.Actions)
            .HasForeignKey(action => action.ItemId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(action => action.ItemId);
        builder.Property(action => action.ItemId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}
