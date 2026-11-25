namespace PaymentService.Payments;

public sealed class RefundNotAllowedException(string message) : Exception(message);

/// <summary>
/// Возврат постфактум: деньги клиенту после перевода продавцу и удержание суммы с продавца.
/// Только для проведённого платежа и не больше одного возврата на платёж. Деньги двигаются через журнал:
/// возврат — по ключу paymentId:REFUND_TO_CLIENT:попытка, удержание — paymentId:RECOVER_FROM_COUNTERPARTY.
/// </summary>
public sealed class RefundOperations(PaymentStore payments, RefundStore refunds, IBank bank, PaymentSettings settings)
{
    public const string RefundStep = "REFUND_TO_CLIENT";
    public const string RecoverStep = "RECOVER_FROM_COUNTERPARTY";

    public static string RefundKey(string paymentId, int attempt) => $"{paymentId}:{RefundStep}:{attempt}";

    public Task<Refund?> GetAsync(string paymentId, CancellationToken ct) => refunds.GetAsync(paymentId, ct);

    /// <summary>Закрепляет возврат за экземпляром процесса; если возврат уже ведёт другой экземпляр, текущий — дубликат.</summary>
    public Task<long?> SetProcessKeyAsync(string paymentId, long processInstanceKey, CancellationToken ct) =>
        refunds.SetProcessKeyAsync(paymentId, processInstanceKey, ct);

    /// <summary>Можно ли начинать возврат: платёж проведён, а возврата по нему ещё не было.</summary>
    /// <exception cref="RefundNotAllowedException">Возврат невозможен.</exception>
    public async Task EnsureRefundableAsync(string paymentId, CancellationToken ct)
    {
        var payment = await payments.GetAsync(paymentId, ct) ?? throw new RefundNotAllowedException($"платёж {paymentId} не найден");
        if (payment.Status != PaymentStatus.Completed)
            throw new RefundNotAllowedException($"платёж {paymentId} в состоянии {payment.Status}, вернуть можно только проведённый");
        if (await refunds.GetAsync(paymentId, ct) is { Status: RefundStatus.Refunded })
            throw new RefundNotAllowedException($"по платежу {paymentId} возврат уже выполнен");
    }

    /// <returns><c>false</c>, если возврат по платежу уже зарегистрирован.</returns>
    public async Task<bool> CreateAsync(string paymentId, CancellationToken ct)
    {
        var payment = await payments.GetAsync(paymentId, ct) ?? throw new RefundNotAllowedException($"платёж {paymentId} не найден");
        if (payment.Status != PaymentStatus.Completed)
            throw new RefundNotAllowedException($"платёж {paymentId} в состоянии {payment.Status}, вернуть можно только проведённый");
        return await refunds.InsertAsync(paymentId, ct);
    }

    /// <summary>Номер следующей попытки, если банк отклонил возврат и клиента попросили указать новые реквизиты.</summary>
    /// <exception cref="RefundNotAllowedException">Новые реквизиты сейчас не ждут.</exception>
    public async Task<int> NextAttemptForNewDetailsAsync(string paymentId, CancellationToken ct)
    {
        var refund = await refunds.GetAsync(paymentId, ct);
        if (refund?.Status != RefundStatus.DetailsRequested)
            throw new RefundNotAllowedException($"по платежу {paymentId} новые реквизиты не запрашивались");
        return refund.Attempt + 1;
    }

