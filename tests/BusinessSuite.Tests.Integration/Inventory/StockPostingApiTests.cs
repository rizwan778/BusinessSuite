using System.Net;
using System.Text.Json;
using BusinessSuite.Infrastructure.Persistence;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.EntityFrameworkCore;

namespace BusinessSuite.Tests.Integration.Inventory;

/// <summary>
/// Stock adjustments and transfers end to end on SQL Server: one transaction per posting, balances and ledger that always add
/// up, protection against concurrent and repeated posting, immutability of what was posted, and server-side validation.
/// </summary>
public sealed class StockPostingApiTests(SqlServerAppFixture app) : IClassFixture<SqlServerAppFixture>
{
    private readonly InventoryHarness _harness = new(app);

    // ---- Posting an adjustment ------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Posting_an_adjustment_changes_balances_ledger_and_status_together_and_is_audited()
    {
        var scene = await _harness.CreateSceneAsync();
        var (a, b) = (scene.Products[0], scene.Products[1]);

        var draft = await _harness.CreateAdjustmentAsync(scene.Owner, scene.WarehouseId, (a, 10, 5m), (b, 4, null));
        var id = draft.GetProperty("id").GetGuid();
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.Equal(0, await _harness.OnHandAsync(scene.WarehouseId, a)); // a draft changes no stock
        Assert.Equal(0, await _harness.MovementCountAsync(scene.OrganizationId));

        using var post = await _harness.SendAsync(HttpMethod.Post, $"/api/v1/stock-adjustments/{id}/post", scene.Owner);
        var posted = await InventoryHarness.JsonAsync(post);

        Assert.Equal("Posted", posted.GetProperty("status").GetString());
        Assert.All(posted.GetProperty("items").EnumerateArray(), item => Assert.NotEqual(JsonValueKind.Null, item.GetProperty("stockMovementId").ValueKind));
        Assert.Equal(10, await _harness.OnHandAsync(scene.WarehouseId, a));
        Assert.Equal(4, await _harness.OnHandAsync(scene.WarehouseId, b));
        Assert.Equal(2, await _harness.MovementCountAsync(scene.OrganizationId));

        // The movement history reads back what happened.
        var history = await _harness.GetAsync($"/api/v1/stock-movements?productId={a}", scene.Owner);
        var movement = Assert.Single(history.GetProperty("items").EnumerateArray());
        Assert.Equal("AdjustmentIncrease", movement.GetProperty("type").GetString());
        Assert.Equal(10, movement.GetProperty("quantity").GetDecimal());
        Assert.Equal(10, movement.GetProperty("quantityAfter").GetDecimal());
        Assert.Equal(5, movement.GetProperty("unitCost").GetDecimal());
        Assert.Equal(scene.Owner.UserId, movement.GetProperty("recordedByUserId").GetGuid());

        // Who did it is on the record, in the same transaction as the change.
        Assert.Equal(1, await _harness.AuditCountAsync(id, "stock_adjustment.created"));
        Assert.Equal(1, await _harness.AuditCountAsync(id, "stock_adjustment.posted"));
        await _harness.AssertLedgerMatchesBalancesAsync(scene.OrganizationId);
    }

    [SqlServerFact]
    public async Task A_document_that_cannot_be_applied_in_full_changes_nothing()
    {
        var scene = await _harness.CreateSceneAsync();
        var (a, b) = (scene.Products[0], scene.Products[1]);

        // First line is fine, second removes stock that is not there.
        var id = (await _harness.CreateAdjustmentAsync(scene.Owner, scene.WarehouseId, (a, 5, 2m), (b, -3, null))).GetProperty("id").GetGuid();
        var (status, code) = await _harness.TryAsync(HttpMethod.Post, $"/api/v1/stock-adjustments/{id}/post", scene.Owner);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("Stock.InsufficientStock", code);
        Assert.Equal(0, await _harness.OnHandAsync(scene.WarehouseId, a)); // the first line was rolled back with the rest
        Assert.Equal(0, await _harness.MovementCountAsync(scene.OrganizationId));
        Assert.Equal(0, await _harness.AuditCountAsync(id, "stock_adjustment.posted"));
        Assert.Equal("Draft", (await _harness.GetAsync($"/api/v1/stock-adjustments/{id}", scene.Owner)).GetProperty("status").GetString());
        await _harness.AssertLedgerMatchesBalancesAsync(scene.OrganizationId);
    }

