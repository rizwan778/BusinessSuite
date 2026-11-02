using System.Net;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Domain.Inventory;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Persistence;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BusinessSuite.Tests.Integration.Inventory;

/// <summary>
/// <see cref="IStockPostingService"/>, the seam Sales and Purchases will use: the same rules as stock documents, idempotent per source
/// line, joining the caller's transaction, and corrected by reversal rather than by editing history. Called here the way another
/// module would: as the current user, inside a request scope.
/// </summary>
public sealed class StockPostingServiceTests(SqlServerAppFixture app) : IClassFixture<SqlServerAppFixture>
{
    private readonly InventoryHarness _harness = new(app);

    private static StockPostingRequest Receipt(Guid sourceId, Guid warehouse, Guid product, decimal quantity, decimal? cost = null, Guid? line = null, string sourceType = "PurchaseReceipt") =>
        new(sourceType, sourceId, warehouse, StockPostingKind.Receipt, [new StockPostingLine(line ?? Guid.NewGuid(), product, null, null, quantity, cost)], "GRN-1");

    private static StockPostingRequest Issue(Guid sourceId, Guid warehouse, Guid product, decimal quantity, Guid? line = null, string sourceType = "SalesInvoice") =>
        new(sourceType, sourceId, warehouse, StockPostingKind.Issue, [new StockPostingLine(line ?? Guid.NewGuid(), product, null, null, quantity)], "INV-1");

    private Task<Result<StockPostingResult>> PostAsync(InventoryHarness.Scene scene, StockPostingRequest request) =>
        _harness.RunAsAsync(scene.Owner, scene.OrganizationId, services => services.GetRequiredService<IStockPostingService>().PostAsync(request));

    private Task<Result<StockPostingResult>> ReverseAsync(InventoryHarness.Scene scene, string sourceType, Guid sourceId) =>
        _harness.RunAsAsync(scene.Owner, scene.OrganizationId, services => services.GetRequiredService<IStockPostingService>().ReverseAsync(sourceType, sourceId));

    private static void AssertFailed(Result result, string code)
    {
        Assert.True(result.IsFailure, "Expected a failure but the call succeeded.");
        Assert.Equal(code, result.Error.Code);
    }

    // ---- Posting ------------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Receipts_and_issues_follow_the_same_rules_as_stock_documents()
    {
        var scene = await _harness.CreateSceneAsync();
        var product = scene.Products[0];

        var received = await PostAsync(scene, Receipt(Guid.NewGuid(), scene.WarehouseId, product, 10, cost: 5m));
        Assert.True(received.IsSuccess, received.Error.Code);
        var receipt = Assert.Single(received.Value.Movements);
        Assert.Equal((StockMovementType.Receipt, 10m, 10m, 5m), (receipt.Type, receipt.Quantity, receipt.QuantityAfter, receipt.UnitCost));

        var issued = await PostAsync(scene, Issue(Guid.NewGuid(), scene.WarehouseId, product, 4));
        Assert.True(issued.IsSuccess, issued.Error.Code);
        var issue = Assert.Single(issued.Value.Movements);
        Assert.Equal((StockMovementType.Issue, -4m, 6m, 5m), (issue.Type, issue.Quantity, issue.QuantityAfter, issue.UnitCost)); // leaves at the average cost

        Assert.Equal(6, await _harness.OnHandAsync(scene.WarehouseId, product));

        // The history shows where each movement came from.
        var history = await _harness.GetAsync($"/api/v1/stock-movements?productId={product}", scene.Owner);
        var sources = history.GetProperty("items").EnumerateArray().Select(movement => movement.GetProperty("sourceType").GetString()).Order().ToArray();
        Assert.Equal(new[] { "PurchaseReceipt", "SalesInvoice" }, sources);
        await _harness.AssertLedgerMatchesBalancesAsync(scene.OrganizationId);
    }

