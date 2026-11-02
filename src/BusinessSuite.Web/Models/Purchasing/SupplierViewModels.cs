using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Features.Purchasing;

namespace BusinessSuite.Web.Models.Purchasing;

public sealed class SupplierListViewModel
{
    public string? Search { get; init; }

    /// <summary><c>active</c>, <c>inactive</c> or empty.</summary>
    public string? Status { get; init; }

    public required PagedResult<SupplierSummary> Suppliers { get; init; }
}

public sealed class SupplierFormModel
{
    public Guid? Id { get; set; }

    public string? Name { get; set; }

    public string? ContactPerson { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Address { get; set; }

    public bool IsEdit => Id is not null;

    public SaveSupplierCommand ToCommand() => new(Name, ContactPerson, Phone, Email, Address);

    public static SupplierFormModel From(SupplierSummary supplier) => new()
    {
        Id = supplier.Id,
        Name = supplier.Name,
        ContactPerson = supplier.ContactPerson,
        Phone = supplier.Phone,
        Email = supplier.Email,
        Address = supplier.Address,
    };
}

/// <summary>One line of a supplier's recent activity (a purchase or a return).</summary>
public sealed record SupplierTransaction(DateOnly Date, string Type, string Reference, Guid PurchaseId, decimal? Amount, string Info);

public sealed class SupplierDetailsViewModel
{
    public required SupplierSummary Supplier { get; init; }

    public required SupplierStatistics Statistics { get; init; }

    public required IReadOnlyList<PurchaseSummary> Purchases { get; init; }

    public required IReadOnlyList<SupplierTransaction> Transactions { get; init; }
}

public sealed record SupplierReportRow(SupplierSummary Supplier, SupplierStatistics Statistics);

public sealed class SupplierReportViewModel
{
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public Guid? SupplierId { get; init; }

    public required IReadOnlyList<SupplierReportRow> Rows { get; init; }

    public required SupplierStatistics Totals { get; init; }

    /// <summary>Purchase history of the supplier picked in the report (empty when none picked).</summary>
    public IReadOnlyList<PurchaseSummary> History { get; init; } = [];

    public SupplierSummary? Selected { get; init; }
}
