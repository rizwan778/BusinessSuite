using BusinessSuite.Domain.Catalog;
using BusinessSuite.Domain.Common;
using BusinessSuite.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BusinessSuite.Infrastructure.Persistence.Configurations;

/// <summary>
/// The tenant-isolation mapping every organization-owned table shares: a key <c>(OrganizationId, Id)</c> that other tables'
/// composite foreign keys point at, so the DATABASE refuses a reference from one organization's row to another's.
/// </summary>
internal static class TenantMapping
{
    /// <summary>Maps the table, the <c>(OrganizationId, Id)</c> key and the foreign key to the organization.</summary>
    public static EntityTypeBuilder<TEntity> Configure<TEntity>(
        EntityTypeBuilder<TEntity> builder, string table, string schema, Action<Microsoft.EntityFrameworkCore.Metadata.Builders.TableBuilder<TEntity>>? extra = null)
        where TEntity : TenantEntity
    {
        builder.ToTable(table, schema, tableBuilder => extra?.Invoke(tableBuilder));

        builder.HasAlternateKey(entity => new { entity.OrganizationId, entity.Id });
        builder.HasOne<Organization>().WithMany().HasForeignKey(entity => entity.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        return builder;
    }
}

/// <summary>Maps <see cref="Branch"/> to <c>platform.Branches</c>.</summary>
internal sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        TenantMapping.Configure(builder, "Branches", DatabaseSchemas.Platform);

        builder.Property(branch => branch.Code).HasMaxLength(Branch.CodeMaxLength).IsRequired();
        builder.Property(branch => branch.Name).HasMaxLength(Branch.NameMaxLength).IsRequired();

        builder.HasIndex(branch => new { branch.OrganizationId, branch.Code }).IsUnique();
        builder.HasIndex(branch => new { branch.OrganizationId, branch.Name }).IsUnique();
    }
}

