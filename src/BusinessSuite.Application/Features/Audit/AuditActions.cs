namespace BusinessSuite.Application.Features.Audit;

/// <summary>
/// The audited actions: stable "<c>entity.verb</c>" codes stored in <c>audit.AuditLogs.Action</c> and used to filter the
/// audit API. Part of the API contract: do not rename released ones.
/// </summary>
public static class AuditActions
{
    // ---- Users -------------------------------------------------------------------------------------------

    /// <summary>A user account was created with a membership in the organization.</summary>
    public const string UserCreated = "user.created";

    /// <summary>A user's display name or phone number was changed by an administrator.</summary>
    public const string UserProfileUpdated = "user.profile_updated";

    /// <summary>A member's role in the organization was changed.</summary>
    public const string UserRoleChanged = "user.role_changed";

    /// <summary>A member's department (group) was changed.</summary>
    public const string UserGroupChanged = "user.group_changed";

    /// <summary>A membership was activated.</summary>
    public const string UserActivated = "user.activated";

    /// <summary>A membership was deactivated: the member lost access to the organization.</summary>
    public const string UserDeactivated = "user.deactivated";

    /// <summary>A Super Admin activated an account (platform-wide).</summary>
    public const string UserAccountActivated = "user.account_activated";

    /// <summary>A Super Admin deactivated an account: sign-in blocked and every session ended (platform-wide).</summary>
    public const string UserAccountDeactivated = "user.account_deactivated";

    /// <summary>An administrator e-mailed a user a password-reset code.</summary>
    public const string UserPasswordResetInitiated = "user.password_reset_initiated";

    // ---- Roles and permissions ---------------------------------------------------------------------------

    /// <summary>A role was created.</summary>
    public const string RoleCreated = "role.created";

    /// <summary>A role was renamed or re-described.</summary>
    public const string RoleUpdated = "role.updated";

    /// <summary>A role was deleted.</summary>
    public const string RoleDeleted = "role.deleted";

    /// <summary>A role's permissions were changed.</summary>
    public const string RolePermissionsChanged = "role.permissions_changed";

    /// <summary>Which organizations may use a shared role was changed.</summary>
    public const string RoleAvailabilityChanged = "role.availability_changed";

    // ---- Groups ------------------------------------------------------------------------------------------

    /// <summary>A group (department) was created.</summary>
    public const string GroupCreated = "group.created";

    /// <summary>A group was renamed or re-described (members move with a rename).</summary>
    public const string GroupUpdated = "group.updated";

    /// <summary>A group was deleted.</summary>
    public const string GroupDeleted = "group.deleted";

    // ---- Organizations -----------------------------------------------------------------------------------

    /// <summary>An organization was created (optionally with its first administrator).</summary>
    public const string OrganizationCreated = "organization.created";

    /// <summary>An organization was renamed.</summary>
    public const string OrganizationRenamed = "organization.renamed";

    /// <summary>An organization was suspended: its members lost access.</summary>
    public const string OrganizationSuspended = "organization.suspended";

    /// <summary>A suspended organization was reactivated.</summary>
    public const string OrganizationReactivated = "organization.reactivated";

    // ---- Stock -------------------------------------------------------------------------------------------
    // The ledger itself (inventory.StockMovements) is the detailed history of stock; these entries record who started and
    // finished each stock DOCUMENT, in the same transaction as the movements it wrote.

    /// <summary>A draft stock adjustment was created.</summary>
    public const string StockAdjustmentCreated = "stock_adjustment.created";

    /// <summary>A stock adjustment was posted: its lines became ledger movements and changed the balances.</summary>
    public const string StockAdjustmentPosted = "stock_adjustment.posted";

    /// <summary>A draft stock adjustment was cancelled.</summary>
    public const string StockAdjustmentCancelled = "stock_adjustment.cancelled";

    /// <summary>A draft stock transfer was created.</summary>
    public const string StockTransferCreated = "stock_transfer.created";

    /// <summary>A stock transfer was posted: stock left the source warehouse and arrived at the destination.</summary>
    public const string StockTransferPosted = "stock_transfer.posted";

    /// <summary>A draft stock transfer was cancelled.</summary>
    public const string StockTransferCancelled = "stock_transfer.cancelled";

    // ---- Products and setup ------------------------------------------------------------------------------

    public const string ProductCreated = "product.created";
    public const string ProductUpdated = "product.updated";
    public const string ProductActivated = "product.activated";
    public const string ProductDeactivated = "product.deactivated";
    public const string ProductDeleted = "product.deleted";

    public const string VariantCreated = "variant.created";
    public const string VariantUpdated = "variant.updated";
    public const string VariantActivated = "variant.activated";
    public const string VariantDeactivated = "variant.deactivated";
    public const string VariantDeleted = "variant.deleted";

    public const string AttributeCreated = "attribute.created";
    public const string AttributeUpdated = "attribute.updated";
    public const string AttributeActivated = "attribute.activated";
    public const string AttributeDeactivated = "attribute.deactivated";
    public const string AttributeDeleted = "attribute.deleted";
    public const string AttributeOptionAdded = "attribute.option_added";
    public const string AttributeOptionUpdated = "attribute.option_updated";
    public const string AttributeOptionDeleted = "attribute.option_deleted";

    public const string CategoryCreated = "category.created";
    public const string CategoryUpdated = "category.updated";
    public const string CategoryActivated = "category.activated";
    public const string CategoryDeactivated = "category.deactivated";
    public const string CategoryDeleted = "category.deleted";

    public const string UnitCreated = "unit.created";
    public const string UnitUpdated = "unit.updated";
    public const string UnitActivated = "unit.activated";
    public const string UnitDeactivated = "unit.deactivated";
    public const string UnitDeleted = "unit.deleted";
    public const string UnitConversionCreated = "unit.conversion_created";
    public const string UnitConversionUpdated = "unit.conversion_updated";
    public const string UnitConversionDeleted = "unit.conversion_deleted";

    public const string WarehouseCreated = "warehouse.created";
    public const string WarehouseUpdated = "warehouse.updated";
    public const string WarehouseActivated = "warehouse.activated";
    public const string WarehouseDeactivated = "warehouse.deactivated";
    public const string WarehouseDefaultChanged = "warehouse.default_changed";
    public const string WarehouseDeleted = "warehouse.deleted";

    public const string BatchRegistered = "batch.registered";
    public const string BatchActivated = "batch.activated";
    public const string BatchClosed = "batch.closed";

    /// <summary>The kinds of record an entry can be about (<c>entityType</c>).</summary>
    public static class EntityTypes
    {
        public const string User = "User";
        public const string Role = "Role";
        public const string Group = "Group";
        public const string Organization = "Organization";
        public const string Product = "Product";
        public const string ProductVariant = "ProductVariant";
        public const string ProductAttribute = "ProductAttribute";
        public const string ProductCategory = "ProductCategory";
        public const string UnitOfMeasure = "UnitOfMeasure";
        public const string UnitConversion = "UnitConversion";
        public const string Warehouse = "Warehouse";
        public const string ProductBatch = "ProductBatch";
        public const string StockAdjustment = "StockAdjustment";
        public const string StockTransfer = "StockTransfer";
    }
}
