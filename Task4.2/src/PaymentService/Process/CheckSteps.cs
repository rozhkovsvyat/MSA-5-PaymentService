using System.Diagnostics;
using PaymentService.FraudCheck;
using PaymentService.Payments;

namespace PaymentService.Process;

/// <summary>
/// Проверки платежа. FraudCheck Service вызывается синхронно по HTTP, срок ответа считается от отправки запроса:
/// если наши обработчики не работают, задача ждёт в Zeebe и без проверки ничего не проводится.
/// </summary>
public sealed class CheckSteps(IFraudCheckClient fraudCheck, PaymentOperations payments, ProcessSettings settings, ILogger<CheckSteps> log)
{
    // пауза между повторами вызова после технического сбоя, пока не истёк срок ответа
    private static readonly TimeSpan CallRetryPause = TimeSpan.FromMilliseconds(200);

    [JobType("CHECK_FRAUD")]
    public async Task<StepResult> CheckFraud(JobContext job, CancellationToken ct)
    {
        await payments.MarkChecksStartedAsync(job.PaymentId, ct);
        var clock = Stopwatch.StartNew();
        var decision = await CallWithinAsync(settings.CheckTimeout, job, token => fraudCheck.CheckFraudAsync(Request(job), token), ct);
        if (decision is null)
        {
            log.LogWarning("{PaymentId} CHECK_FRAUD: антифрод не ответил за {Timeout} с → cut-off, разрешено без ответа проверки",
                job.PaymentId, settings.CheckTimeout.TotalSeconds);
            return new StepResult.Done(new { fraudDecision = "CUT_OFF", cutOff = true });
        }
        if (decision == "MANUAL") await payments.MarkManualReviewAsync(job.PaymentId, ct);
        log.LogInformation("{PaymentId} CHECK_FRAUD: антифрод ответил {Decision} за {Elapsed} мс", job.PaymentId, decision, clock.ElapsedMilliseconds);
        return new StepResult.Done(new { fraudDecision = decision });
    }

    [JobType("CHECK_EXTERNAL")]
    public async Task<StepResult> CheckExternal(JobContext job, CancellationToken ct)
    {
        await payments.MarkChecksStartedAsync(job.PaymentId, ct);
        var result = await fraudCheck.CheckExternalAsync(Request(job), settings.CheckTimeout, ct);
        var aml = result.AmlSource switch
        {
            AmlSource.Provider => $"AML {result.Aml}",
            AmlSource.Cache => $"AML-провайдер молчал, AML {result.Aml} из кеша",
            _ => "AML-провайдер молчал, в кеше AML нет",
        };

        // запрет одного провайдера отклоняет платёж, даже если второй не ответил
        var denied = result.Decision == "DENY" || result.Aml == "DENY" || result.LimitsAndGeo == "DENY";
        if (!denied && (result.LimitsAndGeo is null || result.Aml is null))
        {
            log.LogWarning("{PaymentId} CHECK_EXTERNAL: нет ответа за {Timeout} с ({Silent}; {Aml}) → cut-off, разрешено без ответа проверки",
                job.PaymentId, settings.CheckTimeout.TotalSeconds, result.LimitsAndGeo is null ? "лимиты и GeoRisk молчали" : "лимиты и GeoRisk ответили", aml);
            return new StepResult.Done(new { externalDecision = "CUT_OFF", cutOff = true });
        }
        var decision = denied ? "DENY" : "ALLOW";
        log.LogInformation("{PaymentId} CHECK_EXTERNAL: внешние проверки {Decision} ({Aml}, лимиты и GeoRisk {Limits})",
            job.PaymentId, decision, aml, result.LimitsAndGeo ?? "нет ответа");
        return new StepResult.Done(new { externalDecision = decision });
    }

    [JobType("RECORD_REVIEW_DECISION")]
    public async Task<StepResult> RecordReviewDecision(JobContext job, CancellationToken ct)
    {
        var decision = job.String("reviewDecision") ?? throw new InvalidOperationException("нет решения оператора");
        var final = await fraudCheck.RecordReviewDecisionAsync(job.PaymentId, decision, ct);
        log.LogInformation("{PaymentId} RECORD_REVIEW_DECISION: решение оператора {Decision} сохранено в FraudCheck, итоговое решение {Final}",
            job.PaymentId, decision, final);
        return new StepResult.Done(new { reviewDecision = final });
    }

    [JobType("RECHECK_DECISIONS")]
    public async Task<StepResult> RecheckDecisions(JobContext job, CancellationToken ct)
    {
        await payments.MarkCutOffApprovedAsync(job.PaymentId, ct);
        var final = await CallWithinAsync(settings.RecheckTimeout, job, token => fraudCheck.GetFinalDecisionsAsync(job.PaymentId, token), ct);
        if (final is null)
        {
            log.LogWarning("{PaymentId} RECHECK_DECISIONS: FraudCheck не ответил за {Timeout} мс → проводим по cut-off",
                job.PaymentId, settings.RecheckTimeout.TotalMilliseconds);
            return new StepResult.Done(new { recheckDenied = false });
        }
        if (final.Fraud == "DENY" || final.External == "DENY")
        {
            log.LogWarning("{PaymentId} RECHECK_DECISIONS: после cut-off пришёл запрет ({Source}) → отказ до перевода",
                job.PaymentId, final.Fraud == "DENY" ? "антифрод" : "внешние проверки");
            return final.Fraud == "DENY"
                ? new StepResult.Done(new { recheckDenied = true, fraudDecision = "DENY" })
                : new StepResult.Done(new { recheckDenied = true, externalDecision = "DENY" });
        }
        log.LogInformation("{PaymentId} RECHECK_DECISIONS: запретов нет (антифрод {Fraud}, внешние {External}) → проводим",
            job.PaymentId, final.Fraud ?? "без решения", final.External ?? "без решения");
        return new StepResult.Done(new { recheckDenied = false });
    }

    /// <summary>Вызов со сроком ответа; сбои повторяются, пока срок не истёк. <c>null</c> — ответа за срок нет.</summary>
    private async Task<T?> CallWithinAsync<T>(TimeSpan timeout, JobContext job, Func<CancellationToken, Task<T>> call, CancellationToken ct)
        where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        while (true)
        {
            try
            {
                return await call(deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning("{PaymentId} {Step}: сбой вызова FraudCheck ({Reason}), повтор в пределах срока", job.PaymentId, job.Step, e.Message);
                try { await Task.Delay(CallRetryPause, deadline.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            }
        }
    }

    private static CheckRequest Request(JobContext job) =>
        new(job.PaymentId, job.String("clientId")!, job.String("counterpartyId")!, job.Get<decimal>("amount"), job.String("currency")!);
}
