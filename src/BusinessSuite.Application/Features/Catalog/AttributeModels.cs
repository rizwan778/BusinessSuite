using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Domain.Catalog;

namespace BusinessSuite.Application.Features.Catalog;

/// <summary>An allowed value of a Dropdown or MultiSelect attribute.</summary>
public sealed record AttributeOptionDetails(Guid Id, string Label, int SortOrder, bool IsActive);

/// <summary>An attribute an organization defined, with its options.</summary>
public sealed record AttributeDefinitionDetails(
    Guid Id, string Code, string Name, AttributeDataType DataType, AttributeScope Scope, string? UnitLabel, bool IsRequired,
    bool IsFilterable, Guid? CategoryId, string? CategoryName, int SortOrder, bool IsActive, IReadOnlyList<AttributeOptionDetails> Options);

/// <summary>The organization's attribute schema: what a product (and each variant) can be described with.</summary>
/// <param name="CategoryId">The category the schema was computed for, if any.</param>
/// <param name="ProductAttributes">Attributes that describe the product as a whole.</param>
/// <param name="VariantAttributes">Attributes that describe each variant.</param>
public sealed record AttributeSchema(Guid? CategoryId, IReadOnlyList<AttributeDefinitionDetails> ProductAttributes, IReadOnlyList<AttributeDefinitionDetails> VariantAttributes);

public sealed record AttributeListQuery
{
    public string? Search { get; init; }

    public AttributeScope? Scope { get; init; }

    public AttributeDataType? DataType { get; init; }

    public bool? IsActive { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}

/// <summary>Input for <see cref="IAttributeService.CreateAsync"/>. Code, data type and scope are fixed afterwards.</summary>
public sealed record CreateAttributeCommand(
    string? Code, string? Name, AttributeDataType DataType, AttributeScope Scope, bool IsRequired, string? UnitLabel,
    Guid? CategoryId, int SortOrder, bool IsFilterable, IReadOnlyList<SaveOptionCommand>? Options);

/// <summary>Input for <see cref="IAttributeService.UpdateAsync"/>: everything that may still change.</summary>
public sealed record UpdateAttributeCommand(string? Name, bool IsRequired, string? UnitLabel, Guid? CategoryId, int SortOrder, bool IsFilterable);

/// <summary>Input for creating or replacing an option.</summary>
public sealed record SaveOptionCommand(string? Label, int SortOrder, bool IsActive = true);

/// <summary>
/// Attribute definitions and their options. Controllers require <c>inventory.view</c> / <c>inventory.attribute.manage</c>. Defining an
/// attribute is data, not a schema change.
/// </summary>
public interface IAttributeService
{
    Task<Result<PagedResult<AttributeDefinitionDetails>>> ListAsync(AttributeListQuery query, CancellationToken cancellationToken = default);

    /// <summary>The attributes that apply to a product of <paramref name="categoryId"/> (or to every product when null): active ones, with options, in form order.</summary>
    Task<Result<AttributeSchema>> GetSchemaAsync(Guid? categoryId, CancellationToken cancellationToken = default);

    Task<Result<AttributeDefinitionDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<AttributeDefinitionDetails>> CreateAsync(CreateAttributeCommand command, CancellationToken cancellationToken = default);

    Task<Result<AttributeDefinitionDetails>> UpdateAsync(Guid id, UpdateAttributeCommand command, CancellationToken cancellationToken = default);

    Task<Result<AttributeDefinitionDetails>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<AttributeDefinitionDetails>> AddOptionAsync(Guid id, SaveOptionCommand command, CancellationToken cancellationToken = default);

    Task<Result<AttributeDefinitionDetails>> UpdateOptionAsync(Guid id, Guid optionId, SaveOptionCommand command, CancellationToken cancellationToken = default);