    [SqlServerFact]
    public async Task Stock_never_goes_negative_through_the_contract_and_a_refused_posting_changes_nothing()
    {
        var scene = await _harness.CreateSceneAsync();
        var (a, b) = (scene.Products[0], scene.Products[1]);
        await PostAsync(scene, Receipt(Guid.NewGuid(), scene.WarehouseId, a, 5, cost: 2m));

        // Two lines: the first fits, the second asks for a product with no stock.
        var request = new StockPostingRequest("SalesInvoice", Guid.NewGuid(), scene.WarehouseId, StockPostingKind.Issue,
            [new StockPostingLine(Guid.NewGuid(), a, null, null, 2), new StockPostingLine(Guid.NewGuid(), b, null, null, 1)]);
        var result = await PostAsync(scene, request);

        AssertFailed(result, "Stock.InsufficientStock");
        Assert.Equal(5, await _harness.OnHandAsync(scene.WarehouseId, a)); // the first line was rolled back too
        Assert.Equal(1, await _harness.MovementCountAsync(scene.OrganizationId));
        await _harness.AssertLedgerMatchesBalancesAsync(scene.OrganizationId);
    }

    [SqlServerFact]
    public async Task A_source_line_posts_once_even_when_the_same_request_arrives_again_or_at_the_same_time()
    {
        var scene = await _harness.CreateSceneAsync();
        var product = scene.Products[0];
        var request = Receipt(Guid.NewGuid(), scene.WarehouseId, product, 10, cost: 5m);

        Assert.True((await PostAsync(scene, request)).IsSuccess);
        var again = await PostAsync(scene, request);
        AssertFailed(again, "Stock.AlreadyPosted");
        Assert.Equal(10, await _harness.OnHandAsync(scene.WarehouseId, product)); // not 20

        // Six copies of a NEW request at the same time: exactly one wins, the others learn why.
        var concurrent = Receipt(Guid.NewGuid(), scene.WarehouseId, product, 3, cost: 5m);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostAsync(scene, concurrent)));

        Assert.Equal(1, results.Count(result => result.IsSuccess));
        Assert.All(results.Where(result => result.IsFailure), result => Assert.Contains(result.Error.Code, new[] { "Stock.AlreadyPosted", "Stock.ConcurrentChange" }));
        Assert.Equal(13, await _harness.OnHandAsync(scene.WarehouseId, product));
        Assert.Equal(2, await _harness.MovementCountAsync(scene.OrganizationId));
        await _harness.AssertLedgerMatchesBalancesAsync(scene.OrganizationId);
    }

    [SqlServerFact]
    public async Task Another_organizations_warehouses_and_products_cannot_be_used()
    {
        var mine = await _harness.CreateSceneAsync();
        var theirs = await _harness.CreateSceneAsync();

        // Their warehouse and product, my identity.
        var foreignWarehouse = await _harness.RunAsAsync(mine.Owner, mine.OrganizationId, services =>
            services.GetRequiredService<IStockPostingService>().PostAsync(Receipt(Guid.NewGuid(), theirs.WarehouseId, mine.Products[0], 5)));
        var foreignProduct = await _harness.RunAsAsync(mine.Owner, mine.OrganizationId, services =>
            services.GetRequiredService<IStockPostingService>().PostAsync(Receipt(Guid.NewGuid(), mine.WarehouseId, theirs.Products[0], 5)));

        AssertFailed(foreignWarehouse, "Warehouses.NotFound");
        AssertFailed(foreignProduct, "Stock.ItemInvalid");
        Assert.Equal(0, await _harness.MovementCountAsync(mine.OrganizationId));
        Assert.Equal(0, await _harness.MovementCountAsync(theirs.OrganizationId));
        Assert.Equal(0, await _harness.OnHandAsync(theirs.WarehouseId, theirs.Products[0]));
    }

    [SqlServerFact]
    public async Task Without_a_selected_organization_nothing_can_be_posted()
    {
        var scene = await _harness.CreateSceneAsync();

        // A current user the server resolved no organization for (no organization claim): the service has no tenant to act in, and the
        // request cannot name one. Even a valid warehouse id of someone else's organization gets nowhere.
        var result = await _harness.RunAsAsync(scene.Owner, organizationId: null, services =>
            services.GetRequiredService<IStockPostingService>().PostAsync(Receipt(Guid.NewGuid(), scene.WarehouseId, scene.Products[0], 1)));
        var reversal = await _harness.RunAsAsync(scene.Owner, organizationId: null, services =>
            services.GetRequiredService<IStockPostingService>().ReverseAsync("SalesInvoice", Guid.NewGuid()));

        AssertFailed(result, "Inventory.OrganizationRequired");
        AssertFailed(reversal, "Inventory.OrganizationRequired");
        Assert.Equal(0, await _harness.MovementCountAsync(scene.OrganizationId));
    }

    // ---- Joining the caller's transaction ---------------------------------------------------------------------

    [SqlServerFact]
    public async Task Staged_stock_commits_with_the_callers_own_change_or_not_at_all()
    {
        var scene = await _harness.CreateSceneAsync();
        var product = scene.Products[0];

        // The caller's own change stands for confirming its document (here: a warehouse rename).
        Task<Result> Confirm(IServiceProvider services, StockPostingRequest request, bool callerStepFails) =>
            DatabaseTransactions.RunAsync<Result>(services.GetRequiredService<AppDbContext>(), async () =>
            {
                var db = services.GetRequiredService<AppDbContext>();
                var staged = await services.GetRequiredService<IStockPostingService>().StageAsync(request);
                if (staged.IsFailure)
                {
                    return staged.Error;
                }

                var warehouse = await db.Warehouses.SingleAsync(candidate => candidate.Id == scene.WarehouseId);
                warehouse.Describe(warehouse.BranchId, warehouse.Code, "Confirmed by the caller", warehouse.Address);
                if (callerStepFails)
                {
                    return Error.Conflict("Caller.Failed", "The caller's own step failed after the stock was staged.");
                }

                await db.SaveChangesAsync();
                return Result.Success();
            }, CancellationToken.None);

        // 1. The caller's step fails after staging: neither the stock nor the caller's change is saved.
        var failed = await _harness.RunAsAsync(scene.Owner, scene.OrganizationId, services =>
            Confirm(services, Receipt(Guid.NewGuid(), scene.WarehouseId, product, 10, cost: 5m), callerStepFails: true));
        AssertFailed(failed, "Caller.Failed");
        Assert.Equal(0, await _harness.OnHandAsync(scene.WarehouseId, product));
        Assert.Equal(0, await _harness.MovementCountAsync(scene.OrganizationId));
        Assert.DoesNotContain("Confirmed", await WarehouseNameAsync(scene));

        // 2. Staging itself is refused (no stock to issue): the caller's change is not saved either.
        var refused = await _harness.RunAsAsync(scene.Owner, scene.OrganizationId, services =>
            Confirm(services, Issue(Guid.NewGuid(), scene.WarehouseId, product, 3), callerStepFails: false));
        AssertFailed(refused, "Stock.InsufficientStock");
        Assert.DoesNotContain("Confirmed", await WarehouseNameAsync(scene));

        // 3. Everything works: stock and the caller's change commit together.
        var confirmed = await _harness.RunAsAsync(scene.Owner, scene.OrganizationId, services =>
            Confirm(services, Receipt(Guid.NewGuid(), scene.WarehouseId, product, 10, cost: 5m), callerStepFails: false));
        Assert.True(confirmed.IsSuccess);
        Assert.Equal(10, await _harness.OnHandAsync(scene.WarehouseId, product));
        Assert.Equal("Confirmed by the caller", await WarehouseNameAsync(scene));
        await _harness.AssertLedgerMatchesBalancesAsync(scene.OrganizationId);
    }

    private Task<string> WarehouseNameAsync(InventoryHarness.Scene scene) =>
        _harness.WithDbAsync(db => db.Warehouses.AsNoTracking().Where(warehouse => warehouse.Id == scene.WarehouseId).Select(warehouse => warehouse.Name).SingleAsync());

    // ---- Reversal ---------------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task A_cancelled_document_is_reversed_with_linked_opposite_movements_once()
    {
        var scene = await _harness.CreateSceneAsync();
        var product = scene.Products[0];
        var (receiptDocument, invoice) = (Guid.NewGuid(), Guid.NewGuid());
        await PostAsync(scene, Receipt(receiptDocument, scene.WarehouseId, product, 10, cost: 5m));
        var issued = (await PostAsync(scene, Issue(invoice, scene.WarehouseId, product, 4))).Value.Movements.Single();

        var reversed = await ReverseAsync(scene, "SalesInvoice", invoice);

        Assert.True(reversed.IsSuccess, reversed.Error.Code);
        var reversal = Assert.Single(reversed.Value.Movements);
        Assert.Equal((StockMovementType.ReversalIncrease, 4m, 10m, 5m), (reversal.Type, reversal.Quantity, reversal.QuantityAfter, reversal.UnitCost));
        Assert.Equal(10, await _harness.OnHandAsync(scene.WarehouseId, product));

        // The history is intact: the original is still there, and the reversal points at it.
        var rows = await _harness.WithDbAsync(db => db.StockMovements.AsNoTracking().Where(movement => movement.OrganizationId == scene.OrganizationId).ToListAsync());
        Assert.Equal(3, rows.Count);
        Assert.Equal(-4, rows.Single(movement => movement.Id == issued.MovementId).Quantity);
        Assert.Equal(issued.MovementId, rows.Single(movement => movement.Id == reversal.MovementId).ReversesMovementId);

        // Once only.
        AssertFailed(await ReverseAsync(scene, "SalesInvoice", invoice), "Stock.AlreadyReversed");
        Assert.Equal(10, await _harness.OnHandAsync(scene.WarehouseId, product));
        await _harness.AssertLedgerMatchesBalancesAsync(scene.OrganizationId);
    }

    [SqlServerFact]
    public async Task A_reversal_that_would_make_stock_negative_is_refused_but_one_for_a_retired_product_is_allowed()
    {
        var scene = await _harness.CreateSceneAsync();
        var product = scene.Products[0];
        var (purchase, sale) = (Guid.NewGuid(), Guid.NewGuid());
        await PostAsync(scene, Receipt(purchase, scene.WarehouseId, product, 10, cost: 5m));
        await PostAsync(scene, Issue(sale, scene.WarehouseId, product, 8)); // 2 left

        // Taking the purchase back would need 10 that are no longer there.
        AssertFailed(await ReverseAsync(scene, "PurchaseReceipt", purchase), "Stock.InsufficientStock");
        Assert.Equal(2, await _harness.OnHandAsync(scene.WarehouseId, product));
        Assert.Equal(2, await _harness.MovementCountAsync(scene.OrganizationId));

        // Cancel the sale first; then the product is retired; the correction must still work.
        Assert.True((await ReverseAsync(scene, "SalesInvoice", sale)).IsSuccess);
        using (var retire = await _harness.SendAsync(HttpMethod.Patch, $"/api/v1/products/{product}/status", scene.Owner, new { isActive = false }))
        {
            await InventoryHarness.JsonAsync(retire);
        }

        var late = await ReverseAsync(scene, "PurchaseReceipt", purchase);
        Assert.True(late.IsSuccess, late.Error.Code);
        Assert.Equal(0, await _harness.OnHandAsync(scene.WarehouseId, product));
        await _harness.AssertLedgerMatchesBalancesAsync(scene.OrganizationId);
    }

    [SqlServerFact]
    public async Task Unknown_internal_and_foreign_documents_cannot_be_reversed()
    {
        var mine = await _harness.CreateSceneAsync();
        var theirs = await _harness.CreateSceneAsync();
        var invoice = Guid.NewGuid();
        await PostAsync(mine, Receipt(Guid.NewGuid(), mine.WarehouseId, mine.Products[0], 10, cost: 5m));
        await PostAsync(mine, Issue(invoice, mine.WarehouseId, mine.Products[0], 4));
        var adjustment = await _harness.PostAdjustmentAsync(mine.Owner, mine.WarehouseId, (mine.Products[1], 3, null));

        AssertFailed(await ReverseAsync(mine, "SalesInvoice", Guid.NewGuid()), "Stock.SourceNotFound");
        AssertFailed(await ReverseAsync(mine, "StockAdjustment", adjustment), "Stock.SourceNotReversible"); // corrected by a new adjustment
        AssertFailed(await ReverseAsync(mine, "bad type!", invoice), "Stock.PostingInvalid");

        // Another organization's identity sees no such document.
        AssertFailed(await ReverseAsync(theirs, "SalesInvoice", invoice), "Stock.SourceNotFound");
        Assert.Equal(6, await _harness.OnHandAsync(mine.WarehouseId, mine.Products[0]));
        await _harness.AssertLedgerMatchesBalancesAsync(mine.OrganizationId);
    }
}
