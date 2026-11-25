using PaymentService.Payments;

namespace PaymentService.Process;

/// <summary>Шаги процесса, которые выполняет сам Payment Service: платёж и движение денег.</summary>
public sealed class PaymentSteps(PaymentOperations payments, ProcessSettings settings, ILogger<PaymentSteps> log)
{
    // как часто разморозка переспрашивает банк, пока итог перевода не известен
    private static readonly TimeSpan TransferOutcomePause = TimeSpan.FromSeconds(5);

    [JobType("CREATE_PAYMENT")]
    public async Task<StepResult> CreatePayment(JobContext job, CancellationToken ct)
    {
        var payment = new NewPayment(job.PaymentId, job.String("clientId")!, job.String("counterpartyId")!,
            job.Get<decimal>("amount"), job.String("currency")!, job.Get<DateTimeOffset>("acceptedAt"));
        if (await payments.SetProcessKeyAsync(payment.PaymentId, job.ProcessInstanceKey, ct) is { } owner && owner != job.ProcessInstanceKey)
        {
            log.LogError("{PaymentId} CREATE_PAYMENT: платёж уже ведёт процесс {Owner}, экземпляр {Duplicate} — дубликат, шаги не выполняются",
                payment.PaymentId, owner, job.ProcessInstanceKey);
            return new StepResult.Incident($"платёж {payment.PaymentId} уже ведёт процесс {owner}, этот экземпляр — дубликат");
        }
        var created = await payments.CreateAsync(payment, ct);
        log.LogInformation("{PaymentId} CREATE_PAYMENT: {Result}, {Amount} {Currency}, клиент {ClientId} → продавец {CounterpartyId}",
            payment.PaymentId, created ? "платёж создан" : "платёж уже создан (повтор)", payment.Amount, payment.Currency,
            payment.ClientId, payment.CounterpartyId);
        return new StepResult.Done();
    }

    [JobType("HOLD_FUNDS")]
    public async Task<StepResult> HoldFunds(JobContext job, CancellationToken ct)
    {
        var (result, replayed) = await payments.HoldFundsAsync(job.PaymentId, ct);
        switch (result)
        {
            case HoldResult.Declined:
                log.LogInformation("{PaymentId} HOLD_FUNDS: банк отказал в заморозке — не хватает денег", job.PaymentId);
                return new StepResult.BpmnError("FUNDS_HOLD_DECLINED", "Не хватает денег", new { rejectReason = "INSUFFICIENT_FUNDS" });
            case HoldResult.Cancelled:
                log.LogWarning("{PaymentId} HOLD_FUNDS: ответ банка потерян → заморозка отменена по тому же ключу → отказ", job.PaymentId);
                return new StepResult.BpmnError("FUNDS_HOLD_DECLINED", "Заморозка не подтверждена", new { rejectReason = "HOLD_NOT_CONFIRMED" });
            default:
                log.LogInformation("{PaymentId} HOLD_FUNDS: деньги заморожены на счёте клиента{Replay}", job.PaymentId, Replay(replayed));
                return new StepResult.Done();
        }
    }

