using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Service.Api.Models;

namespace Service.Api.Infrastructure.Database.Configurations;

public sealed class ItemConfiguration : IEntityTypeConfiguration<ItemModel>
{
    public void Configure(EntityTypeBuilder<ItemModel> builder)
    {
        builder.ToTable("Items", table =>
        {
            table.HasCheckConstraint("CK_Items_Name_NotBlank", "\"Name\" ~ '[^[:space:]]'");
            table.HasCheckConstraint("CK_Items_Status", "\"Status\" IN (0, 1)");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.Name).HasMaxLength(200).IsRequired();
        builder.Property(item => item.Status).HasConversion<int>().IsRequired();
        builder.Property(item => item.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(item => item.UpdatedAt).HasColumnType("timestamp with time zone");
    }
}
