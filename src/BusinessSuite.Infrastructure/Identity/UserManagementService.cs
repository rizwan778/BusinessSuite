using System.Linq.Expressions;
using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Accounts;
using BusinessSuite.Application.Features.Audit;
using BusinessSuite.Application.Features.Users;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Auditing;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Identity;

/// <summary>
/// <see cref="IUserManagementService"/> on SQL Server and ASP.NET Core Identity. Who may do what is decided by
/// <see cref="UserAccessRules"/>; this class loads the facts, applies the change and records it in the log.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Tenant isolation:</b> the organization always comes from <see cref="ICurrentUser"/> (resolved and
/// authorized on the server for every request). Users outside it answer <see cref="UserErrors.NotFound"/>.</item>
/// <item><b>Last Owner:</b> demoting or deactivating an Owner locks the organization's Owner memberships
/// (<c>UPDLOCK, HOLDLOCK</c>) until the transaction ends, so two administrators demoting the last two Owners at
/// the same moment cannot leave the organization without one.</item>
/// <item>Profile edits do not change the security stamp, so the user stays signed in; account deactivation does,
/// and also revokes every API session.</item>
/// <item>Logs contain user and organization ids only, never e-mail addresses, phone numbers or reset codes.</item>
/// <item><b>Audit:</b> every successful change adds an <see cref="AuditTrail"/> entry saved with the change itself (same
/// <c>SaveChanges</c> / transaction). Membership changes belong to the organization; account status changes by a Super
/// Admin are platform-wide. A change that alters nothing (same role, same status) records nothing.</item>
/// <item><b>Last Owner:</b> a Super Admin cannot deactivate the account of the last active Owner of an active organization
/// either; promote another Owner or suspend the organization first.</item>
/// </list>
/// </remarks>
internal sealed class UserManagementService(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    ICurrentUser currentUser,
    IUserSessionStore sessions,
    RoleCatalog roleCatalog,
    PasswordResetMailer resetMailer,
    AuditTrail audit,
    TimeProvider timeProvider,
    ILogger<UserManagementService> logger) : IUserManagementService
{
    /// <summary>Longest phone number accepted.</summary>
    internal const int PhoneNumberMaxLength = 32;

    private static readonly string[] PlatformRoleNames = [.. PlatformRoles.All];

    private static readonly Expression<Func<ApplicationUser, UserRow>> ToRow = user => new UserRow(
        user.Id,
        user.Email!,
        user.DisplayName,
        user.PhoneNumber,
        user.IsActive,
        user.LockoutEnd,
        user.EmailConfirmed,
        user.CreatedAtUtc,
        user.UpdatedAtUtc);

    // ---- Reads ------------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<UserDetails>>> ListAsync(UserListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var scope = UserAccessRules.ListScope(currentUser, query.OrganizationId);
        if (scope.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused a user list ({Code}).", currentUser.UserId, scope.Error.Code);
            return scope.Error;
        }

        var organizationId = scope.Value;
        if (organizationId is null && query.SortBy is UserSortField.Role or UserSortField.Department)
        {
            return UserErrors.SortNeedsOrganization;
        }

        // Role filter: by id or name, among the roles of the organization in scope (or any role, unscoped).
        List<Guid>? roleIds = null;
        if (Clean(query.Role) is { } role)
        {
            var candidates = organizationId is { } scopedOrganization ? roleCatalog.AvailableTo(scopedOrganization) : db.RoleDefinitions.AsNoTracking();
            roleIds = Guid.TryParse(role, out var roleId)
                ? await candidates.Where(candidate => candidate.Id == roleId).Select(candidate => candidate.Id).ToListAsync(cancellationToken)
                : await candidates.Where(candidate => candidate.Name == role).Select(candidate => candidate.Id).ToListAsync(cancellationToken);
            if (roleIds.Count == 0)
            {
                return UserErrors.InvalidRole(role);
            }
        }

        // The memberships that count: those of the organization in scope, or all of them (Super Admin, no filter).
        var memberships = organizationId is { } scoped
            ? db.OrganizationMemberships.Where(membership => membership.OrganizationId == scoped)
            : db.OrganizationMemberships;

        var users = db.Users.AsNoTracking();
        if (organizationId is not null)
        {
            users = users.Where(user => memberships.Any(membership => membership.UserId == user.Id));
        }

        if (Clean(query.Search) is { } search)
        {
            users = users.Where(user => user.Email!.Contains(search) || (user.DisplayName != null && user.DisplayName.Contains(search)));
        }

        if (roleIds is not null)
        {
            users = users.Where(user => memberships.Any(membership => membership.UserId == user.Id && roleIds.Contains(membership.RoleId)));
        }

        if (Clean(query.Department) is { } department)
        {
            users = users.Where(user => memberships.Any(membership => membership.UserId == user.Id && membership.Department == department));
        }

        users = (query.Status, organizationId) switch
        {
            (UserStatusFilter.Active, not null) =>
                users.Where(user => user.IsActive && memberships.Any(membership => membership.UserId == user.Id && membership.IsActive)),
            (UserStatusFilter.Inactive, not null) =>
                users.Where(user => !user.IsActive || !memberships.Any(membership => membership.UserId == user.Id && membership.IsActive)),
            (UserStatusFilter.Active, null) => users.Where(user => user.IsActive),
            (UserStatusFilter.Inactive, null) => users.Where(user => !user.IsActive),
            _ => users,
        };

        var descending = query.SortDirection == SortDirection.Desc;
        var ordered = query.SortBy switch
        {
            UserSortField.DisplayName => Sort(users, user => user.DisplayName, descending),
            UserSortField.CreatedAt => Sort(users, user => user.CreatedAtUtc, descending),
            UserSortField.Role => Sort(users, user => db.RoleDefinitions
                .Where(role => memberships.Any(membership => membership.UserId == user.Id && membership.RoleId == role.Id))
                .Select(role => role.Name)
                .FirstOrDefault(), descending),
            UserSortField.Department => Sort(users, user => memberships.Where(membership => membership.UserId == user.Id).Select(membership => membership.Department).FirstOrDefault(), descending),
            _ => Sort(users, user => user.Email, descending),
        };

        // Stable order across pages, whatever the sort column.
        ordered = ordered.ThenBy(user => user.Email).ThenBy(user => user.Id);

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await users.CountAsync(cancellationToken);
        var rows = await ordered.Skip(page.Skip).Take(page.PageSize).Select(ToRow).ToListAsync(cancellationToken);

        return new PagedResult<UserDetails>(await ToDetailsAsync(rows, organizationId, cancellationToken), page, totalCount);
    }

    public async Task<Result<UserDetails>> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var scope = UserAccessRules.ReadScope(currentUser);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        var details = await LoadAsync(userId, scope.Value, cancellationToken);
        return details is null ? UserErrors.NotFound : details;
    }

    // ---- Account changes -----------------------------------------------------------------------------

    public async Task<Result<UserDetails>> CreateAsync(CreateUserCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = UserAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var role = await roleCatalog.FindAssignableAsync(organizationId, command.Role, cancellationToken);
        if (role is null)
        {
            logger.LogInformation("User {ActorId} named a role that is not available in organization {OrganizationId} when creating a user.",
                currentUser.UserId, organizationId);
            return UserErrors.InvalidRole(command.Role);
        }

        var allowed = UserAccessRules.CheckCreate(currentUser, role);
        if (allowed.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused creating a user with role {RoleId} in organization {OrganizationId} ({Code}).",
                currentUser.UserId, role.Id, organizationId, allowed.Error.Code);
            return allowed.Error;
        }

        var email = command.Email.Trim();
        var displayName = Clean(command.DisplayName);
        var phoneNumber = Clean(command.PhoneNumber);
        var department = Clean(command.Department);
        if ((ValidateProfile(displayName, phoneNumber) ?? ValidateDepartment(department)) is { } invalid)
        {
            return invalid;
        }

        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            var isMember = await db.OrganizationMemberships.AnyAsync(
                membership => membership.OrganizationId == organizationId && membership.UserId == existing.Id, cancellationToken);
            logger.LogInformation("User {ActorId} could not create a user in organization {OrganizationId}: the e-mail address belongs to existing user {UserId}.",
                currentUser.UserId, organizationId, existing.Id);
            return isMember ? UserErrors.AlreadyMember : UserErrors.EmailBelongsToExistingAccount;
        }

        var sendSetupEmail = string.IsNullOrEmpty(command.Password);
        ApplicationUser? created = null;
        Result outcome;
        try
        {
            outcome = await InTransactionAsync(async () =>
            {
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    DisplayName = displayName,
                    PhoneNumber = phoneNumber,
                };

                var identityResult = sendSetupEmail
                    ? await userManager.CreateAsync(user)
                    : await userManager.CreateAsync(user, command.Password!);
                if (!identityResult.Succeeded)
                {
                    logger.LogInformation("User creation rejected by Identity: {IdentityErrorCodes}.",
                        string.Join(", ", identityResult.Errors.Select(error => error.Code)));
                    return UserRegistrationService.ToError(identityResult.Errors.ToList());
                }

                db.OrganizationMemberships.Add(OrganizationMembership.Create(organizationId, user.Id, role.Id, department));
                audit.Record(AuditActions.UserCreated, AuditActions.EntityTypes.User, user.Id, organizationId,
                    new { roleId = role.Id, role = role.Name, department, setupEmailSent = sendSetupEmail });
                await db.SaveChangesAsync(cancellationToken);

                // Inside the transaction: if the e-mail cannot be sent, nothing is created and the request can be retried.
                if (sendSetupEmail)
                {
                    await resetMailer.SendAsync(user, isNewAccount: true, cancellationToken);
                }

                created = user;
                return Result.Success();
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (UserRegistrationService.IsUniqueKeyViolation(exception))
        {
            logger.LogWarning("User creation rejected: a concurrent request created an account with the same e-mail address.");
            return AccountErrors.DuplicateEmail;
        }
        catch (DbUpdateException exception) when (IsForeignKeyViolation(exception))
        {
            logger.LogWarning("User creation in organization {OrganizationId} rejected: role {RoleId} was deleted concurrently.", organizationId, role.Id);
            return UserErrors.RoleNoLongerExists;
        }

        if (outcome.IsFailure)
        {
            return outcome.Error;
        }

        logger.LogInformation("User {ActorId} created user {UserId} in organization {OrganizationId} with role {RoleId} (password set-up e-mail: {SetupEmailSent}).",
            currentUser.UserId, created!.Id, organizationId, role.Id, sendSetupEmail);
        return await ReloadAsync(created.Id, cancellationToken);
    }

    public async Task<Result<UserDetails>> UpdateAsync(Guid userId, UpdateUserCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var displayName = Clean(command.DisplayName);
        var phoneNumber = Clean(command.PhoneNumber);
        if (ValidateProfile(displayName, phoneNumber) is { } invalid)
        {
            return invalid;
        }

        var target = await FindForChangeAsync(userId, UserChange.Profile, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error;
        }

        var user = target.Value;
        var displayNameChanged = !string.Equals(user.DisplayName, displayName, StringComparison.Ordinal);
        var phoneNumberChanged = !string.Equals(user.PhoneNumber, phoneNumber, StringComparison.Ordinal);
        user.DisplayName = displayName;
        if (phoneNumberChanged)
        {
            // Not through UserManager.SetPhoneNumberAsync: that changes the security stamp and would sign the user out.
            user.PhoneNumber = phoneNumber;
            user.PhoneNumberConfirmed = false;
        }

        // The phone number itself is personal data: the entry only says that it changed.
        var entry = displayNameChanged || phoneNumberChanged
            ? audit.Record(AuditActions.UserProfileUpdated, AuditActions.EntityTypes.User, userId, currentUser.OrganizationId,
                new { displayNameChanged, phoneNumberChanged })
            : null;

        var updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            if (entry is not null)
            {
                audit.Discard(entry);
            }

            return ToUpdateError(updated, userId);
        }

        logger.LogInformation("User {ActorId} updated the profile of user {UserId}.", currentUser.UserId, userId);
        return await ReloadAsync(userId, cancellationToken);
    }

    public async Task<Result> InitiatePasswordResetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var target = await FindForChangeAsync(userId, UserChange.PasswordReset, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error;
        }

        // Entry and e-mail together: if the e-mail cannot be sent, the entry is rolled back (and the request can be retried).
        var user = target.Value;
        await InTransactionAsync(async () =>
        {
            audit.Record(AuditActions.UserPasswordResetInitiated, AuditActions.EntityTypes.User, userId, currentUser.OrganizationId);
            await db.SaveChangesAsync(cancellationToken);
            await resetMailer.SendAsync(user, isNewAccount: false, cancellationToken);
            return Result.Success();
        }, cancellationToken);

        logger.LogInformation("User {ActorId} started a password reset for user {UserId}.", currentUser.UserId, userId);
        return Result.Success();
    }

    // ---- Membership changes (role, department, status) ------------------------------------------------

    public async Task<Result<UserDetails>> ChangeRoleAsync(Guid userId, string role, CancellationToken cancellationToken = default)
    {
        var acting = UserAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        // Only roles the Super Admin made available to this organization, or its own custom roles.
        var newRole = await roleCatalog.FindAssignableAsync(acting.Value, role, cancellationToken);
        if (newRole is null)
        {
            logger.LogInformation("User {ActorId} named a role that is not available in organization {OrganizationId} for user {UserId}.",
                currentUser.UserId, acting.Value, userId);
            return UserErrors.InvalidRole(role);
        }

        return await ChangeMembershipAsync(
            userId,
            UserChange.Role,
            newRole,
            removesOwner: !newRole.IsOwner,
            membership => membership.ChangeRole(newRole.Id),
            cancellationToken);
    }

    public Task<Result<UserDetails>> ChangeDepartmentAsync(Guid userId, string? department, CancellationToken cancellationToken = default)
    {
        var cleaned = Clean(department);
        if (ValidateDepartment(cleaned) is { } invalid)
        {
            return Task.FromResult<Result<UserDetails>>(invalid);
        }

        return ChangeMembershipAsync(
            userId,
            UserChange.Department,
            newRole: null,
            removesOwner: false,
            membership => membership.ChangeDepartment(cleaned),
            cancellationToken);
    }

    public Task<Result<UserDetails>> ChangeStatusAsync(Guid userId, bool isActive, CancellationToken cancellationToken = default)
    {
        // A Super Admin acting in no organization manages the account itself (sign-in everywhere).
        if (currentUser.IsSuperAdmin && currentUser.OrganizationId is null)
        {
            return ChangeAccountStatusAsync(userId, isActive, cancellationToken);
        }

        return ChangeMembershipAsync(
            userId,
            UserChange.Status,
            newRole: null,
            removesOwner: !isActive,
            membership =>
            {
                if (isActive)
                {
                    membership.Activate();
                }
                else
                {
                    membership.Deactivate();
                }
            },
            cancellationToken);
    }

    private async Task<Result<UserDetails>> ChangeMembershipAsync(
        Guid userId,
        UserChange change,
        AssignableRole? newRole,
        bool removesOwner,
        Action<OrganizationMembership> apply,
        CancellationToken cancellationToken)
    {
        var acting = UserAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        Result outcome;
        try
        {
            outcome = await ChangeMembershipInTransactionAsync(userId, organizationId, change, newRole, removesOwner, apply, cancellationToken);
        }
        catch (DbUpdateException exception) when (newRole is not null && IsForeignKeyViolation(exception))
        {
            logger.LogWarning("Role change of user {UserId} in organization {OrganizationId} rejected: role {RoleId} was deleted concurrently.",
                userId, organizationId, newRole.Id);
            return UserErrors.RoleNoLongerExists;
        }

        if (outcome.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused a {Change} change to user {UserId} in organization {OrganizationId} ({Code}).",
                currentUser.UserId, change, userId, organizationId, outcome.Error.Code);
            return outcome.Error;
        }

        logger.LogInformation("User {ActorId} changed the {Change} of user {UserId} in organization {OrganizationId}{RoleDetail}.",
            currentUser.UserId, change, userId, organizationId, newRole is null ? string.Empty : $" to role {newRole.Id}");
        return await ReloadAsync(userId, cancellationToken);
    }

    private Task<Result> ChangeMembershipInTransactionAsync(
        Guid userId,
        Guid organizationId,
        UserChange change,
        AssignableRole? newRole,
        bool removesOwner,
        Action<OrganizationMembership> apply,
        CancellationToken cancellationToken)
    {
        return InTransactionAsync(async () =>
        {
            var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(
                candidate => candidate.OrganizationId == organizationId && candidate.UserId == userId, cancellationToken);
            if (membership is null)
            {
                return UserErrors.NotFound;
            }

            var currentRole = await roleCatalog.FindAsync(membership.RoleId, cancellationToken);
            var target = new ManagedUser(userId, await HasPlatformRoleAsync(userId, cancellationToken), currentRole?.Name, currentRole?.Permissions);
            var allowed = UserAccessRules.CheckChange(currentUser, target, change, newRole);
            if (allowed.IsFailure)
            {
                return allowed;
            }

            if (removesOwner && await IsLastActiveOwnerAsync(membership, cancellationToken))
            {
                return UserErrors.LastOwner;
            }

            var (roleBefore, departmentBefore, activeBefore) = (membership.RoleId, membership.Department, membership.IsActive);
            apply(membership);
            RecordMembershipChange(membership, change, currentRole, newRole, roleBefore, departmentBefore, activeBefore);
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }, cancellationToken);
    }

    /// <summary>Adds the audit entry for a membership change; nothing when the change altered nothing.</summary>
    private void RecordMembershipChange(
        OrganizationMembership membership,
        UserChange change,
        AssignableRole? currentRole,
        AssignableRole? newRole,
        Guid roleBefore,
        string? departmentBefore,
        bool activeBefore)
    {
        var (userId, organizationId) = (membership.UserId, membership.OrganizationId);
        switch (change)
        {
            case UserChange.Role when membership.RoleId != roleBefore:
                audit.Record(AuditActions.UserRoleChanged, AuditActions.EntityTypes.User, userId, organizationId,
                    new { fromRoleId = roleBefore, fromRole = currentRole?.Name, toRoleId = membership.RoleId, toRole = newRole?.Name });
                break;

            case UserChange.Department when !string.Equals(membership.Department, departmentBefore, StringComparison.Ordinal):
                audit.Record(AuditActions.UserGroupChanged, AuditActions.EntityTypes.User, userId, organizationId,
                    new { from = departmentBefore, to = membership.Department });
                break;

            case UserChange.Status when membership.IsActive != activeBefore:
                audit.Record(membership.IsActive ? AuditActions.UserActivated : AuditActions.UserDeactivated,
                    AuditActions.EntityTypes.User, userId, organizationId, new { scope = "membership" });
                break;
        }
    }

    private async Task<Result<UserDetails>> ChangeAccountStatusAsync(Guid userId, bool isActive, CancellationToken cancellationToken)
    {
        var target = await FindForChangeAsync(userId, UserChange.Status, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error;
        }

        if (target.Value.IsActive == isActive)
        {
            return await ReloadAsync(userId, cancellationToken);
        }

        // Account, sessions and audit entry change together. The last-Owner check locks the Owner memberships involved.
        var revoked = 0;
        var outcome = await InTransactionAsync(async () =>
        {
            var user = await userManager.FindByIdAsync(userId.ToString());
            if (user is null)
            {
                return UserErrors.NotFound;
            }

            if (!isActive && await OrganizationsLeftWithoutOwnerAsync(userId, cancellationToken) is > 0 and var stranded)
            {
                return UserErrors.LastOwnerAccount(stranded);
            }

            var entry = audit.Record(isActive ? AuditActions.UserAccountActivated : AuditActions.UserAccountDeactivated,
                AuditActions.EntityTypes.User, userId, organizationId: null, new { scope = "account" });

            IdentityResult updated;
            if (isActive)
            {
                user.Activate();
                updated = await userManager.UpdateAsync(user);
            }
            else
            {
                // A new security stamp ends website sessions and invalidates outstanding reset codes.
                user.Deactivate();
                updated = await userManager.UpdateSecurityStampAsync(user);
            }

            if (!updated.Succeeded)
            {
                audit.Discard(entry);
                return ToUpdateError(updated, userId);
            }

            // Every API session ends now (ApiSessionValidator would also reject them: the account is inactive).
            revoked = isActive ? 0 : await sessions.RevokeAllAsync(userId, cancellationToken);
            return Result.Success();
        }, cancellationToken);

        if (outcome.IsFailure)
        {
            logger.LogInformation("Super Admin {ActorId} was refused a status change to the account of user {UserId} ({Code}).",
                currentUser.UserId, userId, outcome.Error.Code);
            return outcome.Error;
        }

        logger.LogWarning("Super Admin {ActorId} {Action} the account of user {UserId} ({RevokedCount} API session(s) revoked).",
            currentUser.UserId, isActive ? "activated" : "deactivated", userId, revoked);
        return await ReloadAsync(userId, cancellationToken);
    }

    /// <summary>
    /// How many ACTIVE organizations would be left without an active Owner if <paramref name="userId"/>'s account were
    /// deactivated. Locks the Owner memberships read until the transaction ends (same reason as <see cref="IsLastActiveOwnerAsync"/>).
    /// </summary>
    private async Task<int> OrganizationsLeftWithoutOwnerAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Raw SQL for the lock hints; table and schema names match the entity configurations.
        var organizations = await db.Database.SqlQuery<Guid>($"""
            SELECT m.[OrganizationId] AS [Value]
            FROM [platform].[OrganizationMemberships] AS m WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [platform].[Organizations] AS o ON o.[Id] = m.[OrganizationId]
            WHERE m.[UserId] = {userId}
              AND m.[RoleId] = {OrganizationRoles.OwnerId}
              AND m.[IsActive] = CAST(1 AS bit)
              AND o.[IsActive] = CAST(1 AS bit)
              AND NOT EXISTS (
                  SELECT 1
                  FROM [platform].[OrganizationMemberships] AS other WITH (UPDLOCK, HOLDLOCK)
                  INNER JOIN [identity].[Users] AS u ON u.[Id] = other.[UserId]
                  WHERE other.[OrganizationId] = m.[OrganizationId]
                    AND other.[UserId] <> {userId}
                    AND other.[RoleId] = {OrganizationRoles.OwnerId}
                    AND other.[IsActive] = CAST(1 AS bit)
                    AND u.[IsActive] = CAST(1 AS bit))
            """).ToListAsync(cancellationToken);

        return organizations.Count;
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>
    /// The user to change, tracked, if the caller may make <paramref name="change"/> to them. Organization users only
    /// reach members of their organization; anyone else is <see cref="UserErrors.NotFound"/>.
    /// </summary>
    private async Task<Result<ApplicationUser>> FindForChangeAsync(Guid userId, UserChange change, CancellationToken cancellationToken)
    {
        var scope = UserAccessRules.ReadScope(currentUser);
        if (scope.IsFailure)
        {
            return scope.Error;
        }

        AssignableRole? role = null;
        if (currentUser.OrganizationId is { } organizationId)
        {
            var roleId = await db.OrganizationMemberships.AsNoTracking()
                .Where(membership => membership.OrganizationId == organizationId && membership.UserId == userId)
                .Select(membership => (Guid?)membership.RoleId)
                .SingleOrDefaultAsync(cancellationToken);
            role = roleId is { } held ? await roleCatalog.FindAsync(held, cancellationToken) : null;
        }

        if (scope.Value is not null && role is null)
        {
            return UserErrors.NotFound;
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return UserErrors.NotFound;
        }

        var target = new ManagedUser(userId, await HasPlatformRoleAsync(userId, cancellationToken), role?.Name, role?.Permissions);
        var allowed = UserAccessRules.CheckChange(currentUser, target, change);
        if (allowed.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused a {Change} change to user {UserId} ({Code}).",
                currentUser.UserId, change, userId, allowed.Error.Code);
            return allowed.Error;
        }

        return user;
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="membership"/> is the organization's only ACTIVE Owner (active membership
    /// and active account). Locks every Owner membership of the organization until the transaction ends, so concurrent
    /// demotions are serialized instead of both passing this check.
    /// </summary>
    private async Task<bool> IsLastActiveOwnerAsync(OrganizationMembership membership, CancellationToken cancellationToken)
    {
        // Raw SQL for the lock hints; table and schema names match OrganizationMembershipConfiguration / ApplicationUserConfiguration.
        var activeOwners = await db.Database.SqlQuery<Guid>($"""
            SELECT m.[UserId] AS [Value]
            FROM [platform].[OrganizationMemberships] AS m WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [identity].[Users] AS u ON u.[Id] = m.[UserId]
            WHERE m.[OrganizationId] = {membership.OrganizationId}
              AND m.[RoleId] = {OrganizationRoles.OwnerId}
              AND m.[IsActive] = CAST(1 AS bit)
              AND u.[IsActive] = CAST(1 AS bit)
            """).ToListAsync(cancellationToken);

        return activeOwners.Contains(membership.UserId) && activeOwners.All(ownerId => ownerId == membership.UserId);
    }

    private Task<bool> HasPlatformRoleAsync(Guid userId, CancellationToken cancellationToken) =>
        (from userRole in db.UserRoles
         join role in db.Roles on userRole.RoleId equals role.Id
         where userRole.UserId == userId && PlatformRoleNames.Contains(role.Name!)
         select userRole.UserId)
        .AnyAsync(cancellationToken);

    /// <summary>Runs <paramref name="operation"/> in a transaction (committed only on success), retry-safe.</summary>
    private Task<Result> InTransactionAsync(Func<Task<Result>> operation, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear(); // a retry starts from a clean slate
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var result = await operation();
            if (result.IsSuccess)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        });
    }

    /// <summary>The user as the caller may see them after a change.</summary>
    private async Task<Result<UserDetails>> ReloadAsync(Guid userId, CancellationToken cancellationToken)
    {
        var scope = UserAccessRules.ReadScope(currentUser);
        var details = scope.IsSuccess ? await LoadAsync(userId, scope.Value, cancellationToken) : null;
        return details is null ? UserErrors.NotFound : details;
    }

    /// <summary>One user, or <see langword="null"/> when they do not exist or are outside <paramref name="organizationId"/>.</summary>
    private async Task<UserDetails?> LoadAsync(Guid userId, Guid? organizationId, CancellationToken cancellationToken)
    {
        var users = db.Users.AsNoTracking().Where(user => user.Id == userId);
        if (organizationId is { } scoped)
        {
            users = users.Where(user => db.OrganizationMemberships.Any(membership => membership.UserId == user.Id && membership.OrganizationId == scoped));
        }

        var row = await users.Select(ToRow).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : (await ToDetailsAsync([row], organizationId, cancellationToken))[0];
    }

    /// <summary>Adds memberships (only <paramref name="organizationId"/>'s when given) and, for Super Admins, platform roles.</summary>
    private async Task<IReadOnlyList<UserDetails>> ToDetailsAsync(IReadOnlyList<UserRow> rows, Guid? organizationId, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(row => row.Id).ToArray();

        var memberships = (await (
                from membership in db.OrganizationMemberships.AsNoTracking()
                join organization in db.Organizations.AsNoTracking() on membership.OrganizationId equals organization.Id
                join role in db.RoleDefinitions.AsNoTracking() on membership.RoleId equals role.Id
                where ids.Contains(membership.UserId) && (organizationId == null || membership.OrganizationId == organizationId)
                orderby organization.Name
                select new { membership.UserId, organization.Id, organization.Name, Role = role.Name, membership.RoleId, membership.Department, membership.IsActive })
            .ToListAsync(cancellationToken))
            .ToLookup(
                membership => membership.UserId,
                membership => new UserMembershipDetails(membership.Id, membership.Name, membership.Role, membership.Department, membership.IsActive, membership.RoleId));

        // Platform roles are platform information: only Super Admins see them.
        ILookup<Guid, string> platformRoles = Array.Empty<(Guid UserId, string Name)>().ToLookup(role => role.UserId, role => role.Name);
        if (currentUser.IsSuperAdmin)
        {
            platformRoles = (await (
                    from userRole in db.UserRoles
                    join role in db.Roles on userRole.RoleId equals role.Id
                    where ids.Contains(userRole.UserId) && PlatformRoleNames.Contains(role.Name!)
                    select new { userRole.UserId, Name = role.Name! })
                .ToListAsync(cancellationToken))
                .ToLookup(role => role.UserId, role => role.Name);
        }

        var now = timeProvider.GetUtcNow();
        return rows
            .Select(row => new UserDetails(
                row.Id,
                row.Email,
                row.DisplayName,
                row.PhoneNumber,
                row.IsActive,
                row.LockoutEnd > now,
                row.EmailConfirmed,
                platformRoles[row.Id].Order(StringComparer.Ordinal).ToArray(),
                row.CreatedAtUtc,
                row.UpdatedAtUtc,
                memberships[row.Id].ToArray()))
            .ToArray();
    }

    private Error ToUpdateError(IdentityResult result, Guid userId)
    {
        logger.LogInformation("Update of user {UserId} rejected by Identity: {IdentityErrorCodes}.",
            userId, string.Join(", ", result.Errors.Select(error => error.Code)));

        return result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure))
            ? UserErrors.ConcurrentChange
            : UserRegistrationService.ToError(result.Errors.ToList());
    }

    private static Error? ValidateProfile(string? displayName, string? phoneNumber)
    {
        if (displayName?.Length > ApplicationUser.DisplayNameMaxLength)
        {
            return UserErrors.Invalid($"A display name cannot be longer than {ApplicationUser.DisplayNameMaxLength} characters.");
        }

        if (phoneNumber?.Length > PhoneNumberMaxLength)
        {
            return UserErrors.Invalid($"A phone number cannot be longer than {PhoneNumberMaxLength} characters.");
        }

        return null;
    }

    private static Error? ValidateDepartment(string? department) =>
        department?.Length > OrganizationMembership.DepartmentMaxLength
            ? UserErrors.Invalid($"A department name cannot be longer than {OrganizationMembership.DepartmentMaxLength} characters.")
            : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>SQL Server error 547: a foreign key (here: the membership's role) no longer matches a row.</summary>
    private static bool IsForeignKeyViolation(DbUpdateException exception) =>
        exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 547 };

    private static IOrderedQueryable<T> Sort<T, TKey>(IQueryable<T> source, Expression<Func<T, TKey>> key, bool descending) =>
        descending ? source.OrderByDescending(key) : source.OrderBy(key);

    private sealed record UserRow(
        Guid Id,
        string Email,
        string? DisplayName,
        string? PhoneNumber,
        bool IsActive,
        DateTimeOffset? LockoutEnd,
        bool EmailConfirmed,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? UpdatedAtUtc);
}
