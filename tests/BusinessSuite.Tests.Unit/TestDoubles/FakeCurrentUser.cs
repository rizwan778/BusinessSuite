using BusinessSuite.Application.Common.Abstractions;

namespace BusinessSuite.Tests.Unit.TestDoubles;

/// <summary>Hand-written stand-in for <see cref="ICurrentUser"/>; set only what a test needs.</summary>
internal sealed class FakeCurrentUser : ICurrentUser
{
    public bool IsAuthenticated { get; init; }

    public Guid? UserId { get; init; }

    public string? UserName { get; init; }

    public string? DisplayName { get; init; }

    public Guid? OrganizationId { get; init; }

    public string? OrganizationRole { get; init; }

    public bool IsSuperAdmin { get; init; }

    public IReadOnlyCollection<string> Permissions { get; init; } = [];

    public bool HasPermission(string permission) => IsSuperAdmin || Permissions.Contains(permission);

    public static FakeCurrentUser Anonymous() => new();

    public static FakeCurrentUser SignedIn(string? userName = "user@example.com", string? displayName = null) => new()
    {
        IsAuthenticated = true,
        UserId = Guid.NewGuid(),
        UserName = userName,
        DisplayName = displayName,
    };
}
