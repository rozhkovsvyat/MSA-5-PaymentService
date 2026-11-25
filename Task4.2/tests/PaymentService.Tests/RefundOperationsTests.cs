using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Payments;
using Shouldly;
using Xunit;

namespace PaymentService.Tests;

/// <summary>Возврат постфактум на настоящей PostgreSQL и заглушке банка: один возврат на платёж, журнал, долг продавца.</summary>
public sealed class RefundOperationsTests(Postgres postgres) : IClassFixture<Postgres>
{
    private static readonly PaymentSettings Settings = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Only_a_completed_payment_can_be_refunded_and_only_once()
    {
        var stand = await NewStandAsync(new StandInScenario());
        var id = await stand.NewPaymentAsync();
        await Should.ThrowAsync<RefundNotAllowedException>(() => stand.Refunds.CreateAsync(id, Ct));

        await stand.CompleteAsync(id);
        (await stand.Refunds.CreateAsync(id, Ct)).ShouldBeTrue();
        (await stand.Refunds.CreateAsync(id, Ct)).ShouldBeFalse();
        (await stand.Refunds.RefundToClientAsync(id, 1, null, Ct)).Outcome.ShouldBe(RefundOutcome.Done);

        await Should.ThrowAsync<RefundNotAllowedException>(() => stand.Refunds.EnsureRefundableAsync(id, Ct));
    }

    [Fact]
    public async Task Refund_is_owned_by_the_first_process_instance()
    {
        var stand = await NewStandAsync(new StandInScenario());
        var id = await stand.NewPaymentAsync();
        await stand.CompleteAsync(id);
        await stand.Refunds.CreateAsync(id, Ct);

        (await stand.Refunds.SetProcessKeyAsync(id, 501, Ct)).ShouldBe(501);
        (await stand.Refunds.SetProcessKeyAsync(id, 502, Ct)).ShouldBe(501, "второй экземпляр возврата — дубликат");
    }

    [Fact]
    public async Task Repeated_refund_step_takes_the_result_from_the_journal_and_refunds_once()
    {
        var stand = await NewStandAsync(new StandInScenario());
        var id = await stand.RefundRequestedAsync();

        (await stand.Refunds.RefundToClientAsync(id, 1, null, Ct)).ShouldBe((RefundOutcome.Done, false));
        (await stand.Refunds.RefundToClientAsync(id, 1, null, Ct)).ShouldBe((RefundOutcome.Done, true));

        stand.Bank.Refunds.ShouldBe(1);
        (await stand.Refunds.GetAsync(id, Ct))!.Status.ShouldBe(RefundStatus.Refunded);
    }

    [Fact]
    public async Task Lost_refund_response_is_resolved_by_status_without_sending_again()
    {
        var stand = await NewStandAsync(new StandInScenario { Refund = "LOST" });
        var id = await stand.RefundRequestedAsync();

        (await stand.Refunds.RefundToClientAsync(id, 1, null, Ct)).ShouldBe((RefundOutcome.Unknown, false));
        (await stand.Refunds.RefundToClientAsync(id, 1, null, Ct)).ShouldBe((RefundOutcome.Unknown, true));
        (await stand.Refunds.CheckRefundStatusAsync(id, 1, null, Ct)).ShouldBeNull("на первый запрос банк возврат ещё не нашёл");
        (await stand.Refunds.CheckRefundStatusAsync(id, 1, null, Ct)).ShouldBe(RefundResult.Done);

        stand.Bank.Refunds.ShouldBe(1);
        (await stand.Refunds.GetAsync(id, Ct))!.Status.ShouldBe(RefundStatus.Refunded);
    }

    [Fact]
    public async Task Declined_refund_is_sent_again_only_with_new_details_and_a_new_key()
    {
        var stand = await NewStandAsync(new StandInScenario { Refund = "DECLINE" });
        var id = await stand.RefundRequestedAsync();

        (await stand.Refunds.RefundToClientAsync(id, 1, null, Ct)).Outcome.ShouldBe(RefundOutcome.Declined);
        (await stand.Refunds.RefundToClientAsync(id, 1, null, Ct)).ShouldBe((RefundOutcome.Declined, true));
        var next = await stand.Refunds.NextAttemptForNewDetailsAsync(id, Ct);
        (await stand.Refunds.RefundToClientAsync(id, next, null, Ct)).Outcome.ShouldBe(RefundOutcome.Done);

        next.ShouldBe(2);
        stand.Bank.Refunds.ShouldBe(2);
        (await stand.Payments.OperationsAsync(id, Ct)).Where(o => o.Step == RefundOperations.RefundStep).Select(o => o.Result)
            .ShouldBe(["Declined", "Done"]);
    }