    /// <summary>
    /// Возврат денег клиенту — точка невозврата процесса возврата. Попытка уходит в банк один раз: если она уже
    /// отправлялась, а итог не записан, итог узнаёт только <see cref="CheckRefundStatusAsync"/>.
    /// </summary>
    public async Task<(RefundOutcome Outcome, bool Replayed)> RefundToClientAsync(string paymentId, int attempt, TimeSpan? window, CancellationToken ct)
    {
        var key = RefundKey(paymentId, attempt);
        if (await payments.FindOperationAsync(key, ct) is { } recorded)
        {
            var done = Enum.Parse<RefundResult>(recorded) == RefundResult.Done;
            await refunds.TransitionAsync(paymentId, done ? RefundStatus.Refunded : RefundStatus.DetailsRequested, ct: ct);
            return (done ? RefundOutcome.Done : RefundOutcome.Declined, true);
        }
        var refund = await refunds.GetAsync(paymentId, ct) ?? throw new RefundNotAllowedException($"по платежу {paymentId} возврат не зарегистрирован");
        if (attempt > refund.Attempt)
        {
            await refunds.TransitionAsync(paymentId, RefundStatus.Requested, attempt: attempt, clearDeadline: true, ct: ct);
            refund = refund with { Attempt = attempt, RefundDeadline = null };
        }
        if (refund.RefundDeadline is not null)
        {
            await refunds.TransitionAsync(paymentId, RefundStatus.StatusUnknown, ct: ct);
            return (RefundOutcome.Unknown, true);
        }

        var payment = (await payments.GetAsync(paymentId, ct))!;
        var executeBefore = DateTimeOffset.UtcNow + (window ?? settings.TransferExecutionWindow);
        if (!await refunds.TransitionAsync(paymentId, RefundStatus.Refunding, refundDeadline: executeBefore, ct: ct))
            throw new RefundNotAllowedException($"возврат по платежу {paymentId} в состоянии {refund.Status}, деньги не отправляются");
        RefundResult result;
        try
        {
            result = await bank.RefundAsync(paymentId, key, payment.Amount, executeBefore, ct);
        }
        catch (BankResponseLostException)
        {
            await refunds.TransitionAsync(paymentId, RefundStatus.StatusUnknown, ct: ct);
            return (RefundOutcome.Unknown, false);
        }
        await refunds.RecordAsync(key, paymentId, RefundStep, result.ToString(),
            result == RefundResult.Done ? RefundStatus.Refunded : RefundStatus.DetailsRequested, ct);
        return (result == RefundResult.Done ? RefundOutcome.Done : RefundOutcome.Declined, false);
    }

    /// <returns>
    /// <c>null</c>, пока банк не нашёл возврат, а срок исполнения с запасом не истёк. <see cref="RefundResult.NotExecuted"/> —
    /// возврат точно не выполнен, его можно отправить снова с тем же ключом.
    /// </returns>
    public async Task<RefundResult?> CheckRefundStatusAsync(string paymentId, int attempt, TimeSpan? clockSkewMargin, CancellationToken ct)
    {
        var key = RefundKey(paymentId, attempt);
        if (await payments.FindOperationAsync(key, ct) is { } recorded)
        {
            var replayed = Enum.Parse<RefundResult>(recorded);
            if (replayed == RefundResult.Done) await refunds.TransitionAsync(paymentId, RefundStatus.Refunded, ct: ct);
            return replayed;
        }
        var refund = await refunds.GetAsync(paymentId, ct);
        // эта отправка уже признана неисполненной: задачу выдали повторно или её повторил дежурный после инцидента
        if (refund is { Status: RefundStatus.Requested, RefundDeadline: null }) return RefundResult.NotExecuted;
        var status = await bank.GetRefundStatusAsync(paymentId, key, ct);
        if (status == BankOperationStatus.NotFound)
        {
            var deadline = refund?.RefundDeadline;
            if (deadline is null || DateTimeOffset.UtcNow < deadline + (clockSkewMargin ?? settings.ClockSkewMargin)) return null;
            await refunds.MarkNotExecutedAsync(paymentId, ct);
            return RefundResult.NotExecuted;
        }
        // отказ записывается в журнал, состояние меняет повторный шаг возврата, который возьмёт итог из журнала
        var result = status == BankOperationStatus.Done ? RefundResult.Done : RefundResult.Declined;
        await refunds.RecordAsync(key, paymentId, RefundStep, result.ToString(), result == RefundResult.Done ? RefundStatus.Refunded : null, ct);
        return result;
    }

    /// <returns><c>true</c>, если банк не исполнил возврат столько раз подряд, что дальше нужен дежурный.</returns>
    public Task<bool> UnexecutedLimitReachedAsync(string paymentId, CancellationToken ct) =>
        refunds.TakeUnexecutedLimitAsync(paymentId, settings.MaxUnexecutedRefundSends, ct);

    public int MaxUnexecutedSends => settings.MaxUnexecutedRefundSends;

    /// <summary>Удержать сумму возврата с баланса продавца. Не хватило — остаток записывается в долг продавца.</summary>
    public async Task<(RecoveryResult Result, bool Replayed)> RecoverFromCounterpartyAsync(string paymentId, CancellationToken ct)
    {
        var key = PaymentOperations.Key(paymentId, RecoverStep);
        if (await payments.FindOperationAsync(key, ct) is not null)
        {
            var refund = (await refunds.GetAsync(paymentId, ct))!;
            return (new RecoveryResult(refund.RecoveredAmount ?? 0, refund.DebtAmount ?? 0), true);
        }
        var payment = (await payments.GetAsync(paymentId, ct))!;
        var debited = await bank.DebitCounterpartyAsync(paymentId, key, payment.CounterpartyId, payment.Amount, ct);
        var result = new RecoveryResult(debited, payment.Amount - debited);
        await refunds.RecordRecoveryAsync(key, paymentId, result, ct);
        return (result, false);
    }
}
