using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PaymentService.Payments;
using Shouldly;
using Xunit;

namespace PaymentService.Tests;

/// <summary>Денежные операции на настоящей PostgreSQL и заглушке банка: журнал, переходы состояний, перевод, разморозка.</summary>
public sealed class PaymentOperationsTests(Postgres postgres) : IClassFixture<Postgres>
{
    private static readonly PaymentSettings Settings = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Repeated_hold_takes_the_result_from_the_journal_and_does_not_hold_twice()
    {
        var (payments, bank, _, id) = await NewPaymentAsync(new StandInScenario());

        (await payments.HoldFundsAsync(id, Ct)).ShouldBe((HoldResult.Held, false));
        (await payments.HoldFundsAsync(id, Ct)).ShouldBe((HoldResult.Held, true));

        bank.Holds.ShouldBe(1);
        (await payments.OperationsAsync(id, Ct)).Count(o => o.Step == "HOLD_FUNDS").ShouldBe(1);
        (await StatusAsync(payments, id)).ShouldBe(PaymentStatus.FundsHeld);
    }

    [Fact]
    public async Task Replay_restores_a_lagging_status()
    {
        var (payments, _, store, id) = await NewPaymentAsync(new StandInScenario());
        await store.RecordAsync(PaymentOperations.Key(id, PaymentOperations.HoldStep), id, PaymentOperations.HoldStep, "Held", null, Ct);

        (await payments.HoldFundsAsync(id, Ct)).ShouldBe((HoldResult.Held, true));
        (await StatusAsync(payments, id)).ShouldBe(PaymentStatus.FundsHeld);
    }

    [Fact]
    public async Task Terminal_status_is_never_overwritten()
    {
        var (payments, _, _, id) = await NewPaymentAsync(new StandInScenario());
        await payments.RejectAsync(id, "FRAUD", Ct);

        await payments.MarkManualReviewAsync(id, Ct);
        await payments.MarkChecksStartedAsync(id, Ct);

        (await StatusAsync(payments, id)).ShouldBe(PaymentStatus.Rejected);
    }

    [Fact]
    public async Task Started_transfer_is_not_sent_again_after_redelivery()
    {
        var (payments, bank, _, id) = await NewPaymentAsync(new StandInScenario { Transfer = "LOST" });
        await ApproveAsync(payments, id);

        (await payments.TransferAsync(id, null, Ct)).ShouldBe((TransferOutcome.Unknown, false));
        (await payments.TransferAsync(id, null, Ct)).ShouldBe((TransferOutcome.Unknown, true));

        bank.Transfers.ShouldBe(1);
        (await StatusAsync(payments, id)).ShouldBe(PaymentStatus.TransferStatusUnknown);
    }

    [Fact]
    public async Task Release_is_refused_while_the_transfer_outcome_is_unknown_and_after_it_is_done()
    {
        var (payments, bank, _, id) = await NewPaymentAsync(new StandInScenario { Transfer = "LOST" });
        await ApproveAsync(payments, id);
        await payments.TransferAsync(id, null, Ct);

        await Should.ThrowAsync<TransferMayHaveHappenedException>(() => payments.ReleaseFundsAsync(id, null, Ct));
        // на второй запрос банк находит перевод: он выполнен
        await Should.ThrowAsync<TransferCompletedException>(() => payments.ReleaseFundsAsync(id, null, Ct));

        bank.Releases.ShouldBe(0);
        (await StatusAsync(payments, id)).ShouldBe(PaymentStatus.Completed);
    }

    [Fact]
    public async Task Release_settles_the_transfer_with_the_bank_before_unfreezing()
    {
        var (_, bank, store, id) = await NewPaymentAsync(new StandInScenario());
        bank.LoseTransferRequests = true;
        var payments = new PaymentOperations(store, bank, new PaymentSettings(TimeSpan.FromSeconds(1), TimeSpan.Zero));
        await ApproveAsync(payments, id);
        (await payments.TransferAsync(id, null, Ct)).Outcome.ShouldBe(TransferOutcome.Unknown);

        await Should.ThrowAsync<TransferMayHaveHappenedException>(() => payments.ReleaseFundsAsync(id, null, Ct));
        bank.Releases.ShouldBe(0);
        await Task.Delay(TimeSpan.FromSeconds(1.2), Ct);

        (await payments.ReleaseFundsAsync(id, null, Ct)).ShouldBeFalse("срок исполнения истёк, банк перевод не нашёл — не выполнен");
        bank.Releases.ShouldBe(1);
        (await payments.OperationsAsync(id, Ct)).Select(o => $"{o.Step}:{o.Result}")
            .ShouldBe(["HOLD_FUNDS:Held", "TRANSFER_TO_COUNTERPARTY:NotExecuted", "RELEASE_FUNDS:Released"]);
    }

    [Fact]
    public async Task Release_after_a_declined_transfer_unfreezes_the_money()
    {
        var (payments, bank, _, id) = await NewPaymentAsync(new StandInScenario { Transfer = "DECLINE" });
        await ApproveAsync(payments, id);
        (await payments.TransferAsync(id, null, Ct)).Outcome.ShouldBe(TransferOutcome.Declined);

        (await payments.ReleaseFundsAsync(id, null, Ct)).ShouldBeFalse();
        (await payments.ReleaseFundsAsync(id, null, Ct)).ShouldBeTrue();

        bank.Releases.ShouldBe(1);
        (await StatusAsync(payments, id)).ShouldBe(PaymentStatus.ReleasingFunds);
    }

