using BusinessSuite.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BusinessSuite.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="AuditLog"/> to <c>audit.AuditLogs</c>.</summary>
/// <remarks>
/// No foreign keys on purpose: an entry must outlive the role, group or user it describes. The migration adds a trigger
/// (<c>TR_AuditLogs_AppendOnly</c>) that refuses UPDATE and DELETE, so entries cannot be changed through the application's
/// database login. It is not declared to EF: EF only ever INSERTs here, and the insert reads nothing back (the key is
/// generated on the client), so it never needs the OUTPUT clause that triggers rule out.
/// </remarks>
internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs", DatabaseSchemas.Audit);

        builder.Property(entry => entry.Action)
            .HasMaxLength(AuditLog.ActionMaxLength)
            .IsRequired();

        builder.Property(entry => entry.EntityType)
            .HasMaxLength(AuditLog.EntityTypeMaxLength)
            .IsRequired();

        builder.Property(entry => entry.CorrelationId)
            .HasMaxLength(AuditLog.CorrelationIdMaxLength);

        builder.Property(entry => entry.IpAddress)
            .HasMaxLength(AuditLog.IpAddressMaxLength);

        // An organization's log, newest first (the API's main query), and "what happened to this record / by this person".
        builder.HasIndex(entry => new { entry.OrganizationId, entry.OccurredAtUtc });
        builder.HasIndex(entry => new { entry.EntityType, entry.EntityId });
        builder.HasIndex(entry => entry.ActorUserId);
    }
}
