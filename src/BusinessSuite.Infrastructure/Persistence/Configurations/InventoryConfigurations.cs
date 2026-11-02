using BusinessSuite.Domain.Catalog;
using BusinessSuite.Domain.Common;
using BusinessSuite.Domain.Inventory;
using BusinessSuite.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BusinessSuite.Infrastructure.Persistence.Configurations;

/// <summary>
/// The references stock tables share: warehouse, product, and (optionally) a variant OF THAT PRODUCT and a batch OF THAT
/// PRODUCT, all inside the same organization. Composite keys make the database refuse anything else.
/// </summary>
internal static class StockReferenceMapping
{
    public static void Configure<TEntity>(
        EntityTypeBuilder<TEntity> builder,
        System.Linq.Expressions.Expression<Func<TEntity, object?>> warehouse,
        System.Linq.Expressions.Expression<Func<TEntity, object?>> product,
        System.Linq.Expressions.Expression<Func<TEntity, object?>>? variant,
        System.Linq.Expressions.Expression<Func<TEntity, object?>>? batch)
        where TEntity : TenantEntity
    {
        if (warehouse is not null)
        {
            builder.HasOne<Warehouse>().WithMany().HasForeignKey(warehouse)
                .HasPrincipalKey(target => new { target.OrganizationId, target.Id }).OnDelete(DeleteBehavior.Restrict);
        }

        builder.HasOne<Product>().WithMany().HasForeignKey(product)
            .HasPrincipalKey(target => new { target.OrganizationId, target.Id }).OnDelete(DeleteBehavior.Restrict);

        if (variant is not null)
        {
            builder.HasOne<ProductVariant>().WithMany().HasForeignKey(variant)
                .HasPrincipalKey(target => new { target.OrganizationId, target.ProductId, target.Id }).OnDelete(DeleteBehavior.Restrict);
        }

        if (batch is not null)
        {
            builder.HasOne<ProductBatch>().WithMany().HasForeignKey(batch)
                .HasPrincipalKey(target => new { target.OrganizationId, target.ProductId, target.Id }).OnDelete(DeleteBehavior.Restrict);
        }
    }
}

