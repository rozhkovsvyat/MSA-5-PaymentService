using PaymentService.Payments;

namespace PaymentService.Process;

/// <summary>Шаги процесса «Возврат постфактум» (post-factum-refund).</summary>
public sealed class RefundSteps(RefundOperations refunds, ProcessSettings settings, ILogger<RefundSteps> log)
{
    [JobType("CREATE_REFUND")]
    public async Task<StepResult> CreateRefund(JobContext job, CancellationToken ct)
    {
        try
        {
            var created = await refunds.CreateAsync(job.PaymentId, ct);
            if (await refunds.SetProcessKeyAsync(job.PaymentId, job.ProcessInstanceKey, ct) is { } owner && owner != job.ProcessInstanceKey)
            {
                log.LogError("{PaymentId} CREATE_REFUND: возврат уже ведёт процесс {Owner}, экземпляр {Duplicate} — дубликат",
                    job.PaymentId, owner, job.ProcessInstanceKey);
                return new StepResult.Incident($"возврат по платежу {job.PaymentId} уже ведёт процесс {owner}, этот экземпляр — дубликат");
            }
            var refund = (await refunds.GetAsync(job.PaymentId, ct))!;
            log.LogInformation("{PaymentId} CREATE_REFUND: мошенничество обнаружено после перевода, возврат {Result}",
                job.PaymentId, created ? "зарегистрирован" : "уже зарегистрирован (повтор)");
            return new StepResult.Done(new { refundAttempt = refund.Attempt });
        }
        catch (RefundNotAllowedException e)
        {
            return new StepResult.Incident(e.Message);
        }
    }

    [JobType("REFUND_TO_CLIENT")]
    public async Task<StepResult> RefundToClient(JobContext job, CancellationToken ct)
    {
        var attempt = Attempt(job);
        RefundOutcome outcome;
        bool replayed;
        try
        {
            (outcome, replayed) = await refunds.RefundToClientAsync(job.PaymentId, attempt, job.Duration("transferExecutionWindow"), ct);
        }
        catch (RefundNotAllowedException e)
        {
            return new StepResult.Incident(e.Message);
        }
        switch (outcome)
        {
            case RefundOutcome.Done:
                log.LogInformation("{PaymentId} REFUND_TO_CLIENT: деньги возвращены клиенту (попытка {Attempt}){Replay}", job.PaymentId, attempt,
                    replayed ? " — итог из журнала операций" : "");
                return new StepResult.Done();
            case RefundOutcome.Declined:
                log.LogInformation("{PaymentId} REFUND_TO_CLIENT: банк отклонил возврат (попытка {Attempt}{Replay}) — нужны новые реквизиты клиента",
                    job.PaymentId, attempt, replayed ? ", итог из журнала операций" : "");
                return new StepResult.BpmnError("REFUND_DECLINED", "Банк отклонил возврат");
            default:
                log.LogWarning("{PaymentId} REFUND_TO_CLIENT: {Reason} — возврат повторно не отправляем, уточняем статус", job.PaymentId,
                    replayed ? "возврат уже уходил в банк, итог не записан" : "ответ банка потерян");
                return new StepResult.BpmnError("REFUND_STATUS_UNKNOWN", "Ответ о возврате потерян");
        }
    }

    [JobType("CHECK_REFUND_STATUS")]
    public async Task<StepResult> CheckRefundStatus(JobContext job, CancellationToken ct)
    {
        var status = await refunds.CheckRefundStatusAsync(job.PaymentId, Attempt(job), job.Duration("clockSkewMargin"), ct);
        switch (status)
        {
            case null:
                return new StepResult.RetryLater("банк ещё не нашёл возврат, срок исполнения не истёк", settings.RetryBackoff);
            case RefundResult.Done:
                log.LogInformation("{PaymentId} CHECK_REFUND_STATUS: возврат прошёл", job.PaymentId);
                return new StepResult.Done(new { refundStatus = "DONE" });
            case RefundResult.Declined:
                log.LogInformation("{PaymentId} CHECK_REFUND_STATUS: банк отклонил возврат, шаг возврата возьмёт отказ из журнала", job.PaymentId);
                return new StepResult.Done(new { refundStatus = "DECLINED" });
            default:
                if (await refunds.UnexecutedLimitReachedAsync(job.PaymentId, ct))
                    return new StepResult.Incident($"банк не исполнил возврат, отправок подряд: {refunds.MaxUnexecutedSends}; после устранения причины шаг нужно повторить");
                log.LogInformation("{PaymentId} CHECK_REFUND_STATUS: возврат точно не выполнен, отправим снова с тем же ключом", job.PaymentId);
                return new StepResult.Done(new { refundStatus = "NOT_EXECUTED" });
        }
    }

    [JobType("RECOVER_FROM_COUNTERPARTY")]
    public async Task<StepResult> RecoverFromCounterparty(JobContext job, CancellationToken ct)
    {
        var (result, replayed) = await refunds.RecoverFromCounterpartyAsync(job.PaymentId, ct);
        if (result.Debt > 0)
            log.LogInformation("{PaymentId} RECOVER_FROM_COUNTERPARTY: с продавца удержано {Recovered}, на балансе не хватило — {Debt} записано в долг продавца{Replay}",
                job.PaymentId, result.Recovered, result.Debt, replayed ? " (повтор: из журнала)" : "");
        else
            log.LogInformation("{PaymentId} RECOVER_FROM_COUNTERPARTY: с продавца удержано {Recovered}{Replay}",
                job.PaymentId, result.Recovered, replayed ? " (повтор: из журнала)" : "");
        return new StepResult.Done(new { recoveredAmount = result.Recovered, debtAmount = result.Debt });
    }

    private static int Attempt(JobContext job) => Math.Max(job.Get<int>("refundAttempt"), 1);
}