    [Fact]
    public async Task Unexecuted_refund_is_sent_again_with_the_same_key_and_needs_an_operator_after_the_limit()
    {
        var stand = await NewStandAsync(new StandInScenario { RefundNotReceived = 3 }, new PaymentSettings(TimeSpan.FromMilliseconds(50), TimeSpan.Zero));
        var id = await stand.RefundRequestedAsync();

        for (var send = 1; send <= 3; send++)
        {
            (await stand.Refunds.RefundToClientAsync(id, 1, null, Ct)).Outcome.ShouldBe(RefundOutcome.Unknown);
            await Task.Delay(TimeSpan.FromMilliseconds(150), Ct);
            (await stand.Refunds.CheckRefundStatusAsync(id, 1, null, Ct)).ShouldBe(RefundResult.NotExecuted);
            // повторно выданная задача не считает ту же отправку второй раз
            (await stand.Refunds.CheckRefundStatusAsync(id, 1, null, Ct)).ShouldBe(RefundResult.NotExecuted);
            (await stand.Refunds.UnexecutedLimitReachedAsync(id, Ct)).ShouldBe(send == 3, $"отправка {send}");
        }
        // после инцидента счёт начинается заново, четвёртая отправка доходит до банка
        (await stand.Refunds.UnexecutedLimitReachedAsync(id, Ct)).ShouldBeFalse();
        (await stand.Refunds.RefundToClientAsync(id, 1, null, Ct)).ShouldBe((RefundOutcome.Done, false));

        stand.Bank.Refunds.ShouldBe(4);
        (await stand.Payments.OperationsAsync(id, Ct)).Count(o => o.Step == RefundOperations.RefundStep).ShouldBe(1);
        (await stand.Refunds.GetAsync(id, Ct))!.Status.ShouldBe(RefundStatus.Refunded);
    }

    [Fact]
    public async Task Short_seller_balance_leaves_the_rest_as_debt()
    {
        var stand = await NewStandAsync(new StandInScenario { SellerBalance = 500m });
        var id = await stand.RefundRequestedAsync();
        await stand.Refunds.RefundToClientAsync(id, 1, null, Ct);

        (await stand.Refunds.RecoverFromCounterpartyAsync(id, Ct)).ShouldBe((new RecoveryResult(500m, 1000m), false));
        (await stand.Refunds.RecoverFromCounterpartyAsync(id, Ct)).ShouldBe((new RecoveryResult(500m, 1000m), true));

        stand.Bank.Debits.ShouldBe(1);
        var refund = (await stand.Refunds.GetAsync(id, Ct))!;
        refund.RecoveryStatus.ShouldBe(RecoveryStatus.DebtRecorded);
        refund.DebtAmount.ShouldBe(1000m);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Stand> NewStandAsync(StandInScenario scenario, PaymentSettings? settings = null)
    {
        var db = await postgres.NewDatabaseAsync();
        var scenarios = new StandInScenarios();
        var bank = new CountingBank(new BankStandIn(scenarios, NullLogger<BankStandIn>.Instance));
        var store = new PaymentStore(db);
        return new Stand(new PaymentOperations(store, bank, Settings), new RefundOperations(store, new RefundStore(db), bank, settings ?? Settings),
            bank, scenarios, scenario);
    }

    private sealed record Stand(PaymentOperations Payments, RefundOperations Refunds, CountingBank Bank, StandInScenarios Scenarios, StandInScenario Scenario)
    {
        public async Task<string> NewPaymentAsync()
        {
            var id = Guid.NewGuid().ToString();
            Scenarios.Remember(id, Scenario);
            await Payments.CreateAsync(new NewPayment(id, "client-1", "seller-1", 1500m, "RUB", DateTimeOffset.UtcNow), Ct);
            return id;
        }

        public async Task CompleteAsync(string id)
        {
            await Payments.HoldFundsAsync(id, Ct);
            await Payments.MarkChecksStartedAsync(id, Ct);
            (await Payments.TransferAsync(id, null, Ct)).Outcome.ShouldBe(TransferOutcome.Done);
        }

        public async Task<string> RefundRequestedAsync()
        {
            var id = await NewPaymentAsync();
            await CompleteAsync(id);
            await Refunds.CreateAsync(id, Ct);
            return id;
        }
    }
}