/// <summary>Maps <see cref="Warehouse"/> to <c>inventory.Warehouses</c>.</summary>
internal sealed class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> builder)
    {
        TenantMapping.Configure(builder, "Warehouses", DatabaseSchemas.Inventory);

        builder.Property(warehouse => warehouse.Code).HasMaxLength(Warehouse.CodeMaxLength).IsRequired();
        builder.Property(warehouse => warehouse.Name).HasMaxLength(Warehouse.NameMaxLength).IsRequired();
        builder.Property(warehouse => warehouse.Address).HasMaxLength(Warehouse.AddressMaxLength);

        builder.HasIndex(warehouse => new { warehouse.OrganizationId, warehouse.Code }).IsUnique();
        builder.HasIndex(warehouse => new { warehouse.OrganizationId, warehouse.Name }).IsUnique();
        builder.HasIndex(warehouse => new { warehouse.OrganizationId, warehouse.BranchId });
        // At most one default warehouse per organization.
        builder.HasIndex(warehouse => warehouse.OrganizationId).IsUnique().HasFilter("[IsDefault] = 1").HasDatabaseName("IX_Warehouses_OrganizationId_Default");

        builder.HasOne<Branch>().WithMany()
            .HasForeignKey(warehouse => new { warehouse.OrganizationId, warehouse.BranchId })
            .HasPrincipalKey(branch => new { branch.OrganizationId, branch.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps <see cref="ProductBatch"/> to <c>inventory.ProductBatches</c>.</summary>
internal sealed class ProductBatchConfiguration : IEntityTypeConfiguration<ProductBatch>
{
    public void Configure(EntityTypeBuilder<ProductBatch> builder)
    {
        TenantMapping.Configure(builder, "ProductBatches", DatabaseSchemas.Inventory, table =>
            table.HasCheckConstraint("CK_ProductBatches_Dates", "[ManufactureDate] IS NULL OR [ExpiryDate] IS NULL OR [ExpiryDate] >= [ManufactureDate]"));

        // Lets stock rows prove that a batch belongs to THEIR product.
        builder.HasAlternateKey(batch => new { batch.OrganizationId, batch.ProductId, batch.Id });

        builder.Property(batch => batch.BatchNumber).HasMaxLength(ProductBatch.BatchNumberMaxLength).IsRequired();

        builder.HasIndex(batch => new { batch.OrganizationId, batch.ProductId, batch.ProductVariantId, batch.BatchNumber }).IsUnique().HasFilter(null);
        builder.HasIndex(batch => new { batch.OrganizationId, batch.ExpiryDate }).HasFilter("[ExpiryDate] IS NOT NULL");

        builder.HasOne<Product>().WithMany()
            .HasForeignKey(batch => new { batch.OrganizationId, batch.ProductId })
            .HasPrincipalKey(product => new { product.OrganizationId, product.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductVariant>().WithMany()
            .HasForeignKey(batch => new { batch.OrganizationId, batch.ProductId, batch.ProductVariantId })
            .HasPrincipalKey(variant => new { variant.OrganizationId, variant.ProductId, variant.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps <see cref="InventoryBalance"/> to <c>inventory.InventoryBalances</c>.</summary>
internal sealed class InventoryBalanceConfiguration : IEntityTypeConfiguration<InventoryBalance>
{
    public void Configure(EntityTypeBuilder<InventoryBalance> builder)
    {
        // Negative stock is allowed per product, so the database cannot check the sign here (see docs/INVENTORY_DATABASE.md 3.9):
        // the domain rule and the row version below are the guarantees.
        TenantMapping.Configure(builder, "InventoryBalances", DatabaseSchemas.Inventory, table =>
            table.HasCheckConstraint("CK_InventoryBalances_Reserved", "[QuantityReserved] >= 0"));

        builder.Ignore(balance => balance.StockValue);
        builder.Ignore(balance => balance.QuantityAvailable);
        builder.Property(balance => balance.RowVersion).IsRowVersion();

        // Exactly one row per warehouse / product / variant / batch (NULL variant and batch compare equal in this index).
        // HasFilter(null) keeps it that way: by default EF Core filters out rows with a NULL column, which would allow duplicates.
        builder.HasIndex(balance => new { balance.OrganizationId, balance.WarehouseId, balance.ProductId, balance.ProductVariantId, balance.BatchId }).IsUnique().HasFilter(null);
        builder.HasIndex(balance => new { balance.OrganizationId, balance.ProductId });

        StockReferenceMapping.Configure(builder,
            balance => new { balance.OrganizationId, balance.WarehouseId },
            balance => new { balance.OrganizationId, balance.ProductId },
            balance => new { balance.OrganizationId, balance.ProductId, balance.ProductVariantId },
            balance => new { balance.OrganizationId, balance.ProductId, balance.BatchId });
    }
}

/// <summary>Maps <see cref="StockMovement"/> to <c>inventory.StockMovements</c> (append-only; the migration adds the trigger).</summary>
internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        TenantMapping.Configure(builder, "StockMovements", DatabaseSchemas.Inventory, table =>
        {
            table.HasCheckConstraint("CK_StockMovements_Quantity", "([Type] IN (1,2,4,7,8) AND [Quantity] > 0) OR ([Type] IN (3,5,6,9) AND [Quantity] < 0)");
            table.HasCheckConstraint("CK_StockMovements_EnteredQuantity", "([EnteredUnitId] IS NULL AND [EnteredQuantity] IS NULL) OR ([EnteredUnitId] IS NOT NULL AND [EnteredQuantity] IS NOT NULL)");
            // The trigger TR_StockMovements_AppendOnly is created by the migration (EF Core cannot model a trigger's body).
            table.HasTrigger("TR_StockMovements_AppendOnly");
        });

        builder.Property(movement => movement.Type).HasConversion<int>();
        builder.Property(movement => movement.Reference).HasMaxLength(StockMovement.ReferenceMaxLength);
        builder.Property(movement => movement.Notes).HasMaxLength(StockMovement.NotesMaxLength);
        builder.Property(movement => movement.SourceType).HasMaxLength(StockMovement.SourceTypeMaxLength).IsRequired();

        builder.HasIndex(movement => new { movement.OrganizationId, movement.ProductId, movement.OccurredAtUtc });
        builder.HasIndex(movement => new { movement.OrganizationId, movement.WarehouseId, movement.OccurredAtUtc });
        builder.HasIndex(movement => new { movement.OrganizationId, movement.SourceType, movement.SourceId }).HasFilter("[SourceId] IS NOT NULL");
        // One source line posts a given movement once: the same document line (SourceType + SourceId + SourceLineId) cannot hit the
        // ledger twice for the same type, warehouse, product, variant and batch, whatever the application does. This is what makes a
        // retried or repeated posting by Sales/Purchases (or a bug) safe: the second attempt fails here instead of doubling the stock.
        // Explicit filter, so rows without a source line (manual movements) are left out and NULL variant/batch compare equal.
        builder.HasIndex(movement => new
            {
                movement.OrganizationId, movement.SourceType, movement.SourceId, movement.SourceLineId, movement.Type,
                movement.WarehouseId, movement.ProductId, movement.ProductVariantId, movement.BatchId,
            })
            .IsUnique()
            .HasFilter("[SourceId] IS NOT NULL AND [SourceLineId] IS NOT NULL")
            .HasDatabaseName("IX_StockMovements_SourceLine");
        // A movement is reversed at most once.
        builder.HasIndex(movement => new { movement.OrganizationId, movement.ReversesMovementId }).IsUnique().HasFilter("[ReversesMovementId] IS NOT NULL");

        StockReferenceMapping.Configure(builder,
            movement => new { movement.OrganizationId, movement.WarehouseId },
            movement => new { movement.OrganizationId, movement.ProductId },
            movement => new { movement.OrganizationId, movement.ProductId, movement.ProductVariantId },
            movement => new { movement.OrganizationId, movement.ProductId, movement.BatchId });

        builder.HasOne<UnitOfMeasure>().WithMany()
            .HasForeignKey(movement => new { movement.OrganizationId, movement.EnteredUnitId })
            .HasPrincipalKey(unit => new { unit.OrganizationId, unit.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StockMovement>().WithMany()
            .HasForeignKey(movement => new { movement.OrganizationId, movement.ReversesMovementId })
            .HasPrincipalKey(original => new { original.OrganizationId, original.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps <see cref="StockAdjustment"/> to <c>inventory.StockAdjustments</c>.</summary>
internal sealed class StockAdjustmentConfiguration : IEntityTypeConfiguration<StockAdjustment>
{
    public void Configure(EntityTypeBuilder<StockAdjustment> builder)
    {
        TenantMapping.Configure(builder, "StockAdjustments", DatabaseSchemas.Inventory, table =>
        {
            table.HasCheckConstraint("CK_StockAdjustments_Status", "[Status] BETWEEN 1 AND 3");
            // TR_StockAdjustments_NoDeletePosted (created by the migration) refuses to delete a document that is not a draft.
            table.HasTrigger("TR_StockAdjustments_NoDeletePosted");
            // TR_StockAdjustments_Immutable refuses any change to a document that is already posted or cancelled.
            table.HasTrigger("TR_StockAdjustments_Immutable");
        });

        builder.Property(adjustment => adjustment.Number).HasMaxLength(StockAdjustment.NumberMaxLength).IsRequired();
        builder.Property(adjustment => adjustment.Notes).HasMaxLength(StockAdjustment.NotesMaxLength);
        builder.Property(adjustment => adjustment.Status).HasConversion<int>();
        builder.Property(adjustment => adjustment.Reason).HasConversion<int>();
        builder.Property(adjustment => adjustment.RowVersion).IsRowVersion();

        builder.HasIndex(adjustment => new { adjustment.OrganizationId, adjustment.Number }).IsUnique();
        builder.HasIndex(adjustment => new { adjustment.OrganizationId, adjustment.WarehouseId, adjustment.AdjustmentDate });

        builder.HasOne<Warehouse>().WithMany()
            .HasForeignKey(adjustment => new { adjustment.OrganizationId, adjustment.WarehouseId })
            .HasPrincipalKey(warehouse => new { warehouse.OrganizationId, warehouse.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Lines live and die with a DRAFT; a posted document cannot be deleted (trigger), so neither can its lines.
        builder.HasMany(adjustment => adjustment.Items).WithOne()
            .HasForeignKey(item => new { item.OrganizationId, item.StockAdjustmentId })
            .HasPrincipalKey(adjustment => new { adjustment.OrganizationId, adjustment.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(adjustment => adjustment.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Maps <see cref="StockAdjustmentItem"/> to <c>inventory.StockAdjustmentItems</c>.</summary>
internal sealed class StockAdjustmentItemConfiguration : IEntityTypeConfiguration<StockAdjustmentItem>
{
    public void Configure(EntityTypeBuilder<StockAdjustmentItem> builder)
    {
        TenantMapping.Configure(builder, "StockAdjustmentItems", DatabaseSchemas.Inventory, table =>
        {
            table.HasCheckConstraint("CK_StockAdjustmentItems_Quantity", "[QuantityDifference] <> 0");
            // TR_StockAdjustmentItems_Immutable: lines of a posted document cannot be added, changed or removed (the movement link is set once).
            table.HasTrigger("TR_StockAdjustmentItems_Immutable");
        });

        builder.Property(item => item.Notes).HasMaxLength(StockMovement.NotesMaxLength);

        builder.HasIndex(item => new { item.OrganizationId, item.StockAdjustmentId, item.ProductId, item.ProductVariantId, item.BatchId }).IsUnique().HasFilter(null);
        builder.HasIndex(item => new { item.OrganizationId, item.ProductId });

        StockReferenceMapping.Configure(builder,
            null!,
            item => new { item.OrganizationId, item.ProductId },
            item => new { item.OrganizationId, item.ProductId, item.ProductVariantId },
            item => new { item.OrganizationId, item.ProductId, item.BatchId });

        builder.HasOne<StockMovement>().WithMany()
            .HasForeignKey(item => new { item.OrganizationId, item.StockMovementId })
            .HasPrincipalKey(movement => new { movement.OrganizationId, movement.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Maps <see cref="StockTransfer"/> to <c>inventory.StockTransfers</c>.</summary>
internal sealed class StockTransferConfiguration : IEntityTypeConfiguration<StockTransfer>
{
    public void Configure(EntityTypeBuilder<StockTransfer> builder)
    {
        TenantMapping.Configure(builder, "StockTransfers", DatabaseSchemas.Inventory, table =>
        {
            table.HasCheckConstraint("CK_StockTransfers_Status", "[Status] BETWEEN 1 AND 4");
            table.HasCheckConstraint("CK_StockTransfers_DifferentWarehouses", "[FromWarehouseId] <> [ToWarehouseId]");
            // TR_StockTransfers_NoDeletePosted (created by the migration) refuses to delete a document that is not a draft.
            table.HasTrigger("TR_StockTransfers_NoDeletePosted");
            // TR_StockTransfers_Immutable refuses any change to a transfer that is already received or cancelled.
            table.HasTrigger("TR_StockTransfers_Immutable");
        });

        builder.Property(transfer => transfer.Number).HasMaxLength(StockAdjustment.NumberMaxLength).IsRequired();
        builder.Property(transfer => transfer.Notes).HasMaxLength(StockAdjustment.NotesMaxLength);
        builder.Property(transfer => transfer.Status).HasConversion<int>();
        builder.Property(transfer => transfer.RowVersion).IsRowVersion();

        builder.HasIndex(transfer => new { transfer.OrganizationId, transfer.Number }).IsUnique();
        builder.HasIndex(transfer => new { transfer.OrganizationId, transfer.FromWarehouseId, transfer.Status });
        builder.HasIndex(transfer => new { transfer.OrganizationId, transfer.ToWarehouseId, transfer.Status });

        builder.HasOne<Warehouse>().WithMany()
            .HasForeignKey(transfer => new { transfer.OrganizationId, transfer.FromWarehouseId })
            .HasPrincipalKey(warehouse => new { warehouse.OrganizationId, warehouse.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Warehouse>().WithMany()
            .HasForeignKey(transfer => new { transfer.OrganizationId, transfer.ToWarehouseId })
            .HasPrincipalKey(warehouse => new { warehouse.OrganizationId, warehouse.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(transfer => transfer.Items).WithOne()
            .HasForeignKey(item => new { item.OrganizationId, item.StockTransferId })
            .HasPrincipalKey(transfer => new { transfer.OrganizationId, transfer.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(transfer => transfer.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Maps <see cref="StockTransferItem"/> to <c>inventory.StockTransferItems</c>.</summary>
internal sealed class StockTransferItemConfiguration : IEntityTypeConfiguration<StockTransferItem>
{
    public void Configure(EntityTypeBuilder<StockTransferItem> builder)
    {
        TenantMapping.Configure(builder, "StockTransferItems", DatabaseSchemas.Inventory, table =>
        {
            table.HasCheckConstraint("CK_StockTransferItems_Quantity", "[Quantity] > 0 AND ([ReceivedQuantity] IS NULL OR [ReceivedQuantity] > 0)");
            // TR_StockTransferItems_Immutable: lines of a posted transfer cannot be added, changed or removed (the movement links, cost and received quantity are set once).
            table.HasTrigger("TR_StockTransferItems_Immutable");
        });

        builder.HasIndex(item => new { item.OrganizationId, item.StockTransferId, item.ProductId, item.ProductVariantId, item.BatchId }).IsUnique().HasFilter(null);
        builder.HasIndex(item => new { item.OrganizationId, item.ProductId });

        StockReferenceMapping.Configure(builder,
            null!,
            item => new { item.OrganizationId, item.ProductId },
            item => new { item.OrganizationId, item.ProductId, item.ProductVariantId },
            item => new { item.OrganizationId, item.ProductId, item.BatchId });

        builder.HasOne<StockMovement>().WithMany()
            .HasForeignKey(item => new { item.OrganizationId, item.DispatchMovementId })
            .HasPrincipalKey(movement => new { movement.OrganizationId, movement.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StockMovement>().WithMany()
            .HasForeignKey(item => new { item.OrganizationId, item.ReceiptMovementId })
            .HasPrincipalKey(movement => new { movement.OrganizationId, movement.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