/// <summary>Maps <see cref="ProductCategory"/> to <c>catalog.ProductCategories</c>.</summary>
internal sealed class ProductCategoryConfiguration : IEntityTypeConfiguration<ProductCategory>
{
    public void Configure(EntityTypeBuilder<ProductCategory> builder)
    {
        TenantMapping.Configure(builder, "ProductCategories", DatabaseSchemas.Catalog);

        builder.Property(category => category.Code).HasMaxLength(ProductCategory.CodeMaxLength);
        builder.Property(category => category.Name).HasMaxLength(ProductCategory.NameMaxLength).IsRequired();
        builder.Property(category => category.Description).HasMaxLength(ProductCategory.DescriptionMaxLength);

        // A name once per parent. HasFilter(null): EF Core would otherwise skip rows with a NULL parent, but top-level names must be
        // unique too (SQL Server compares NULLs as equal in an unfiltered unique index).
        builder.HasIndex(category => new { category.OrganizationId, category.ParentCategoryId, category.Name }).IsUnique().HasFilter(null);
        builder.HasIndex(category => new { category.OrganizationId, category.Code }).IsUnique().HasFilter("[Code] IS NOT NULL");

        builder.HasOne<ProductCategory>().WithMany()
            .HasForeignKey(category => new { category.OrganizationId, category.ParentCategoryId })
            .HasPrincipalKey(parent => new { parent.OrganizationId, parent.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps <see cref="UnitOfMeasure"/> to <c>catalog.UnitsOfMeasure</c>.</summary>
internal sealed class UnitOfMeasureConfiguration : IEntityTypeConfiguration<UnitOfMeasure>
{
    public void Configure(EntityTypeBuilder<UnitOfMeasure> builder)
    {
        TenantMapping.Configure(builder, "UnitsOfMeasure", DatabaseSchemas.Catalog, table =>
            table.HasCheckConstraint("CK_UnitsOfMeasure_DecimalPlaces", $"[DecimalPlaces] BETWEEN 0 AND {UnitOfMeasure.MaxDecimalPlaces}"));

        builder.Property(unit => unit.Code).HasMaxLength(UnitOfMeasure.CodeMaxLength).IsRequired();
        builder.Property(unit => unit.Name).HasMaxLength(UnitOfMeasure.NameMaxLength).IsRequired();

        builder.HasIndex(unit => new { unit.OrganizationId, unit.Code }).IsUnique();
        builder.HasIndex(unit => new { unit.OrganizationId, unit.Name }).IsUnique();
    }
}

/// <summary>Maps <see cref="UnitConversion"/> to <c>catalog.UnitConversions</c>.</summary>
internal sealed class UnitConversionConfiguration : IEntityTypeConfiguration<UnitConversion>
{
    public void Configure(EntityTypeBuilder<UnitConversion> builder)
    {
        TenantMapping.Configure(builder, "UnitConversions", DatabaseSchemas.Catalog, table =>
        {
            table.HasCheckConstraint("CK_UnitConversions_Factor", "[Factor] > 0");
            table.HasCheckConstraint("CK_UnitConversions_DifferentUnits", "[FromUnitId] <> [ToUnitId]");
        });

        builder.Property(conversion => conversion.Factor).HasPrecision(18, UnitConversion.FactorScale);

        // One factor per product (or general) and direction.
        builder.HasIndex(conversion => new { conversion.OrganizationId, conversion.ProductId, conversion.FromUnitId, conversion.ToUnitId }).IsUnique().HasFilter(null);

        builder.HasOne<UnitOfMeasure>().WithMany()
            .HasForeignKey(conversion => new { conversion.OrganizationId, conversion.FromUnitId })
            .HasPrincipalKey(unit => new { unit.OrganizationId, unit.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UnitOfMeasure>().WithMany()
            .HasForeignKey(conversion => new { conversion.OrganizationId, conversion.ToUnitId })
            .HasPrincipalKey(unit => new { unit.OrganizationId, unit.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Product>().WithMany()
            .HasForeignKey(conversion => new { conversion.OrganizationId, conversion.ProductId })
            .HasPrincipalKey(product => new { product.OrganizationId, product.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps <see cref="Product"/> to <c>catalog.Products</c>.</summary>
internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        TenantMapping.Configure(builder, "Products", DatabaseSchemas.Catalog, table =>
        {
            table.HasCheckConstraint("CK_Products_Amounts",
                "([ReorderLevel] IS NULL OR [ReorderLevel] >= 0) AND ([CostPrice] IS NULL OR [CostPrice] >= 0) AND ([SalePrice] IS NULL OR [SalePrice] >= 0)");
            table.HasCheckConstraint("CK_Products_BatchTracking",
                "([TracksBatches] = 0 OR [TracksStock] = 1) AND ([RequiresExpiry] = 0 OR [TracksBatches] = 1)");
        });

        builder.Property(product => product.Sku).HasMaxLength(Product.SkuMaxLength).IsRequired();
        builder.Property(product => product.Barcode).HasMaxLength(Product.BarcodeMaxLength);
        builder.Property(product => product.Name).HasMaxLength(Product.NameMaxLength).IsRequired();
        builder.Property(product => product.Description).HasMaxLength(Product.DescriptionMaxLength);

        // The guarantees behind the service's duplicate checks and their concurrent-create races.
        builder.HasIndex(product => new { product.OrganizationId, product.Sku }).IsUnique();
        builder.HasIndex(product => new { product.OrganizationId, product.Barcode }).IsUnique().HasFilter("[Barcode] IS NOT NULL");
        builder.HasIndex(product => new { product.OrganizationId, product.Name });
        builder.HasIndex(product => new { product.OrganizationId, product.CategoryId });

        builder.HasOne<ProductCategory>().WithMany()
            .HasForeignKey(product => new { product.OrganizationId, product.CategoryId })
            .HasPrincipalKey(category => new { category.OrganizationId, category.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UnitOfMeasure>().WithMany()
            .HasForeignKey(product => new { product.OrganizationId, product.BaseUnitId })
            .HasPrincipalKey(unit => new { unit.OrganizationId, unit.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps <see cref="ProductVariant"/> to <c>catalog.ProductVariants</c>.</summary>
internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        TenantMapping.Configure(builder, "ProductVariants", DatabaseSchemas.Catalog, table =>
            table.HasCheckConstraint("CK_ProductVariants_Amounts",
                "([ReorderLevel] IS NULL OR [ReorderLevel] >= 0) AND ([CostPrice] IS NULL OR [CostPrice] >= 0) AND ([SalePrice] IS NULL OR [SalePrice] >= 0)"));

        // Lets stock rows prove that a variant belongs to THEIR product.
        builder.HasAlternateKey(variant => new { variant.OrganizationId, variant.ProductId, variant.Id });

        builder.Property(variant => variant.Sku).HasMaxLength(Product.SkuMaxLength).IsRequired();
        builder.Property(variant => variant.Barcode).HasMaxLength(Product.BarcodeMaxLength);
        builder.Property(variant => variant.Name).HasMaxLength(ProductVariant.NameMaxLength);

        builder.HasIndex(variant => new { variant.OrganizationId, variant.Sku }).IsUnique();
        builder.HasIndex(variant => new { variant.OrganizationId, variant.Barcode }).IsUnique().HasFilter("[Barcode] IS NOT NULL");
        builder.HasIndex(variant => new { variant.OrganizationId, variant.ProductId });

        builder.HasOne<Product>().WithMany()
            .HasForeignKey(variant => new { variant.OrganizationId, variant.ProductId })
            .HasPrincipalKey(product => new { product.OrganizationId, product.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps <see cref="ProductAttributeDefinition"/> to <c>catalog.ProductAttributeDefinitions</c>.</summary>
internal sealed class ProductAttributeDefinitionConfiguration : IEntityTypeConfiguration<ProductAttributeDefinition>
{
    public void Configure(EntityTypeBuilder<ProductAttributeDefinition> builder)
    {
        TenantMapping.Configure(builder, "ProductAttributeDefinitions", DatabaseSchemas.Catalog, table =>
        {
            table.HasCheckConstraint("CK_ProductAttributeDefinitions_DataType", "[DataType] BETWEEN 1 AND 7");
            table.HasCheckConstraint("CK_ProductAttributeDefinitions_Scope", "[Scope] BETWEEN 1 AND 2");
        });

        builder.Property(definition => definition.Code).HasMaxLength(ProductAttributeDefinition.CodeMaxLength).IsRequired();
        builder.Property(definition => definition.Name).HasMaxLength(ProductAttributeDefinition.NameMaxLength).IsRequired();
        builder.Property(definition => definition.UnitLabel).HasMaxLength(ProductAttributeDefinition.UnitLabelMaxLength);
        builder.Property(definition => definition.DataType).HasConversion<int>();
        builder.Property(definition => definition.Scope).HasConversion<int>();
        builder.Ignore(definition => definition.UsesOptions);
        builder.Ignore(definition => definition.AllowsSeveralValues);

        builder.HasIndex(definition => new { definition.OrganizationId, definition.Code }).IsUnique();
        builder.HasIndex(definition => new { definition.OrganizationId, definition.Scope, definition.SortOrder });

        builder.HasOne<ProductCategory>().WithMany()
            .HasForeignKey(definition => new { definition.OrganizationId, definition.CategoryId })
            .HasPrincipalKey(category => new { category.OrganizationId, category.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps <see cref="ProductAttributeOption"/> to <c>catalog.ProductAttributeOptions</c>.</summary>
internal sealed class ProductAttributeOptionConfiguration : IEntityTypeConfiguration<ProductAttributeOption>
{
    public void Configure(EntityTypeBuilder<ProductAttributeOption> builder)
    {
        TenantMapping.Configure(builder, "ProductAttributeOptions", DatabaseSchemas.Catalog);

        // Lets values prove that an option belongs to THEIR attribute.
        builder.HasAlternateKey(option => new { option.OrganizationId, option.AttributeDefinitionId, option.Id });

        builder.Property(option => option.Label).HasMaxLength(ProductAttributeOption.LabelMaxLength).IsRequired();
        builder.HasIndex(option => new { option.OrganizationId, option.AttributeDefinitionId, option.Label }).IsUnique();

        builder.HasOne<ProductAttributeDefinition>().WithMany()
            .HasForeignKey(option => new { option.OrganizationId, option.AttributeDefinitionId })
            .HasPrincipalKey(definition => new { definition.OrganizationId, definition.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>What a product value and a variant value share (<see cref="AttributeValueBase"/>).</summary>
internal static class AttributeValueMapping
{
    private const string ExactlyOneValue =
        "(CASE WHEN [TextValue] IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN [NumberValue] IS NOT NULL THEN 1 ELSE 0 END"
        + " + CASE WHEN [DateValue] IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN [BooleanValue] IS NOT NULL THEN 1 ELSE 0 END"
        + " + CASE WHEN [OptionId] IS NOT NULL THEN 1 ELSE 0 END) = 1";

    public static void Configure<TValue>(EntityTypeBuilder<TValue> builder, string table, string ownerColumnCheckName)
        where TValue : AttributeValueBase
    {
        TenantMapping.Configure(builder, table, DatabaseSchemas.Catalog, tableBuilder =>
        {
            tableBuilder.HasCheckConstraint($"CK_{ownerColumnCheckName}_ExactlyOneValue", ExactlyOneValue);
            tableBuilder.HasCheckConstraint($"CK_{ownerColumnCheckName}_MultiValueHasOption", "[IsMultiValue] = 0 OR [OptionId] IS NOT NULL");
        });

        builder.Property(value => value.TextValue).HasMaxLength(AttributeValueBase.TextMaxLength);
        builder.Property(value => value.NumberValue).HasPrecision(28, 8);

        // The attribute, and an option that must belong to THAT attribute (composite keys; OptionId may be null).
        builder.HasOne<ProductAttributeDefinition>().WithMany()
            .HasForeignKey(value => new { value.OrganizationId, value.AttributeDefinitionId })
            .HasPrincipalKey(definition => new { definition.OrganizationId, definition.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductAttributeOption>().WithMany()
            .HasForeignKey(value => new { value.OrganizationId, value.AttributeDefinitionId, value.OptionId })
            .HasPrincipalKey(option => new { option.OrganizationId, option.AttributeDefinitionId, option.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps <see cref="ProductAttributeValue"/> to <c>catalog.ProductAttributeValues</c>.</summary>
internal sealed class ProductAttributeValueConfiguration : IEntityTypeConfiguration<ProductAttributeValue>
{
    public void Configure(EntityTypeBuilder<ProductAttributeValue> builder)
    {
        AttributeValueMapping.Configure(builder, "ProductAttributeValues", "ProductAttributeValues");

        // One row per (product, attribute) for single-valued types; one per chosen option for MultiSelect.
        builder.HasIndex(value => new { value.OrganizationId, value.ProductId, value.AttributeDefinitionId }).IsUnique().HasFilter("[IsMultiValue] = 0");
        builder.HasIndex(value => new { value.OrganizationId, value.ProductId, value.AttributeDefinitionId, value.OptionId }).IsUnique().HasFilter("[OptionId] IS NOT NULL");
        builder.HasIndex(value => new { value.OrganizationId, value.AttributeDefinitionId, value.OptionId }).HasFilter("[OptionId] IS NOT NULL");

        builder.HasOne<Product>().WithMany()
            .HasForeignKey(value => new { value.OrganizationId, value.ProductId })
            .HasPrincipalKey(product => new { product.OrganizationId, product.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps <see cref="ProductVariantAttributeValue"/> to <c>catalog.ProductVariantAttributeValues</c>.</summary>
internal sealed class ProductVariantAttributeValueConfiguration : IEntityTypeConfiguration<ProductVariantAttributeValue>
{
    public void Configure(EntityTypeBuilder<ProductVariantAttributeValue> builder)
    {
        AttributeValueMapping.Configure(builder, "ProductVariantAttributeValues", "ProductVariantAttributeValues");

        builder.HasIndex(value => new { value.OrganizationId, value.ProductVariantId, value.AttributeDefinitionId }).IsUnique().HasFilter("[IsMultiValue] = 0");
        builder.HasIndex(value => new { value.OrganizationId, value.ProductVariantId, value.AttributeDefinitionId, value.OptionId }).IsUnique().HasFilter("[OptionId] IS NOT NULL");
        builder.HasIndex(value => new { value.OrganizationId, value.AttributeDefinitionId, value.OptionId }).HasFilter("[OptionId] IS NOT NULL");

        builder.HasOne<ProductVariant>().WithMany()
            .HasForeignKey(value => new { value.OrganizationId, value.ProductVariantId })
            .HasPrincipalKey(variant => new { variant.OrganizationId, variant.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
