namespace BusinessSuite.Domain.Common;

/// <summary>
/// Base class for entities that record who created and last modified them.
/// Most business entities (products, invoices, customers, ...) should inherit from this.
/// </summary>
/// <remarks>
/// The setters are private on purpose: the values are written by EF Core through the
/// <c>AuditableEntityInterceptor</c>, so application code cannot forge them.
/// </remarks>
public abstract class AuditableEntity : Entity, IAuditable
{
    /// <inheritdoc />
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <inheritdoc />
    public Guid? CreatedByUserId { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    /// <inheritdoc />
    public Guid? UpdatedByUserId { get; private set; }
}
