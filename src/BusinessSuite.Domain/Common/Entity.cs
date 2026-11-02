namespace BusinessSuite.Domain.Common;

/// <summary>
/// Base class for every business entity that is stored in the database.
/// </summary>
/// <remarks>
/// <para>
/// The identifier is a <see cref="Guid"/>. Leave it empty when you create a new entity:
/// EF Core generates a sequential GUID (index-friendly for SQL Server) when the entity is
/// added to the DbContext.
/// </para>
/// <para>
/// Keep business rules inside entities (methods that validate and then change state),
/// never in controllers, Razor views or JavaScript.
/// </para>
/// </remarks>
public abstract class Entity
{
    /// <summary>Unique identifier (primary key).</summary>
    public Guid Id { get; protected set; }
}