    [JobType("TRANSFER_TO_COUNTERPARTY")]
    public async Task<StepResult> Transfer(JobContext job, CancellationToken ct)
    {
        TransferOutcome outcome;
        bool replayed;
        try
        {
            (outcome, replayed) = await payments.TransferAsync(job.PaymentId, job.Duration("transferExecutionWindow"), ct);
        }
        catch (PaymentNotApprovedException e)
        {
            return new StepResult.Incident(e.Message);
        }
        var cutOff = job.Flag("cutOff");
        switch (outcome)
        {
            case TransferOutcome.Done:
                var payment = await payments.GetAsync(job.PaymentId, ct);
                log.LogInformation("{PaymentId} TRANSFER_TO_COUNTERPARTY: деньги списаны и переведены продавцу{CutOff}, платёж проведён за {Elapsed} мс от приёма{Replay}",
                    job.PaymentId, cutOff ? " (по cut-off)" : "", ElapsedMs(payment), Replay(replayed));
                return new StepResult.Done(new { transferStatus = "DONE" });
            case TransferOutcome.Declined:
                log.LogInformation("{PaymentId} TRANSFER_TO_COUNTERPARTY: банк отклонил перевод, деньги остаются замороженными", job.PaymentId);
                return new StepResult.BpmnError("TRANSFER_DECLINED", "Перевод отклонён");
            default:
                log.LogWarning("{PaymentId} TRANSFER_TO_COUNTERPARTY: {Reason} — перевод повторно не отправляем, уточняем статус", job.PaymentId,
                    replayed ? "перевод уже уходил в банк, итог не записан" : "ответ банка потерян");
                return new StepResult.BpmnError("TRANSFER_STATUS_UNKNOWN", "Ответ о переводе потерян");
        }
    }

    [JobType("CHECK_TRANSFER_STATUS")]
    public async Task<StepResult> CheckTransferStatus(JobContext job, CancellationToken ct)
    {
        var status = await payments.CheckTransferStatusAsync(job.PaymentId, job.Duration("clockSkewMargin"), ct);
        switch (status)
        {
            case null:
                return new StepResult.RetryLater("банк ещё не нашёл перевод, срок исполнения не истёк", settings.RetryBackoff);
            case TransferResult.Done:
                var payment = await payments.GetAsync(job.PaymentId, ct);
                log.LogInformation("{PaymentId} CHECK_TRANSFER_STATUS: перевод прошёл, платёж проведён за {Elapsed} мс от приёма",
                    job.PaymentId, ElapsedMs(payment));
                return new StepResult.Done(new { transferStatus = "DONE" });
            default:
                log.LogInformation("{PaymentId} CHECK_TRANSFER_STATUS: {Reason}, деньги остаются замороженными", job.PaymentId,
                    status == TransferResult.NotExecuted ? "срок исполнения истёк, банк перевод не нашёл — не выполнен" : "банк отклонил перевод");
                return new StepResult.Done(new { transferStatus = "DECLINED", rejectReason = "TRANSFER_DECLINED" });
        }
    }

    [JobType("RELEASE_FUNDS")]
    public async Task<StepResult> ReleaseFunds(JobContext job, CancellationToken ct)
    {
        try
        {
            var replayed = await payments.ReleaseFundsAsync(job.PaymentId, job.Duration("clockSkewMargin"), ct);
            log.LogInformation("{PaymentId} RELEASE_FUNDS: деньги разморожены, клиент снова может ими пользоваться{Replay}",
                job.PaymentId, Replay(replayed));
            return new StepResult.Done();
        }
        catch (TransferMayHaveHappenedException)
        {
            return new StepResult.RetryLater("перевод уходил в банк, его итог ещё не известен — разморозка ждёт", TransferOutcomePause);
        }
        catch (TransferCompletedException e)
        {
            return new StepResult.Incident(e.Message);
        }
        catch (BankUnavailableException e)
        {
            return new StepResult.Retry(e.Message);
        }
    }

    [JobType("REJECT_PAYMENT")]
    public async Task<StepResult> RejectPayment(JobContext job, CancellationToken ct)
    {
        var reason = job.String("rejectReason");
        await payments.RejectAsync(job.PaymentId, reason, ct);
        log.LogInformation("{PaymentId} REJECT_PAYMENT: платёж отклонён, причина {Reason}", job.PaymentId, reason);
        return new StepResult.Done();
    }

    private static string Replay(bool replayed) => replayed ? " (повтор: результат из журнала операций)" : "";

    private static long ElapsedMs(Payment? payment) =>
        payment is null ? 0 : (long)(DateTimeOffset.UtcNow - payment.AcceptedAt).TotalMilliseconds;
}