    Task<Result<AttributeDefinitionDetails>> DeleteOptionAsync(Guid id, Guid optionId, CancellationToken cancellationToken = default);
}

// ---- Attribute values ------------------------------------------------------------------------------------

/// <summary>
/// A value a client sends for an attribute, identified by the attribute's code. Fill only the field that matches the
/// attribute's data type: <c>text</c>, <c>number</c> (Number, Decimal), <c>date</c>, <c>boolean</c>, <c>optionId</c>
/// (Dropdown) or <c>optionIds</c> (MultiSelect).
/// </summary>
public sealed record AttributeValueCommand(string? Code, string? Text, decimal? Number, DateOnly? Date, bool? Boolean, Guid? OptionId, IReadOnlyList<Guid>? OptionIds);

/// <summary>A value as returned by the API.</summary>
public sealed record AttributeValueDetails(
    Guid AttributeId, string Code, string Name, AttributeDataType DataType, string? UnitLabel, string? Text, decimal? Number,
    DateOnly? Date, bool? Boolean, IReadOnlyList<AttributeOptionRef> Options);

/// <summary>A chosen option.</summary>
public sealed record AttributeOptionRef(Guid Id, string Label);

/// <summary>What the validator needs to know about an attribute that applies.</summary>
public sealed record AttributeRule(Guid Id, string Code, string Name, AttributeDataType DataType, bool IsRequired, bool IsActive, IReadOnlySet<Guid> ActiveOptionIds);

/// <summary>The outcome of validating a set of values: problems, or the values to store (MultiSelect expanded to one per option).</summary>
public sealed record AttributeValidation(IReadOnlyList<string> Problems, IReadOnlyList<(AttributeRule Rule, AttributeValueInput Input)> Values)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Validates the dynamic attribute values of a product or variant against the organization's schema. Pure, so every rule is
/// unit-tested; the server never trusts a client's idea of which attributes exist or which options are valid.
/// </summary>
public static class AttributeValueValidator
{
    /// <summary>
    /// Checks <paramref name="values"/> against <paramref name="rules"/> (the attributes that apply to this owner: right scope
    /// and category). Unknown, duplicated, retired, mistyped and missing-required values are all reported, not just the first.
    /// </summary>
    public static AttributeValidation Validate(IReadOnlyCollection<AttributeRule> rules, IReadOnlyCollection<AttributeValueCommand>? values)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var problems = new List<string>();
        var stored = new List<(AttributeRule, AttributeValueInput)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values ?? [])
        {
            var code = value.Code?.Trim();
            if (string.IsNullOrEmpty(code))
            {
                problems.Add("Every attribute value needs the attribute's code.");
                continue;
            }

            if (!seen.Add(code))
            {
                problems.Add($"Attribute '{code}' is given more than once.");
                continue;
            }

            var rule = rules.FirstOrDefault(candidate => string.Equals(candidate.Code, code, StringComparison.OrdinalIgnoreCase));
            if (rule is null)
            {
                problems.Add($"Attribute '{code}' does not exist or does not apply here.");
                continue;
            }

            if (!rule.IsActive)
            {
                problems.Add($"Attribute '{rule.Name}' is retired and cannot be set.");
                continue;
            }

            CheckValue(rule, value, problems, stored);
        }

        foreach (var rule in rules.Where(rule => rule.IsRequired && rule.IsActive && !seen.Contains(rule.Code)))
        {
            problems.Add($"Attribute '{rule.Name}' is required.");
        }

        return new AttributeValidation(problems, stored);
    }

    private static void CheckValue(AttributeRule rule, AttributeValueCommand value, List<string> problems, List<(AttributeRule, AttributeValueInput)> stored)
    {
        var name = rule.Name;
        var optionIds = (value.OptionIds ?? []).Concat(value.OptionId is { } single ? [single] : []).Distinct().ToList();
        var given = new Dictionary<string, bool>
        {
            ["text"] = value.Text is not null,
            ["number"] = value.Number is not null,
            ["date"] = value.Date is not null,
            ["boolean"] = value.Boolean is not null,
            ["option"] = optionIds.Count > 0,
        };
        var expected = rule.DataType switch
        {
            AttributeDataType.Text => "text",
            AttributeDataType.Number or AttributeDataType.Decimal => "number",
            AttributeDataType.Date => "date",
            AttributeDataType.Boolean => "boolean",
            _ => "option",
        };

        if (!given[expected])
        {
            problems.Add($"Attribute '{name}' needs {Describe(rule.DataType)}.");
            return;
        }

        if (given.Any(pair => pair.Key != expected && pair.Value))
        {
            problems.Add($"Attribute '{name}' accepts only {Describe(rule.DataType)}.");
            return;
        }

        switch (rule.DataType)
        {
            case AttributeDataType.Text when string.IsNullOrWhiteSpace(value.Text):
                problems.Add($"Attribute '{name}' needs a text.");
                break;
            case AttributeDataType.Text when value.Text!.Trim().Length > AttributeValueBase.TextMaxLength:
                problems.Add($"Attribute '{name}' cannot be longer than {AttributeValueBase.TextMaxLength} characters.");
                break;
            case AttributeDataType.Text:
                stored.Add((rule, new AttributeValueInput(Text: value.Text)));
                break;
            case AttributeDataType.Number when value.Number != decimal.Truncate(value.Number!.Value):
                problems.Add($"Attribute '{name}' needs a whole number.");
                break;
            case AttributeDataType.Number or AttributeDataType.Decimal when Math.Abs(value.Number!.Value) > Domain.Common.DomainGuard.MaxAmount:
                problems.Add($"Attribute '{name}' is too large.");
                break;
            case AttributeDataType.Number or AttributeDataType.Decimal:
                stored.Add((rule, new AttributeValueInput(Number: value.Number)));
                break;
            case AttributeDataType.Date:
                stored.Add((rule, new AttributeValueInput(Date: value.Date)));
                break;
            case AttributeDataType.Boolean:
                stored.Add((rule, new AttributeValueInput(Boolean: value.Boolean)));
                break;
            case AttributeDataType.Dropdown when optionIds.Count != 1:
                problems.Add($"Attribute '{name}' takes exactly one option.");
                break;
            default:
                if (optionIds.Any(id => !rule.ActiveOptionIds.Contains(id)))
                {
                    problems.Add($"Attribute '{name}' was given an option that does not exist for it or is retired.");
                    break;
                }

                stored.AddRange(optionIds.Select(id => (rule, new AttributeValueInput(OptionId: id))));
                break;
        }
    }

    private static string Describe(AttributeDataType type) => type switch
    {
        AttributeDataType.Text => "a text",
        AttributeDataType.Number => "a whole number",
        AttributeDataType.Decimal => "a number",
        AttributeDataType.Date => "a date",
        AttributeDataType.Boolean => "yes or no (boolean)",
        AttributeDataType.Dropdown => "one option (optionId)",
        _ => "one or more options (optionIds)",
    };
}
