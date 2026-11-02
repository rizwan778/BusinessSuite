using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Audit;
using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Domain.Catalog;
using BusinessSuite.Domain.Common;
using BusinessSuite.Infrastructure.Auditing;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Catalog;

/// <summary>
/// <see cref="IProductService"/> on SQL Server.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Tenant isolation:</b> every query is filtered by <c>ICurrentUser.OrganizationId</c>; the category, unit and
/// attributes a product refers to must belong to the same organization (also enforced by composite foreign keys).</item>
/// <item><b>Dynamic attributes</b> are validated against the organization's schema by <see cref="AttributeValueValidator"/>
/// before anything is written. A product (or variant) and its attribute values are saved in ONE transaction.</item>
/// <item>Expected failures are <see cref="Result"/> values; unique-index races become conflicts; anything else is unexpected and
/// left to the API exception handler (logged, 500).</item>
/// <item>Logs contain user, organization and record ids only, never names, SKUs or barcodes.</item>
/// </list>
/// </remarks>
internal sealed class ProductService(AppDbContext db, ICurrentUser currentUser, AuditTrail audit, ILogger<ProductService> logger) : IProductService
{
    // ---- Products: reads ----------------------------------------------------------------------------------

    public async Task<Result<PagedResult<ProductSummary>>> ListAsync(ProductListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var products = db.Products.AsNoTracking().Where(product => product.OrganizationId == organizationId);

        if (DatabaseErrors.Clean(query.Search) is { } search)
        {
            products = products.Where(product =>
                product.Name.Contains(search) || product.Sku.Contains(search) || (product.Barcode != null && product.Barcode.Contains(search))
                || db.ProductVariants.Any(variant => variant.ProductId == product.Id
                    && (variant.Sku.Contains(search) || (variant.Barcode != null && variant.Barcode.Contains(search)) || (variant.Name != null && variant.Name.Contains(search)))));
        }

        if (query.CategoryId is { } categoryId)
        {
            var categoryIds = query.IncludeSubcategories
                ? await CategoryTree.SelfAndDescendantsAsync(db, organizationId, categoryId, cancellationToken)
                : [categoryId];
            products = products.Where(product => product.CategoryId != null && categoryIds.Contains(product.CategoryId.Value));
        }

        if (query.IsActive is { } isActive)
        {
            products = products.Where(product => product.IsActive == isActive);
        }

        if (query.HasVariants is { } hasVariants)
        {
            products = products.Where(product => product.HasVariants == hasVariants);
        }

        if (query.TracksStock is { } tracksStock)
        {
            products = products.Where(product => product.TracksStock == tracksStock);
        }

        if (query.OptionId is { } optionId)
        {
            products = products.Where(product =>
                db.ProductAttributeValues.Any(value => value.ProductId == product.Id && value.OptionId == optionId)
                || db.ProductVariantAttributeValues.Any(value => value.OptionId == optionId
                    && db.ProductVariants.Any(variant => variant.Id == value.ProductVariantId && variant.ProductId == product.Id)));
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await products.CountAsync(cancellationToken);
        var items = await (
                from product in products.OrderBy(product => product.Name).ThenBy(product => product.Id).Skip(page.Skip).Take(page.PageSize)
                join unit in db.UnitsOfMeasure on product.BaseUnitId equals unit.Id
                select new
                {
                    Product = product,
                    CategoryName = db.ProductCategories.Where(category => category.Id == product.CategoryId).Select(category => category.Name).FirstOrDefault(),
                    UnitCode = unit.Code,
                    VariantCount = db.ProductVariants.Count(variant => variant.ProductId == product.Id),
                    OnHand = db.InventoryBalances.Where(balance => balance.ProductId == product.Id).Sum(balance => (decimal?)balance.QuantityOnHand) ?? 0,
                    Reserved = db.InventoryBalances.Where(balance => balance.ProductId == product.Id).Sum(balance => (decimal?)balance.QuantityReserved) ?? 0,
                })
            .ToListAsync(cancellationToken);

        var summaries = items.ConvertAll(row => new ProductSummary(
            row.Product.Id, row.Product.Sku, row.Product.Barcode, row.Product.Name, row.Product.CategoryId, row.CategoryName, row.UnitCode,
            row.Product.HasVariants, row.VariantCount, row.Product.TracksStock, row.Product.IsActive, row.Product.SalePrice,
            row.OnHand, row.OnHand - row.Reserved, row.Product.CreatedAtUtc));
        return new PagedResult<ProductSummary>(summaries, page, totalCount);
    }

    public async Task<Result<ProductDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var details = await LoadProductAsync(id, acting.Value, cancellationToken);
        if (details is null)
        {
            // Unknown, or another organization's: the caller cannot tell which. Worth a trace when probing ids.
            logger.LogInformation("User {ActorId} asked for product {ProductId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, id, acting.Value);
            return CatalogErrors.ProductNotFound;
        }

        return details;
    }

    // ---- Products: changes --------------------------------------------------------------------------------

    public async Task<Result<ProductDetails>> CreateAsync(SaveProductCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        Guid productId = Guid.Empty;
        Result outcome;
        try
        {
            outcome = await DatabaseTransactions.RunAsync<Result>(db, async () =>
            {
                if (await CheckReferencesAsync(organizationId, command, existing: null, cancellationToken) is { } badReference)
                {
                    return badReference;
                }

                Product product;
                try
                {
                    product = Product.Create(organizationId, ToSpec(command));
                }
                catch (DomainException exception)
                {
                    return CatalogErrors.ProductInvalid(exception.Message);
                }

                if (await DuplicateAsync(organizationId, product.Sku, product.Barcode, ignoreProductId: null, ignoreVariantId: null, cancellationToken) is { } duplicate)
                {
                    return duplicate;
                }

                var values = await BuildValuesAsync(organizationId, AttributeScope.Product, product.CategoryId, command.Attributes, cancellationToken);
                if (values.Error is { } invalid)
                {
                    return invalid;
                }

                db.Products.Add(product);
                productId = product.Id;
                foreach (var (definition, input) in values.Values!)
                {
                    db.ProductAttributeValues.Add(ProductAttributeValue.Create(product.Id, definition, input));
                }

                audit.Record(AuditActions.ProductCreated, AuditActions.EntityTypes.Product, product.Id, organizationId,
                    new { sku = product.Sku, name = product.Name, hasVariants = product.HasVariants, tracksStock = product.TracksStock, tracksBatches = product.TracksBatches, baseUnitId = product.BaseUnitId, categoryId = product.CategoryId });
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }, cancellationToken);
        }
        catch (Exception exception) when (DatabaseErrors.IsRace(exception))
        {
            logger.LogWarning("Creation of a product by user {ActorId} in organization {OrganizationId} lost a race with a concurrent request.", currentUser.UserId, organizationId);
            return CatalogErrors.ProductDuplicateSku;
        }

        if (outcome.IsFailure)
        {
            logger.LogInformation("User {ActorId} could not create a product in organization {OrganizationId} ({Code}).", currentUser.UserId, organizationId, outcome.Error.Code);
            return outcome.Error;
        }

        logger.LogInformation("User {ActorId} created product {ProductId} in organization {OrganizationId}.", currentUser.UserId, productId, organizationId);
        return (await LoadProductAsync(productId, organizationId, cancellationToken))!;
    }

    public async Task<Result<ProductDetails>> UpdateAsync(Guid id, SaveProductCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        Result outcome;
        try
        {
            outcome = await DatabaseTransactions.RunAsync<Result>(db, async () =>
            {
                var product = await db.Products.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
                if (product is null)
                {
                    return CatalogErrors.ProductNotFound;
                }

                if (await CheckReferencesAsync(organizationId, command, product, cancellationToken) is { } badReference)
                {
                    return badReference;
                }

                // Changing these once stock exists would reinterpret quantities already in the ledger.
                var structural = command.BaseUnitId != product.BaseUnitId || command.HasVariants != product.HasVariants
                    || command.TracksStock != product.TracksStock || command.TracksBatches != product.TracksBatches;
                if (structural && await HasStockHistoryAsync(organizationId, id, cancellationToken))
                {
                    return CatalogErrors.ProductHasStock;
                }

                if (product.HasVariants && !command.HasVariants && await db.ProductVariants.AnyAsync(variant => variant.ProductId == id, cancellationToken))
                {
                    return CatalogErrors.ProductHasVariants;
                }

                var before = Snapshot(product);
                try
                {
                    product.Apply(ToSpec(command));
                }
                catch (DomainException exception)
                {
                    return CatalogErrors.ProductInvalid(exception.Message);
                }

                if (await DuplicateAsync(organizationId, product.Sku, product.Barcode, ignoreProductId: id, ignoreVariantId: null, cancellationToken) is { } duplicate)
                {
                    return duplicate;
                }

                var values = await BuildValuesAsync(organizationId, AttributeScope.Product, product.CategoryId, command.Attributes, cancellationToken);
                if (values.Error is { } invalid)
                {
                    return invalid;
                }

                // The attribute values are replaced as a whole: the old ones go first so the unique indexes never see both.
                db.ProductAttributeValues.RemoveRange(await db.ProductAttributeValues.Where(value => value.ProductId == id && value.OrganizationId == organizationId).ToListAsync(cancellationToken));
                await db.SaveChangesAsync(cancellationToken);
                foreach (var (definition, input) in values.Values!)
                {
                    db.ProductAttributeValues.Add(ProductAttributeValue.Create(id, definition, input));
                }

                audit.Record(AuditActions.ProductUpdated, AuditActions.EntityTypes.Product, id, organizationId,
                    new { changes = AuditDiff.Changes(before, Snapshot(product)), attributeValueCount = values.Values.Count });
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }, cancellationToken);
        }
        catch (Exception exception) when (DatabaseErrors.IsRace(exception))
        {
            logger.LogWarning("Change of product {ProductId} by user {ActorId} lost a race with a concurrent request.", id, currentUser.UserId);
            return CatalogErrors.ConcurrentChange;
        }

        if (outcome.IsFailure)
        {
            logger.LogInformation("User {ActorId} could not change product {ProductId} in organization {OrganizationId} ({Code}).", currentUser.UserId, id, organizationId, outcome.Error.Code);
            return outcome.Error;
        }

        logger.LogInformation("User {ActorId} changed product {ProductId} in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
        return (await LoadProductAsync(id, organizationId, cancellationToken))!;
    }

    public async Task<Result<ProductDetails>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var product = await db.Products.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        if (product.IsActive != isActive)
        {
            product.SetActive(isActive);
            audit.Record(isActive ? AuditActions.ProductActivated : AuditActions.ProductDeactivated, AuditActions.EntityTypes.Product, id, organizationId,
                new { sku = product.Sku });
            if (await DatabaseErrors.TrySaveAsync(db, logger, "product", currentUser.UserId, CatalogErrors.ProductDuplicateSku, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
            {
                return error;
            }

            logger.LogInformation("User {ActorId} {Action} product {ProductId} in organization {OrganizationId}.", currentUser.UserId, isActive ? "activated" : "retired", id, organizationId);
        }

        return (await LoadProductAsync(id, organizationId, cancellationToken))!;
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        Result outcome;
        try
        {
            // The product's own descriptive rows go with it; stock, batches and documents are what the database refuses on.
            outcome = await DatabaseTransactions.RunAsync<Result>(db, async () =>
            {
                var product = await db.Products.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
                if (product is null)
                {
                    return CatalogErrors.ProductNotFound;
                }

                var variantIds = await db.ProductVariants.Where(variant => variant.ProductId == id && variant.OrganizationId == organizationId).Select(variant => variant.Id).ToListAsync(cancellationToken);
                db.ProductVariantAttributeValues.RemoveRange(await db.ProductVariantAttributeValues.Where(value => variantIds.Contains(value.ProductVariantId)).ToListAsync(cancellationToken));
                db.ProductAttributeValues.RemoveRange(await db.ProductAttributeValues.Where(value => value.ProductId == id).ToListAsync(cancellationToken));
                db.UnitConversions.RemoveRange(await db.UnitConversions.Where(conversion => conversion.ProductId == id).ToListAsync(cancellationToken));
                db.ProductVariants.RemoveRange(await db.ProductVariants.Where(variant => variant.ProductId == id).ToListAsync(cancellationToken));
                db.Products.Remove(product);
                audit.Record(AuditActions.ProductDeleted, AuditActions.EntityTypes.Product, id, organizationId, new { sku = product.Sku, name = product.Name });
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsConstraintViolation(exception))
        {
            logger.LogInformation("User {ActorId} could not delete product {ProductId} in organization {OrganizationId}: stock, batches or documents refer to it.", currentUser.UserId, id, organizationId);
            return CatalogErrors.ProductInUse;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("Deletion of product {ProductId} by user {ActorId} lost a race with a concurrent change.", id, currentUser.UserId);
            return CatalogErrors.ConcurrentChange;
        }

        if (outcome.IsFailure)
        {
            logger.LogInformation("User {ActorId} tried to delete product {ProductId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
            return outcome.Error;
        }

        logger.LogInformation("User {ActorId} deleted product {ProductId} of organization {OrganizationId}.", currentUser.UserId, id, organizationId);
        return Result.Success();
    }

    // ---- Variants -----------------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<VariantDetails>>> ListVariantsAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        if (!await db.Products.AnyAsync(product => product.Id == productId && product.OrganizationId == organizationId, cancellationToken))
        {
            return CatalogErrors.ProductNotFound;
        }

        return await LoadVariantsAsync(db.ProductVariants.AsNoTracking().Where(variant => variant.ProductId == productId && variant.OrganizationId == organizationId), cancellationToken);
    }

    public async Task<Result<VariantDetails>> GetVariantAsync(Guid productId, Guid variantId, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var variant = await LoadVariantAsync(productId, variantId, acting.Value, cancellationToken);
        if (variant is null)
        {
            logger.LogInformation("User {ActorId} asked for variant {VariantId} of product {ProductId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, variantId, productId, acting.Value);
            return CatalogErrors.VariantNotFound;
        }

        return variant;
    }

    public async Task<Result<VariantDetails>> CreateVariantAsync(Guid productId, SaveVariantCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        Guid variantId = Guid.Empty;
        Result outcome;
        try
        {
            outcome = await DatabaseTransactions.RunAsync<Result>(db, async () =>
            {
                var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == productId && candidate.OrganizationId == organizationId, cancellationToken);
                if (product is null)
                {
                    return CatalogErrors.ProductNotFound;
                }

                if (!product.HasVariants)
                {
                    return CatalogErrors.VariantsNotAllowed;
                }

                ProductVariant variant;
                try
                {
                    variant = ProductVariant.Create(organizationId, productId, ToSpec(command));
                }
                catch (DomainException exception)
                {
                    return CatalogErrors.ProductInvalid(exception.Message);
                }

                if (await DuplicateAsync(organizationId, variant.Sku, variant.Barcode, ignoreProductId: null, ignoreVariantId: null, cancellationToken) is { } duplicate)
                {
                    return duplicate;
                }

                var values = await BuildValuesAsync(organizationId, AttributeScope.Variant, product.CategoryId, command.Attributes, cancellationToken);
                if (values.Error is { } invalid)
                {
                    return invalid;
                }

                db.ProductVariants.Add(variant);
                variantId = variant.Id;
                foreach (var (definition, input) in values.Values!)
                {
                    db.ProductVariantAttributeValues.Add(ProductVariantAttributeValue.Create(variant.Id, definition, input));
                }

                audit.Record(AuditActions.VariantCreated, AuditActions.EntityTypes.ProductVariant, variant.Id, organizationId,
                    new { productId, sku = variant.Sku });
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }, cancellationToken);
        }
        catch (Exception exception) when (DatabaseErrors.IsRace(exception))
        {
            logger.LogWarning("Creation of a variant of product {ProductId} by user {ActorId} lost a race with a concurrent request.", productId, currentUser.UserId);
            return CatalogErrors.ProductDuplicateSku;
        }

        if (outcome.IsFailure)
        {
            logger.LogInformation("User {ActorId} could not create a variant of product {ProductId} in organization {OrganizationId} ({Code}).", currentUser.UserId, productId, organizationId, outcome.Error.Code);
            return outcome.Error;
        }

        logger.LogInformation("User {ActorId} created variant {VariantId} of product {ProductId} in organization {OrganizationId}.", currentUser.UserId, variantId, productId, organizationId);
        return (await LoadVariantAsync(productId, variantId, organizationId, cancellationToken))!;
    }

    public async Task<Result<VariantDetails>> UpdateVariantAsync(Guid productId, Guid variantId, SaveVariantCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        Result outcome;
        try
        {
            outcome = await DatabaseTransactions.RunAsync<Result>(db, async () =>
            {
                var variant = await db.ProductVariants.SingleOrDefaultAsync(
                    candidate => candidate.Id == variantId && candidate.ProductId == productId && candidate.OrganizationId == organizationId, cancellationToken);
                if (variant is null)
                {
                    return CatalogErrors.VariantNotFound;
                }

                var categoryId = await db.Products.Where(product => product.Id == productId).Select(product => product.CategoryId).SingleAsync(cancellationToken);
                var before = Snapshot(variant);
                try
                {
                    variant.Apply(ToSpec(command));
                }
                catch (DomainException exception)
                {
                    return CatalogErrors.ProductInvalid(exception.Message);
                }

                if (await DuplicateAsync(organizationId, variant.Sku, variant.Barcode, ignoreProductId: null, ignoreVariantId: variantId, cancellationToken) is { } duplicate)
                {
                    return duplicate;
                }

                var values = await BuildValuesAsync(organizationId, AttributeScope.Variant, categoryId, command.Attributes, cancellationToken);
                if (values.Error is { } invalid)
                {
                    return invalid;
                }

                db.ProductVariantAttributeValues.RemoveRange(
                    await db.ProductVariantAttributeValues.Where(value => value.ProductVariantId == variantId && value.OrganizationId == organizationId).ToListAsync(cancellationToken));
                await db.SaveChangesAsync(cancellationToken);
                foreach (var (definition, input) in values.Values!)
                {
                    db.ProductVariantAttributeValues.Add(ProductVariantAttributeValue.Create(variantId, definition, input));
                }

                audit.Record(AuditActions.VariantUpdated, AuditActions.EntityTypes.ProductVariant, variantId, organizationId,
                    new { productId, changes = AuditDiff.Changes(before, Snapshot(variant)), attributeValueCount = values.Values.Count });
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }, cancellationToken);
        }
        catch (Exception exception) when (DatabaseErrors.IsRace(exception))
        {
            logger.LogWarning("Change of variant {VariantId} by user {ActorId} lost a race with a concurrent request.", variantId, currentUser.UserId);
            return CatalogErrors.ConcurrentChange;
        }

        if (outcome.IsFailure)
        {
            logger.LogInformation("User {ActorId} could not change variant {VariantId} of product {ProductId} in organization {OrganizationId} ({Code}).", currentUser.UserId, variantId, productId, organizationId, outcome.Error.Code);
            return outcome.Error;
        }

        logger.LogInformation("User {ActorId} changed variant {VariantId} of product {ProductId} in organization {OrganizationId}.", currentUser.UserId, variantId, productId, organizationId);
        return (await LoadVariantAsync(productId, variantId, organizationId, cancellationToken))!;
    }

    public async Task<Result<VariantDetails>> SetVariantActiveAsync(Guid productId, Guid variantId, bool isActive, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var variant = await db.ProductVariants.SingleOrDefaultAsync(
            candidate => candidate.Id == variantId && candidate.ProductId == productId && candidate.OrganizationId == organizationId, cancellationToken);
        if (variant is null)
        {
            return CatalogErrors.VariantNotFound;
        }

        if (variant.IsActive != isActive)
        {
            variant.SetActive(isActive);
            audit.Record(isActive ? AuditActions.VariantActivated : AuditActions.VariantDeactivated, AuditActions.EntityTypes.ProductVariant, variantId, organizationId,
                new { productId, sku = variant.Sku });
            if (await DatabaseErrors.TrySaveAsync(db, logger, "variant", currentUser.UserId, CatalogErrors.ProductDuplicateSku, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
            {
                return error;
            }

            logger.LogInformation("User {ActorId} {Action} variant {VariantId} in organization {OrganizationId}.", currentUser.UserId, isActive ? "activated" : "retired", variantId, organizationId);
        }

        return (await LoadVariantAsync(productId, variantId, organizationId, cancellationToken))!;
    }

    public async Task<Result> DeleteVariantAsync(Guid productId, Guid variantId, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        Result outcome;
        try
        {
            outcome = await DatabaseTransactions.RunAsync<Result>(db, async () =>
            {
                var variant = await db.ProductVariants.SingleOrDefaultAsync(
                    candidate => candidate.Id == variantId && candidate.ProductId == productId && candidate.OrganizationId == organizationId, cancellationToken);
                if (variant is null)
                {
                    return CatalogErrors.VariantNotFound;
                }

                db.ProductVariantAttributeValues.RemoveRange(await db.ProductVariantAttributeValues.Where(value => value.ProductVariantId == variantId).ToListAsync(cancellationToken));
                db.ProductVariants.Remove(variant);
                audit.Record(AuditActions.VariantDeleted, AuditActions.EntityTypes.ProductVariant, variantId, organizationId, new { productId, sku = variant.Sku });
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsConstraintViolation(exception))
        {
            logger.LogInformation("User {ActorId} could not delete variant {VariantId} in organization {OrganizationId}: stock, batches or documents refer to it.", currentUser.UserId, variantId, organizationId);
            return CatalogErrors.VariantInUse;
        }
        catch (DbUpdateConcurrencyException)
        {
            return CatalogErrors.ConcurrentChange;
        }

        if (outcome.IsSuccess)
        {
            logger.LogInformation("User {ActorId} deleted variant {VariantId} of product {ProductId} in organization {OrganizationId}.", currentUser.UserId, variantId, productId, organizationId);
        }

        return outcome;
    }

    // ---- Validation helpers -------------------------------------------------------------------------------

    /// <summary>The category, base unit must exist in THIS organization and be active; a reference the product already has is kept even if retired since.</summary>
    /// <summary>The audited fields of a product (free text such as the description is left out).</summary>
    private static Dictionary<string, object?> Snapshot(Product product) => new()
    {
        ["sku"] = product.Sku,
        ["barcode"] = product.Barcode,
        ["name"] = product.Name,
        ["categoryId"] = product.CategoryId,
        ["baseUnitId"] = product.BaseUnitId,
        ["hasVariants"] = product.HasVariants,
        ["tracksStock"] = product.TracksStock,
        ["tracksBatches"] = product.TracksBatches,
        ["requiresExpiry"] = product.RequiresExpiry,
        ["allowNegativeStock"] = product.AllowNegativeStock,
        ["reorderLevel"] = product.ReorderLevel,
        ["costPrice"] = product.CostPrice,
        ["salePrice"] = product.SalePrice,
    };

    private static Dictionary<string, object?> Snapshot(ProductVariant variant) => new()
    {
        ["sku"] = variant.Sku,
        ["barcode"] = variant.Barcode,
        ["name"] = variant.Name,
        ["reorderLevel"] = variant.ReorderLevel,
        ["costPrice"] = variant.CostPrice,
        ["salePrice"] = variant.SalePrice,
    };

    private async Task<Error?> CheckReferencesAsync(Guid organizationId, SaveProductCommand command, Product? existing, CancellationToken cancellationToken)
    {
        if (command.CategoryId is { } categoryId && categoryId != existing?.CategoryId
            && !await db.ProductCategories.AnyAsync(category => category.Id == categoryId && category.OrganizationId == organizationId && category.IsActive, cancellationToken))
        {
            return CatalogErrors.ProductReferenceInvalid("category");
        }

        var unitId = command.BaseUnitId;
        if (unitId != existing?.BaseUnitId
            && !await db.UnitsOfMeasure.AnyAsync(unit => unit.Id == unitId && unit.OrganizationId == organizationId && unit.IsActive, cancellationToken))
        {
            return CatalogErrors.ProductReferenceInvalid("unit of measure");
        }

        return null;
    }

    /// <summary>The SKU and barcode are one namespace across products and variants (a scan must identify one thing).</summary>
    private async Task<Error?> DuplicateAsync(Guid organizationId, string sku, string? barcode, Guid? ignoreProductId, Guid? ignoreVariantId, CancellationToken cancellationToken)
    {
        // The database's collation decides letter case.
        if (await db.Products.AnyAsync(other => other.OrganizationId == organizationId && other.Sku == sku && other.Id != ignoreProductId, cancellationToken)
            || await db.ProductVariants.AnyAsync(other => other.OrganizationId == organizationId && other.Sku == sku && other.Id != ignoreVariantId, cancellationToken))
        {
            return CatalogErrors.ProductDuplicateSku;
        }

        if (barcode is not null
            && (await db.Products.AnyAsync(other => other.OrganizationId == organizationId && other.Barcode == barcode && other.Id != ignoreProductId, cancellationToken)
                || await db.ProductVariants.AnyAsync(other => other.OrganizationId == organizationId && other.Barcode == barcode && other.Id != ignoreVariantId, cancellationToken)))
        {
            return CatalogErrors.ProductDuplicateBarcode;
        }

        return null;
    }

    private Task<bool> HasStockHistoryAsync(Guid organizationId, Guid productId, CancellationToken cancellationToken) =>
        db.StockMovements.AnyAsync(movement => movement.OrganizationId == organizationId && movement.ProductId == productId, cancellationToken);

    /// <summary>
    /// Validates dynamic attribute values against the organization's schema for this scope and category, and returns the
    /// definitions and inputs to store, or the validation error listing every problem.
    /// </summary>
    private async Task<(Error? Error, List<(ProductAttributeDefinition Definition, AttributeValueInput Input)>? Values)> BuildValuesAsync(
        Guid organizationId, AttributeScope scope, Guid? categoryId, IReadOnlyList<AttributeValueCommand>? commands, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<Guid> ancestry = categoryId is { } category
            ? await CategoryTree.AncestryAsync(db, organizationId, category, cancellationToken) ?? []
            : [];

        var definitions = await db.ProductAttributeDefinitions.AsNoTracking()
            .Where(definition => definition.OrganizationId == organizationId && definition.Scope == scope
                && (definition.CategoryId == null || ancestry.Contains(definition.CategoryId.Value)))
            .ToListAsync(cancellationToken);
        var definitionIds = definitions.Select(definition => definition.Id).ToList();
        var activeOptions = (await db.ProductAttributeOptions.AsNoTracking()
                .Where(option => option.OrganizationId == organizationId && option.IsActive && definitionIds.Contains(option.AttributeDefinitionId))
                .Select(option => new { option.AttributeDefinitionId, option.Id })
                .ToListAsync(cancellationToken))
            .ToLookup(option => option.AttributeDefinitionId, option => option.Id);

        var rules = definitions.ConvertAll(definition => new AttributeRule(
            definition.Id, definition.Code, definition.Name, definition.DataType, definition.IsRequired, definition.IsActive,
            activeOptions[definition.Id].ToHashSet()));

        var validation = AttributeValueValidator.Validate(rules, commands);
        if (!validation.IsValid)
        {
            return (CatalogErrors.AttributeValuesInvalid(validation.Problems), null);
        }

        var byId = definitions.ToDictionary(definition => definition.Id);
        return (null, validation.Values.Select(value => (byId[value.Rule.Id], value.Input)).ToList());
    }

    // ---- Read helpers -------------------------------------------------------------------------------------

    private async Task<ProductDetails?> LoadProductAsync(Guid id, Guid organizationId, CancellationToken cancellationToken)
    {
        var row = await (
                from stored in db.Products.AsNoTracking().Where(candidate => candidate.Id == id && candidate.OrganizationId == organizationId)
                join unit in db.UnitsOfMeasure on stored.BaseUnitId equals unit.Id
                select new
                {
                    Product = stored,
                    CategoryName = db.ProductCategories.Where(category => category.Id == stored.CategoryId).Select(category => category.Name).FirstOrDefault(),
                    UnitCode = unit.Code,
                    unit.DecimalPlaces,
                    OnHand = db.InventoryBalances.Where(balance => balance.ProductId == stored.Id).Sum(balance => (decimal?)balance.QuantityOnHand) ?? 0,
                    Reserved = db.InventoryBalances.Where(balance => balance.ProductId == stored.Id).Sum(balance => (decimal?)balance.QuantityReserved) ?? 0,
                })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var attributes = await ProductValuesAsync([id], cancellationToken);
        var variants = await LoadVariantsAsync(db.ProductVariants.AsNoTracking().Where(variant => variant.ProductId == id && variant.OrganizationId == organizationId), cancellationToken);
        var product = row.Product;
        return new ProductDetails(
            product.Id, product.CategoryId, row.CategoryName, product.Sku, product.Barcode, product.Name, product.Description,
            product.BaseUnitId, row.UnitCode, row.DecimalPlaces, product.HasVariants, product.TracksStock, product.TracksBatches,
            product.RequiresExpiry, product.AllowNegativeStock, product.ReorderLevel, product.CostPrice, product.SalePrice, product.IsActive,
            row.OnHand, row.Reserved, row.OnHand - row.Reserved, attributes.GetValueOrDefault(id) ?? [], variants, product.CreatedAtUtc, product.UpdatedAtUtc);
    }

    private async Task<VariantDetails?> LoadVariantAsync(Guid productId, Guid variantId, Guid organizationId, CancellationToken cancellationToken)
    {
        var rows = await LoadVariantsAsync(
            db.ProductVariants.AsNoTracking().Where(variant => variant.Id == variantId && variant.ProductId == productId && variant.OrganizationId == organizationId), cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    private async Task<List<VariantDetails>> LoadVariantsAsync(IQueryable<ProductVariant> variants, CancellationToken cancellationToken)
    {
        var rows = await variants.OrderBy(variant => variant.Sku).ThenBy(variant => variant.Id)
            .Select(variant => new
            {
                Variant = variant,
                OnHand = db.InventoryBalances.Where(balance => balance.ProductVariantId == variant.Id).Sum(balance => (decimal?)balance.QuantityOnHand) ?? 0,
                Reserved = db.InventoryBalances.Where(balance => balance.ProductVariantId == variant.Id).Sum(balance => (decimal?)balance.QuantityReserved) ?? 0,
            })
            .ToListAsync(cancellationToken);
        var attributes = await VariantValuesAsync(rows.Select(row => row.Variant.Id).ToList(), cancellationToken);

        return rows.ConvertAll(row => new VariantDetails(
            row.Variant.Id, row.Variant.ProductId, row.Variant.Sku, row.Variant.Barcode, row.Variant.Name, row.Variant.ReorderLevel,
            row.Variant.CostPrice, row.Variant.SalePrice, row.Variant.IsActive, row.OnHand, row.OnHand - row.Reserved,
            attributes.GetValueOrDefault(row.Variant.Id) ?? [], row.Variant.CreatedAtUtc, row.Variant.UpdatedAtUtc));
    }

    private async Task<Dictionary<Guid, List<AttributeValueDetails>>> ProductValuesAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        var values = await db.ProductAttributeValues.AsNoTracking().Where(value => productIds.Contains(value.ProductId)).ToListAsync(cancellationToken);
        return await DescribeAsync(values.Select(value => (value.ProductId, (AttributeValueBase)value)).ToList(), cancellationToken);
    }

    private async Task<Dictionary<Guid, List<AttributeValueDetails>>> VariantValuesAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        var values = await db.ProductVariantAttributeValues.AsNoTracking().Where(value => variantIds.Contains(value.ProductVariantId)).ToListAsync(cancellationToken);
        return await DescribeAsync(values.Select(value => (value.ProductVariantId, (AttributeValueBase)value)).ToList(), cancellationToken);
    }

    /// <summary>Joins stored values with their attribute definitions and options (a MultiSelect's rows become one value with several options).</summary>
    private async Task<Dictionary<Guid, List<AttributeValueDetails>>> DescribeAsync(List<(Guid Owner, AttributeValueBase Value)> values, CancellationToken cancellationToken)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var definitionIds = values.Select(pair => pair.Value.AttributeDefinitionId).Distinct().ToList();
        var definitions = await db.ProductAttributeDefinitions.AsNoTracking().Where(definition => definitionIds.Contains(definition.Id)).ToDictionaryAsync(definition => definition.Id, cancellationToken);
        var optionIds = values.Where(pair => pair.Value.OptionId is not null).Select(pair => pair.Value.OptionId!.Value).Distinct().ToList();
        var options = await db.ProductAttributeOptions.AsNoTracking().Where(option => optionIds.Contains(option.Id)).ToDictionaryAsync(option => option.Id, cancellationToken);

        return values
            .GroupBy(pair => pair.Owner)
            .ToDictionary(
                owner => owner.Key,
                owner => owner.GroupBy(pair => pair.Value.AttributeDefinitionId)
                    .Select(group =>
                    {
                        var definition = definitions[group.Key];
                        var first = group.First().Value;
                        var chosen = group.Where(pair => pair.Value.OptionId is not null)
                            .Select(pair => options[pair.Value.OptionId!.Value])
                            .OrderBy(option => option.SortOrder).ThenBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
                            .Select(option => new AttributeOptionRef(option.Id, option.Label))
                            .ToList();
                        return (Definition: definition, Value: new AttributeValueDetails(
                            definition.Id, definition.Code, definition.Name, definition.DataType, definition.UnitLabel,
                            first.TextValue, first.NumberValue, first.DateValue, first.BooleanValue, chosen));
                    })
                    .OrderBy(item => item.Definition.SortOrder).ThenBy(item => item.Definition.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(item => item.Value)
                    .ToList());
    }

    private static ProductSpec ToSpec(SaveProductCommand command) => new(
        command.CategoryId, command.Sku, command.Barcode, command.Name, command.Description, command.BaseUnitId, command.HasVariants,
        command.TracksStock, command.TracksBatches, command.RequiresExpiry, command.AllowNegativeStock, command.ReorderLevel,
        command.CostPrice, command.SalePrice);

    private static ProductVariantSpec ToSpec(SaveVariantCommand command) =>
        new(command.Sku, command.Barcode, command.Name, command.ReorderLevel, command.CostPrice, command.SalePrice);
}