    [Fact]
    public async Task Not_found_transfer_counts_as_not_executed_only_after_deadline_and_clock_skew()
    {
        var (_, bank, store, id) = await NewPaymentAsync(new StandInScenario());
        bank.LoseTransferRequests = true;
        var skewed = new PaymentOperations(store, bank, new PaymentSettings(TimeSpan.FromMilliseconds(100), TimeSpan.FromHours(1)));
        var noSkew = new PaymentOperations(store, bank, new PaymentSettings(TimeSpan.FromMilliseconds(100), TimeSpan.Zero));
        await ApproveAsync(skewed, id);
        (await skewed.TransferAsync(id, null, Ct)).Outcome.ShouldBe(TransferOutcome.Unknown);
        await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);

        (await skewed.CheckTransferStatusAsync(id, null, Ct)).ShouldBeNull("срок исполнения прошёл, запас на расхождение часов ещё нет");
        (await noSkew.CheckTransferStatusAsync(id, null, Ct)).ShouldBe(TransferResult.NotExecuted);
        (await noSkew.ReleaseFundsAsync(id, null, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Cancelled_hold_key_cannot_be_used_again()
    {
        var (payments, bank, _, id) = await NewPaymentAsync(new StandInScenario { Hold = "LOST" });

        (await payments.HoldFundsAsync(id, Ct)).Result.ShouldBe(HoldResult.Cancelled);

        var key = PaymentOperations.Key(id, PaymentOperations.HoldStep);
        (await bank.HoldAsync(id, key, 100m, "RUB", Ct)).ShouldBe(HoldResult.Declined);
    }

    [Fact]
    public async Task Same_request_key_gives_the_same_payment_and_other_content_is_a_conflict()
    {
        var (payments, _, _, _) = await NewPaymentAsync(new StandInScenario());
        var key = Guid.NewGuid().ToString();

        var first = await payments.RegisterRequestAsync(key, 100m, "RUB", "client-1", "seller-1", "payment-a", Ct);
        var repeat = await payments.RegisterRequestAsync(key, 100m, "RUB", "client-1", "seller-1", "payment-b", Ct);
        var changed = await payments.RegisterRequestAsync(key, 999m, "RUB", "client-1", "seller-1", "payment-c", Ct);

        first.ShouldBe(new PaymentRequestRecord("payment-a", null, Created: true, Conflict: false));
        repeat.ShouldBe(new PaymentRequestRecord("payment-a", null, Created: false, Conflict: false));
        changed.Conflict.ShouldBeTrue();
    }

    [Fact]
    public async Task Concurrent_requests_with_one_key_get_one_payment()
    {
        var (payments, _, _, _) = await NewPaymentAsync(new StandInScenario());
        var key = Guid.NewGuid().ToString();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            payments.RegisterRequestAsync(key, 100m, "RUB", "client-1", "seller-1", $"payment-{i}", Ct)));

        results.Select(r => r.PaymentId).Distinct().Count().ShouldBe(1);
        results.Count(r => r.Created).ShouldBe(1);
    }

    [Fact]
    public async Task Process_key_is_recorded_once_and_found_by_payment()
    {
        var (payments, _, _, _) = await NewPaymentAsync(new StandInScenario());
        var key = Guid.NewGuid().ToString();
        await payments.RegisterRequestAsync(key, 100m, "RUB", "client-1", "seller-1", "payment-x", Ct);

        (await payments.SetProcessKeyAsync("payment-x", 111, Ct)).ShouldBe(111);
        (await payments.SetProcessKeyAsync("payment-x", 222, Ct)).ShouldBe(111, "второй экземпляр — дубликат");

        (await payments.FindProcessKeyAsync("payment-x", Ct)).ShouldBe(111);
        (await payments.RegisterRequestAsync(key, 100m, "RUB", "client-1", "seller-1", "payment-y", Ct)).ProcessInstanceKey.ShouldBe(111);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(PaymentOperations Payments, CountingBank Bank, PaymentStore Store, string PaymentId)> NewPaymentAsync(StandInScenario scenario)
    {
        NpgsqlDataSource db = await postgres.NewDatabaseAsync();
        var scenarios = new StandInScenarios();
        var bank = new CountingBank(new BankStandIn(scenarios, NullLogger<BankStandIn>.Instance));
        var store = new PaymentStore(db);
        var payments = new PaymentOperations(store, bank, Settings);
        var id = Guid.NewGuid().ToString();
        scenarios.Remember(id, scenario);
        await payments.CreateAsync(new NewPayment(id, "client-1", "seller-1", 1500m, "RUB", DateTimeOffset.UtcNow), Ct);
        return (payments, bank, store, id);
    }

    private static async Task ApproveAsync(PaymentOperations payments, string id)
    {
        (await payments.HoldFundsAsync(id, Ct)).Result.ShouldBe(HoldResult.Held);
        await payments.MarkChecksStartedAsync(id, Ct);
    }

    private static async Task<string> StatusAsync(PaymentOperations payments, string id) => (await payments.GetAsync(id, Ct))!.Status;
}
