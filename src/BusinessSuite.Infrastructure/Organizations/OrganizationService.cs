using System.Linq.Expressions;
using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Accounts;
using BusinessSuite.Application.Features.Audit;
using BusinessSuite.Application.Features.Organizations;
using BusinessSuite.Application.Features.Roles;
using BusinessSuite.Application.Features.Users;
using BusinessSuite.Domain.Common;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Auditing;
using BusinessSuite.Infrastructure.Identity;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Organizations;

/// <summary>
/// <see cref="IOrganizationService"/> on SQL Server. Who reaches which organization is decided by
/// <see cref="OrganizationAccessRules"/>; this class loads the facts, applies the change and records it in the log.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Tenant isolation:</b> outside platform operators, only the organization from
/// <see cref="ICurrentUser.OrganizationId"/> is reachable; every other id is <see cref="OrganizationErrors.NotFound"/>.</item>
/// <item>The member and role lists check that the organization is visible and then DELEGATE to
/// <see cref="IUserManagementService"/> and <see cref="IRoleService"/>, so their rules (and logging) stay in one place.</item>
/// <item>Suspension takes effect on the members' next request: access is calculated per request from the database
/// (<c>UserAccessService</c> ignores suspended organizations). Nothing is deleted.</item>
/// <item>Expected failures are <see cref="Result"/> values. The only database race caught is a concurrent change or
/// removal of the row (409); anything else is unexpected and left to the API exception handler (logged, 500).</item>
/// <item>Creating an organization WITH its first administrator is one transaction (organization, account, Owner membership,
/// set-up e-mail): any failure leaves nothing behind. The administrator's password, if given, is never logged.</item>
/// <item>Logs contain user and organization ids only, never organization names, e-mail addresses or member details.</item>
/// <item><b>Audit:</b> creation, renaming, suspension and reactivation add an <see cref="AuditTrail"/> entry in the
/// organization's own log, saved with the change. Creating with a first administrator also records the user's creation.</item>
/// </list>
/// </remarks>
internal sealed class OrganizationService(
    AppDbContext db,
    ICurrentUser currentUser,
    IUserManagementService users,
    IRoleService roles,
    UserManager<ApplicationUser> userManager,
    PasswordResetMailer resetMailer,
    AuditTrail audit,
    ILogger<OrganizationService> logger) : IOrganizationService
{
    private readonly Expression<Func<Organization, OrganizationDetails>> toDetails = organization => new OrganizationDetails(
        organization.Id,
        organization.Name,
        organization.IsActive,
        db.OrganizationMemberships.Count(membership => membership.OrganizationId == organization.Id),
        organization.CreatedAtUtc,
        organization.UpdatedAtUtc);

    // ---- Reads ------------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<OrganizationDetails>>> ListAsync(OrganizationListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var scope = OrganizationAccessRules.ListScope(currentUser);
        if (scope.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused an organization list ({Code}).", currentUser.UserId, scope.Error.Code);
            return scope.Error;
        }

        var organizations = db.Organizations.AsNoTracking();
        if (scope.Value is { } organizationId)
        {
            organizations = organizations.Where(organization => organization.Id == organizationId);
        }

        if (Clean(query.Search) is { } search)
        {
            organizations = organizations.Where(organization => organization.Name.Contains(search));
        }

        organizations = query.Status switch
        {
            OrganizationStatusFilter.Active => organizations.Where(organization => organization.IsActive),
            OrganizationStatusFilter.Suspended => organizations.Where(organization => !organization.IsActive),
            _ => organizations,
        };

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await organizations.CountAsync(cancellationToken);
        var items = await organizations
            .OrderBy(organization => organization.Name).ThenBy(organization => organization.Id) // stable across pages
            .Skip(page.Skip).Take(page.PageSize)
            .Select(toDetails)
            .ToListAsync(cancellationToken);

        return new PagedResult<OrganizationDetails>(items, page, totalCount);
    }

    public async Task<Result<OrganizationDetails>> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var access = OrganizationAccessRules.CheckAccess(currentUser, organizationId);
        if (access.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused organization {OrganizationId} ({Code}).", currentUser.UserId, organizationId, access.Error.Code);
            return access.Error;
        }

        var details = await LoadAsync(organizationId, cancellationToken);
        return details is null ? OrganizationErrors.NotFound : details;
    }

    public async Task<Result<PagedResult<UserDetails>>> ListUsersAsync(Guid organizationId, UserListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var visible = await CheckVisibleAsync(organizationId, "list the users of", cancellationToken);
        if (visible.IsFailure)
        {
            return visible.Error;
        }

        // The user service applies its own scope rules on top, so this can never widen what the caller sees.
        return await users.ListAsync(query with { OrganizationId = organizationId }, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<RoleDetails>>> ListRolesAsync(Guid organizationId, RoleListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var visible = await CheckVisibleAsync(organizationId, "list the roles of", cancellationToken);
        if (visible.IsFailure)
        {
            return visible.Error;
        }

        return await roles.ListAsync(query with { OrganizationId = organizationId }, cancellationToken);
    }

    // ---- Changes ----------------------------------------------------------------------------------------

    public async Task<Result<OrganizationDetails>> CreateAsync(CreateOrganizationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var allowed = OrganizationAccessRules.CheckCreate(currentUser);
        if (allowed.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused creating an organization ({Code}).", currentUser.UserId, allowed.Error.Code);
            return allowed.Error;
        }

        if (command.InitialAdmin is not null)
        {
            var superAdminOnly = OrganizationAccessRules.CheckCreateInitialAdmin(currentUser);
            if (superAdminOnly.IsFailure)
            {
                logger.LogInformation("User {ActorId} was refused creating an organization with an initial administrator ({Code}).",
                    currentUser.UserId, superAdminOnly.Error.Code);
                return superAdminOnly.Error;
            }
        }

        // Validate the name up front (the organization itself is built again inside the transaction).
        try
        {
            _ = Organization.Create(command.Name);
        }
        catch (DomainException exception)
        {
            return OrganizationErrors.Invalid(exception.Message);
        }
        catch (ArgumentException)
        {
            return OrganizationErrors.Invalid("Enter an organization name.");
        }

        if (command.InitialAdmin is { } initialAdmin)
        {
            return await CreateWithInitialAdminAsync(command.Name, initialAdmin, cancellationToken);
        }

        // Nothing can conflict: organizations have no unique key, so a failure here is unexpected (500).
        var organization = Organization.Create(command.Name);
        db.Organizations.Add(organization);
        audit.Record(AuditActions.OrganizationCreated, AuditActions.EntityTypes.Organization, organization.Id, organization.Id,
            new { name = organization.Name, initialAdmin = false });
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User {ActorId} created organization {OrganizationId}.", currentUser.UserId, organization.Id);
        return await ReloadAsync(organization.Id, cancellationToken);
    }

    /// <summary>
    /// The organization, its first administrator's account and their Owner membership, created in ONE transaction: any
    /// failure (a weak password, an e-mail address already in use, an e-mail that cannot be sent) leaves nothing behind.
    /// </summary>
    private async Task<Result<OrganizationDetails>> CreateWithInitialAdminAsync(string organizationName, InitialAdminCommand admin, CancellationToken cancellationToken)
    {
        var email = admin.Email?.Trim();
        var displayName = Clean(admin.DisplayName);
        var phoneNumber = Clean(admin.PhoneNumber);
        if (string.IsNullOrEmpty(email))
        {
            return OrganizationErrors.Invalid("Enter the e-mail address of the first administrator.");
        }

        if (displayName?.Length > ApplicationUser.DisplayNameMaxLength)
        {
            return OrganizationErrors.Invalid($"A display name cannot be longer than {ApplicationUser.DisplayNameMaxLength} characters.");
        }

        if (phoneNumber?.Length > UserManagementService.PhoneNumberMaxLength)
        {
            return OrganizationErrors.Invalid($"A phone number cannot be longer than {UserManagementService.PhoneNumberMaxLength} characters.");
        }

        // Existing accounts cannot be adopted by e-mail address (that needs the person's consent: invitations, planned).
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            logger.LogInformation("User {ActorId} could not create an organization: the first administrator's e-mail address belongs to existing user {UserId}.",
                currentUser.UserId, existing.Id);
            return UserErrors.EmailBelongsToExistingAccount;
        }

        var sendSetupEmail = string.IsNullOrEmpty(admin.Password);
        Guid organizationId = Guid.Empty;
        Guid adminUserId = Guid.Empty;
        Result outcome;
        try
        {
            outcome = await DatabaseTransactions.RunAsync<Result>(db, async () =>
            {
                var organization = Organization.Create(organizationName);
                db.Organizations.Add(organization);
                await db.SaveChangesAsync(cancellationToken);

                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    DisplayName = displayName,
                    PhoneNumber = phoneNumber,
                };

                var identityResult = sendSetupEmail
                    ? await userManager.CreateAsync(user)
                    : await userManager.CreateAsync(user, admin.Password!);
                if (!identityResult.Succeeded)
                {
                    logger.LogInformation("Creation of the first administrator rejected by Identity: {IdentityErrorCodes}.",
                        string.Join(", ", identityResult.Errors.Select(error => error.Code)));
                    return UserRegistrationService.ToError(identityResult.Errors.ToList());
                }

                db.OrganizationMemberships.Add(OrganizationMembership.Create(organization.Id, user.Id, OrganizationRoles.Owner));
                audit.Record(AuditActions.OrganizationCreated, AuditActions.EntityTypes.Organization, organization.Id, organization.Id,
                    new { name = organization.Name, initialAdmin = true, initialAdminUserId = user.Id });
                audit.Record(AuditActions.UserCreated, AuditActions.EntityTypes.User, user.Id, organization.Id,
                    new { roleId = OrganizationRoles.OwnerId, role = OrganizationRoles.Owner, department = (string?)null, setupEmailSent = sendSetupEmail });
                await db.SaveChangesAsync(cancellationToken);

                // Inside the transaction: if the e-mail cannot be sent, nothing is created and the request can be retried.
                if (sendSetupEmail)
                {
                    await resetMailer.SendAsync(user, isNewAccount: true, cancellationToken);
                }

                organizationId = organization.Id;
                adminUserId = user.Id;
                return Result.Success();
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (UserRegistrationService.IsUniqueKeyViolation(exception))
        {
            logger.LogWarning("Organization creation by user {ActorId} rejected: a concurrent request created an account with the same e-mail address.", currentUser.UserId);
            return AccountErrors.DuplicateEmail;
        }

        if (outcome.IsFailure)
        {
            return outcome.Error;
        }

        logger.LogInformation("User {ActorId} created organization {OrganizationId} with first administrator {AdminUserId} as its Owner (password set-up e-mail: {SetupEmailSent}).",
            currentUser.UserId, organizationId, adminUserId, sendSetupEmail);
        return await ReloadAsync(organizationId, cancellationToken);
    }

    public async Task<Result<OrganizationDetails>> UpdateAsync(Guid organizationId, UpdateOrganizationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var found = await FindForChangeAsync(organizationId, "update", cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var organization = found.Value;
        var previousName = organization.Name;
        try
        {
            organization.Rename(command.Name);
        }
        catch (DomainException exception)
        {
            return OrganizationErrors.Invalid(exception.Message);
        }
        catch (ArgumentException)
        {
            return OrganizationErrors.Invalid("Enter an organization name.");
        }

        if (!string.Equals(previousName, organization.Name, StringComparison.Ordinal))
        {
            audit.Record(AuditActions.OrganizationRenamed, AuditActions.EntityTypes.Organization, organization.Id, organization.Id,
                new { from = previousName, to = organization.Name });
        }

        if (await SaveAsync(organization.Id, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        logger.LogInformation("User {ActorId} renamed organization {OrganizationId}.", currentUser.UserId, organization.Id);
        return await ReloadAsync(organization.Id, cancellationToken);
    }

    public async Task<Result<OrganizationDetails>> ChangeStatusAsync(Guid organizationId, bool isActive, CancellationToken cancellationToken = default)
    {
        var allowed = OrganizationAccessRules.CheckChangeStatus(currentUser);
        if (allowed.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused changing the status of organization {OrganizationId} ({Code}).",
                currentUser.UserId, organizationId, allowed.Error.Code);
            return allowed.Error;
        }

        var organization = await db.Organizations.SingleOrDefaultAsync(candidate => candidate.Id == organizationId, cancellationToken);
        if (organization is null)
        {
            logger.LogInformation("User {ActorId} tried to change the status of unknown organization {OrganizationId}.", currentUser.UserId, organizationId);
            return OrganizationErrors.NotFound;
        }

        if (organization.IsActive != isActive)
        {
            if (isActive)
            {
                organization.Activate();
            }
            else
            {
                organization.Suspend();
            }

            audit.Record(isActive ? AuditActions.OrganizationReactivated : AuditActions.OrganizationSuspended,
                AuditActions.EntityTypes.Organization, organization.Id, organization.Id);

            if (await SaveAsync(organization.Id, cancellationToken) is { } conflict)
            {
                return conflict;
            }

            // Affects every member of the organization at once: always visible in the log.
            logger.LogWarning("User {ActorId} {Action} organization {OrganizationId}; its members {Effect} from their next request.",
                currentUser.UserId,
                isActive ? "reactivated" : "suspended",
                organization.Id,
                isActive ? "regain access" : "lose access");
        }

        return await ReloadAsync(organization.Id, cancellationToken);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>The organization, tracked, if the caller may see it (<see cref="OrganizationAccessRules.CheckAccess"/>).</summary>
    private async Task<Result<Organization>> FindForChangeAsync(Guid organizationId, string action, CancellationToken cancellationToken)
    {
        var access = OrganizationAccessRules.CheckAccess(currentUser, organizationId);
        if (access.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused to {Action} organization {OrganizationId} ({Code}).",
                currentUser.UserId, action, organizationId, access.Error.Code);
            return access.Error;
        }

        var organization = await db.Organizations.SingleOrDefaultAsync(candidate => candidate.Id == organizationId, cancellationToken);
        return organization is null ? OrganizationErrors.NotFound : organization;
    }

    /// <summary>Succeeds when the caller may see the organization and it exists.</summary>
    private async Task<Result> CheckVisibleAsync(Guid organizationId, string action, CancellationToken cancellationToken)
    {
        var access = OrganizationAccessRules.CheckAccess(currentUser, organizationId);
        if (access.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused to {Action} organization {OrganizationId} ({Code}).",
                currentUser.UserId, action, organizationId, access.Error.Code);
            return access;
        }

        return await db.Organizations.AsNoTracking().AnyAsync(organization => organization.Id == organizationId, cancellationToken)
            ? Result.Success()
            : OrganizationErrors.NotFound;
    }

    /// <summary>Saves; a row that was changed or removed concurrently becomes a conflict instead of a 500.</summary>
    private async Task<Error?> SaveAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("Change of organization {OrganizationId} by user {ActorId} lost a race with a concurrent change.",
                organizationId, currentUser.UserId);
            return OrganizationErrors.ConcurrentChange;
        }
    }

    private async Task<Result<OrganizationDetails>> ReloadAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var details = await LoadAsync(organizationId, cancellationToken);
        return details is null ? OrganizationErrors.NotFound : details;
    }

    private Task<OrganizationDetails?> LoadAsync(Guid organizationId, CancellationToken cancellationToken) =>
        db.Organizations.AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(toDetails)
            .SingleOrDefaultAsync(cancellationToken);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