    [SqlServerFact]
    public async Task Posting_the_same_document_again_is_refused_and_applies_it_only_once()
    {
        var scene = await _harness.CreateSceneAsync();
        var a = scene.Products[0];
        var id = await _harness.PostAdjustmentAsync(scene.Owner, scene.WarehouseId, (a, 10, 5m));

        var (status, code) = await _harness.TryAsync(HttpMethod.Post, $"/api/v1/stock-adjustments/{id}/post", scene.Owner);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("Adjustments.NotDraft", code);
        Assert.Equal(10, await _harness.OnHandAsync(scene.WarehouseId, a)); // not 20
        Assert.Equal(1, await _harness.MovementCountAsync(scene.OrganizationId));
        Assert.Equal(1, await _harness.AuditCountAsync(id, "stock_adjustment.posted"));
        await _harness.AssertLedgerMatchesBalancesAsync(scene.OrganizationId);
    }

    [SqlServerFact]
    public async Task Concurrent_posts_of_one_document_apply_it_exactly_once()
    {
        var scene = await _harness.CreateSceneAsync();
        var a = scene.Products[0];
        var id = (await _harness.CreateAdjustmentAsync(scene.Owner, scene.WarehouseId, (a, 10, 5m))).GetProperty("id").GetGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            _harness.TryAsync(HttpMethod.Post, $"/api/v1/stock-adjustments/{id}/post", scene.Owner)));

        Assert.Equal(1, results.Count(result => result.Code is null));
        Assert.All(results.Where(result => result.Code is not null), result =>
        {
            Assert.Equal(HttpStatusCode.Conflict, result.Status);
            Assert.Contains(result.Code, new[] { "Adjustments.NotDraft", "Stock.ConcurrentChange" });
        });
        Assert.Equal(10, await _harness.OnHandAsync(scene.WarehouseId, a));
        Assert.Equal(1, await _harness.MovementCountAsync(scene.OrganizationId));
        Assert.Equal(1, await _harness.AuditCountAsync(id, "stock_adjustment.posted"));
        await _harness.AssertLedgerMatchesBalancesAsync(scene.OrganizationId);
    }

    [SqlServerFact]
    public async Task Concurrent_removals_of_the_same_stock_lose_no_update_and_the_stock_rule_still_holds_on_retry()
    {
        var scene = await _harness.CreateSceneAsync();
        var a = scene.Products[0];
        await _harness.PostAdjustmentAsync(scene.Owner, scene.WarehouseId, (a, 10, 5m));

        // Five separate documents each take 4 from a stock of 10, all posted at once: at most two can fit.
        var ids = new List<Guid>();
        for (var number = 0; number < 5; number++)
        {
            ids.Add((await _harness.CreateAdjustmentAsync(scene.Owner, scene.WarehouseId, (a, -4, null))).GetProperty("id").GetGuid());
        }

        var results = await Task.WhenAll(ids.Select(id => _harness.TryAsync(HttpMethod.Post, $"/api/v1/stock-adjustments/{id}/post", scene.Owner)));

        var successes = results.Count(result => result.Code is null);
        Assert.InRange(successes, 1, 2);
        Assert.All(results.Where(result => result.Code is not null), result =>
        {
            Assert.Equal(HttpStatusCode.Conflict, result.Status);
            Assert.Contains(result.Code, new[] { "Stock.InsufficientStock", "Stock.ConcurrentChange" });
        });
        // No update was lost: what is on hand is exactly what the successful documents left.
        Assert.Equal(10 - (4 * successes), await _harness.OnHandAsync(scene.WarehouseId, a));

        // The burst may refuse documents that would have fitted (they lost a race). Retrying them one at a time, as a client
        // would, makes the STOCK rule the only thing that can stop them: 10 holds two removals of 4, never three.
        var retries = new List<(HttpStatusCode Status, string? Code)>();
        for (var index = 0; index < ids.Count; index++)
        {
            if (results[index].Code is not null)
            {
                retries.Add(await _harness.TryAsync(HttpMethod.Post, $"/api/v1/stock-adjustments/{ids[index]}/post", scene.Owner));
            }
        }

        Assert.Equal(2, successes + retries.Count(retry => retry.Code is null));
        Assert.All(retries.Where(retry => retry.Code is not null), retry => Assert.Equal((HttpStatusCode.Conflict, "Stock.InsufficientStock"), retry));
        Assert.Equal(2, await _harness.OnHandAsync(scene.WarehouseId, a));
        await _harness.AssertLedgerMatchesBalancesAsync(scene.OrganizationId);
    }

    [SqlServerFact]
    public async Task A_cancelled_draft_cannot_be_posted_and_a_posted_one_cannot_be_cancelled()
    {
        var scene = await _harness.CreateSceneAsync();
        var a = scene.Products[0];
        var draftId = (await _harness.CreateAdjustmentAsync(scene.Owner, scene.WarehouseId, (a, 3, null))).GetProperty("id").GetGuid();

        using var cancelled = await _harness.SendAsync(HttpMethod.Post, $"/api/v1/stock-adjustments/{draftId}/cancel", scene.Owner);
        Assert.Equal("Cancelled", (await InventoryHarness.JsonAsync(cancelled)).GetProperty("status").GetString());
        var (postStatus, postCode) = await _harness.TryAsync(HttpMethod.Post, $"/api/v1/stock-adjustments/{draftId}/post", scene.Owner);

        var postedId = await _harness.PostAdjustmentAsync(scene.Owner, scene.WarehouseId, (a, 7, null));
        var (cancelStatus, cancelCode) = await _harness.TryAsync(HttpMethod.Post, $"/api/v1/stock-adjustments/{postedId}/cancel", scene.Owner);

        Assert.Equal((HttpStatusCode.Conflict, "Adjustments.NotDraft"), (postStatus, postCode));
        Assert.Equal((HttpStatusCode.Conflict, "Adjustments.NotDraft"), (cancelStatus, cancelCode));
        Assert.Equal(7, await _harness.OnHandAsync(scene.WarehouseId, a)); // only the posted one counts
        Assert.Equal(1, await _harness.AuditCountAsync(draftId, "stock_adjustment.cancelled"));
        await _harness.AssertLedgerMatchesBalancesAsync(scene.OrganizationId);
    }

    // ---- Transfers --------------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task A_transfer_moves_stock_between_warehouses_atomically_and_a_refused_one_changes_nothing()
    {
        var scene = await _harness.CreateSceneAsync();
        var (a, b) = (scene.Products[0], scene.Products[1]);
        var second = await _harness.CreateWarehouseAsync(scene.Owner, "SECOND");
        await _harness.PostAdjustmentAsync(scene.Owner, scene.WarehouseId, (a, 10, 5m));

        var transferId = (await _harness.CreateTransferAsync(scene.Owner, scene.WarehouseId, second, (a, 4))).GetProperty("id").GetGuid();
        using var posted = await _harness.SendAsync(HttpMethod.Post, $"/api/v1/stock-transfers/{transferId}/post", scene.Owner);
        var transfer = await InventoryHarness.JsonAsync(posted);

        Assert.Equal("Received", transfer.GetProperty("status").GetString());
        Assert.Equal(6, await _harness.OnHandAsync(scene.WarehouseId, a));
        Assert.Equal(4, await _harness.OnHandAsync(second, a));
        var item = Assert.Single(transfer.GetProperty("items").EnumerateArray());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("dispatchMovementId").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("receiptMovementId").ValueKind);
        Assert.Equal(5, item.GetProperty("unitCost").GetDecimal()); // the stock keeps the cost it left with
        Assert.Equal(1, await _harness.AuditCountAsync(transferId, "stock_transfer.created"));
        Assert.Equal(1, await _harness.AuditCountAsync(transferId, "stock_transfer.posted"));

        // Second transfer: the first line is available, the second product has nothing at the source.
        var refusedId = (await _harness.CreateTransferAsync(scene.Owner, scene.WarehouseId, second, (a, 1), (b, 1))).GetProperty("id").GetGuid();
        var (status, code) = await _harness.TryAsync(HttpMethod.Post, $"/api/v1/stock-transfers/{refusedId}/post", scene.Owner);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("Stock.InsufficientStock", code);
        Assert.Equal(6, await _harness.OnHandAsync(scene.WarehouseId, a)); // neither side moved
        Assert.Equal(4, await _harness.OnHandAsync(second, a));
        Assert.Equal("Draft", (await _harness.GetAsync($"/api/v1/stock-transfers/{refusedId}", scene.Owner)).GetProperty("status").GetString());
        Assert.Equal(0, await _harness.AuditCountAsync(refusedId, "stock_transfer.posted"));
        await _harness.AssertLedgerMatchesBalancesAsync(scene.OrganizationId);
    }

    [SqlServerFact]
    public async Task Concurrent_posts_of_one_transfer_move_the_stock_once()
    {
        var scene = await _harness.CreateSceneAsync();
        var a = scene.Products[0];
        var second = await _harness.CreateWarehouseAsync(scene.Owner, "SECOND");
        await _harness.PostAdjustmentAsync(scene.Owner, scene.WarehouseId, (a, 10, 5m));
        var transferId = (await _harness.CreateTransferAsync(scene.Owner, scene.WarehouseId, second, (a, 4))).GetProperty("id").GetGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            _harness.TryAsync(HttpMethod.Post, $"/api/v1/stock-transfers/{transferId}/post", scene.Owner)));

        Assert.Equal(1, results.Count(result => result.Code is null));
        Assert.Equal(6, await _harness.OnHandAsync(scene.WarehouseId, a));
        Assert.Equal(4, await _harness.OnHandAsync(second, a));
        await _harness.AssertLedgerMatchesBalancesAsync(scene.OrganizationId);
    }

    // ---- Immutability: what was posted stays as it was, even against direct SQL -------------------------------

    [SqlServerFact]
    public async Task Posted_adjustments_and_the_ledger_cannot_be_changed_even_directly_in_the_database()
    {
        var scene = await _harness.CreateSceneAsync();
        var (a, b) = (scene.Products[0], scene.Products[1]);
        var id = await _harness.PostAdjustmentAsync(scene.Owner, scene.WarehouseId, (a, 10, 5m));
        var organizationId = scene.OrganizationId;

        // The ledger is append-only.
        Assert.Equal(51010, await _harness.RefusedWithAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [inventory].[StockMovements] SET [Notes] = N'tampered' WHERE [OrganizationId] = {organizationId}")));
        Assert.Equal(51010, await _harness.RefusedWithAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM [inventory].[StockMovements] WHERE [OrganizationId] = {organizationId}")));

        // A posted document and its lines are final.
        Assert.Equal(51013, await _harness.RefusedWithAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [inventory].[StockAdjustments] SET [Notes] = N'tampered' WHERE [Id] = {id}")));
        Assert.Equal(51013, await _harness.RefusedWithAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [inventory].[StockAdjustments] SET [Status] = 1 WHERE [Id] = {id}"))); // cannot be re-opened
        Assert.Equal(51011, await _harness.RefusedWithAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM [inventory].[StockAdjustments] WHERE [Id] = {id}")));
        Assert.Equal(51015, await _harness.RefusedWithAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [inventory].[StockAdjustmentItems] SET [QuantityDifference] = 999 WHERE [StockAdjustmentId] = {id}")));
        Assert.Equal(51015, await _harness.RefusedWithAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [inventory].[StockAdjustmentItems] SET [StockMovementId] = NULL WHERE [StockAdjustmentId] = {id}")));
        Assert.Equal(51015, await _harness.RefusedWithAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM [inventory].[StockAdjustmentItems] WHERE [StockAdjustmentId] = {id}")));
        Assert.Equal(51015, await _harness.RefusedWithAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [inventory].[StockAdjustmentItems] ([Id],[OrganizationId],[StockAdjustmentId],[ProductId],[QuantityDifference],[CreatedAtUtc])
            VALUES (NEWID(), {organizationId}, {id}, {b}, 1, SYSDATETIMEOFFSET())
            """)));

        // Nothing moved, and the application's own reading agrees.
        Assert.Equal(10, await _harness.OnHandAsync(scene.WarehouseId, a));
        Assert.Equal(1, await _harness.MovementCountAsync(organizationId));
        var document = await _harness.GetAsync($"/api/v1/stock-adjustments/{id}", scene.Owner);
        Assert.Equal("Posted", document.GetProperty("status").GetString());
        Assert.Equal(10, document.GetProperty("items")[0].GetProperty("quantityDifference").GetDecimal());
        await _harness.AssertLedgerMatchesBalancesAsync(organizationId);
    }

    [SqlServerFact]
    public async Task Posted_transfers_cannot_be_changed_even_directly_in_the_database()
    {
        var scene = await _harness.CreateSceneAsync();
        var a = scene.Products[0];
        var second = await _harness.CreateWarehouseAsync(scene.Owner, "SECOND");
        await _harness.PostAdjustmentAsync(scene.Owner, scene.WarehouseId, (a, 10, 5m));
        var transferId = (await _harness.CreateTransferAsync(scene.Owner, scene.WarehouseId, second, (a, 4))).GetProperty("id").GetGuid();
        using var posted = await _harness.SendAsync(HttpMethod.Post, $"/api/v1/stock-transfers/{transferId}/post", scene.Owner);
        await InventoryHarness.JsonAsync(posted);

        Assert.Equal(51014, await _harness.RefusedWithAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [inventory].[StockTransfers] SET [Notes] = N'tampered' WHERE [Id] = {transferId}")));
        Assert.Equal(51014, await _harness.RefusedWithAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [inventory].[StockTransfers] SET [Status] = 1 WHERE [Id] = {transferId}")));
        Assert.Equal(51012, await _harness.RefusedWithAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM [inventory].[StockTransfers] WHERE [Id] = {transferId}")));
        Assert.Equal(51016, await _harness.RefusedWithAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [inventory].[StockTransferItems] SET [Quantity] = 999 WHERE [StockTransferId] = {transferId}")));
        Assert.Equal(51016, await _harness.RefusedWithAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [inventory].[StockTransferItems] SET [UnitCost] = 1 WHERE [StockTransferId] = {transferId}"))); // set once at posting
        Assert.Equal(51016, await _harness.RefusedWithAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM [inventory].[StockTransferItems] WHERE [StockTransferId] = {transferId}")));

        Assert.Equal(6, await _harness.OnHandAsync(scene.WarehouseId, a));
        Assert.Equal(4, await _harness.OnHandAsync(second, a));
        await _harness.AssertLedgerMatchesBalancesAsync(scene.OrganizationId);
    }

    // ---- Duplicate posting at the ledger -----------------------------------------------------------------------

    [SqlServerFact]
    public async Task A_source_line_cannot_post_the_same_movement_twice_but_may_post_a_different_kind()
    {
        var scene = await _harness.CreateSceneAsync();
        var a = scene.Products[0];
        var (organizationId, warehouseId, source, line) = (scene.OrganizationId, scene.WarehouseId, Guid.NewGuid(), Guid.NewGuid());

        Task Insert(AppDbContext db, int type, int quantity) => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [inventory].[StockMovements] ([Id],[OrganizationId],[WarehouseId],[ProductId],[Type],[Quantity],[QuantityAfter],[OccurredAtUtc],[SourceType],[SourceId],[SourceLineId],[CreatedAtUtc])
            VALUES (NEWID(), {organizationId}, {warehouseId}, {a}, {type}, {quantity}, 0, SYSDATETIMEOFFSET(), N'SalesInvoice', {source}, {line}, SYSDATETIMEOFFSET())
            """);

        await _harness.WithDbAsync(async db =>
        {
            await Insert(db, type: 3, quantity: -1); // an Issue for this invoice line
            return 0;
        });

        // The same line, same type, warehouse and product again: refused by the unique index.
        Assert.Equal(2601, await _harness.RefusedWithAsync(db => Insert(db, type: 3, quantity: -1)));

        // A different kind of movement for the line (a return) is a different fact and is allowed.
        await _harness.WithDbAsync(async db =>
        {
            await Insert(db, type: 8, quantity: 1);
            return 0;
        });
        Assert.Equal(2, await _harness.WithDbAsync(db => db.StockMovements.AsNoTracking().CountAsync(movement => movement.SourceId == source)));
    }


    // ---- Server-side validation ---------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Invalid_documents_are_refused_by_the_server_before_anything_is_saved()
    {
        var scene = await _harness.CreateSceneAsync();
        var a = scene.Products[0];
        var owner = scene.Owner;
        string Path() => "/api/v1/stock-adjustments";
        object Body(Guid warehouse, params object[] items) => new { warehouseId = warehouse, reason = "StockCount", items };

        // A unit counted in whole items refuses 2.5; zero is no change at all.
        Assert.Equal((HttpStatusCode.BadRequest, "Stock.ItemInvalid"),
            await _harness.TryAsync(HttpMethod.Post, Path(), owner, Body(scene.WarehouseId, new { productId = a, quantityDifference = 2.5m })));
        Assert.Equal((HttpStatusCode.BadRequest, "Stock.ItemInvalid"),
            await _harness.TryAsync(HttpMethod.Post, Path(), owner, Body(scene.WarehouseId, new { productId = a, quantityDifference = 0m })));

        // Nothing to adjust, an unknown warehouse, an unknown product, the same product twice.
        Assert.Equal(HttpStatusCode.BadRequest, (await _harness.TryAsync(HttpMethod.Post, Path(), owner, Body(scene.WarehouseId))).Status);
        Assert.Equal((HttpStatusCode.NotFound, "Warehouses.NotFound"),
            await _harness.TryAsync(HttpMethod.Post, Path(), owner, Body(Guid.NewGuid(), new { productId = a, quantityDifference = 1m })));
        Assert.Equal((HttpStatusCode.BadRequest, "Stock.ItemInvalid"),
            await _harness.TryAsync(HttpMethod.Post, Path(), owner, Body(scene.WarehouseId, new { productId = Guid.NewGuid(), quantityDifference = 1m })));
        Assert.Equal((HttpStatusCode.BadRequest, "Adjustments.Invalid"),
            await _harness.TryAsync(HttpMethod.Post, Path(), owner, Body(scene.WarehouseId,
                new { productId = a, quantityDifference = 1m }, new { productId = a, quantityDifference = 2m })));

        // A retired product and a retired warehouse take no new stock.
        using (var retire = await _harness.SendAsync(HttpMethod.Patch, $"/api/v1/products/{a}/status", owner, new { isActive = false }))
        {
            await InventoryHarness.JsonAsync(retire);
        }

        Assert.Equal((HttpStatusCode.BadRequest, "Stock.ItemInvalid"),
            await _harness.TryAsync(HttpMethod.Post, Path(), owner, Body(scene.WarehouseId, new { productId = a, quantityDifference = 1m })));

        var spare = await _harness.CreateWarehouseAsync(owner, "SPARE");
        var other = scene.Products[1];
        using (var retire = await _harness.SendAsync(HttpMethod.Patch, $"/api/v1/warehouses/{spare}/status", owner, new { isActive = false }))
        {
            await InventoryHarness.JsonAsync(retire);
        }

        Assert.Equal((HttpStatusCode.BadRequest, "Warehouses.Inactive"),
            await _harness.TryAsync(HttpMethod.Post, Path(), owner, Body(spare, new { productId = other, quantityDifference = 1m })));

        // Two different warehouses are needed to transfer.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _harness.TryAsync(HttpMethod.Post, "/api/v1/stock-transfers", owner, new
            {
                fromWarehouseId = scene.WarehouseId, toWarehouseId = scene.WarehouseId, items = new[] { new { productId = other, quantity = 1m } },
            })).Status);

        Assert.Equal(0, await _harness.MovementCountAsync(scene.OrganizationId));
    }
}
