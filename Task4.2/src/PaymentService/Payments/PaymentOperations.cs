using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PaymentService.Payments;

public enum TransferOutcome { Done, Declined, Unknown }

/// <param name="TransferExecutionWindow">Сколько банк может исполнять перевод после отправки.</param>
/// <param name="ClockSkewMargin">Запас на расхождение часов с банком: «не найден» значит «не выполнен» только после срока и этого запаса.</param>
/// <param name="MaxUnexecutedRefundSends">После скольких отправок возврата, которые банк не исполнил, шагу нужен дежурный.</param>
public sealed record PaymentSettings(TimeSpan TransferExecutionWindow, TimeSpan ClockSkewMargin, int MaxUnexecutedRefundSends = 3);

public sealed class TransferMayHaveHappenedException(string paymentId)
    : Exception($"перевод по платежу {paymentId} уходил в банк, его итог ещё не известен, разморозка запрещена");

public sealed class TransferCompletedException(string paymentId)
    : Exception($"перевод по платежу {paymentId} выполнен, разморозка невозможна");

public sealed class PaymentNotApprovedException(string paymentId, string status)
    : Exception($"платёж {paymentId} в состоянии {status}, перевод не отправляется");

/// <summary>
/// Операции платежа, которые вызывают шаги процесса. Денежные операции проходят через журнал
/// с ключом paymentId:STEP: повтор шага берёт результат из журнала и не создаёт второго эффекта.
/// </summary>
public sealed class PaymentOperations(PaymentStore store, IBank bank, PaymentSettings settings)
{
    public const string HoldStep = "HOLD_FUNDS";
    public const string TransferStep = "TRANSFER_TO_COUNTERPARTY";
    public const string ReleaseStep = "RELEASE_FUNDS";

    public static string Key(string paymentId, string step) => $"{paymentId}:{step}";

    public Task<bool> CreateAsync(NewPayment payment, CancellationToken ct) => store.InsertAsync(payment, ct);

    public Task<Payment?> GetAsync(string paymentId, CancellationToken ct) => store.GetAsync(paymentId, ct);

    public Task<IReadOnlyList<PaymentOperation>> OperationsAsync(string paymentId, CancellationToken ct) =>
        store.ListOperationsAsync(paymentId, ct);

    /// <summary>Регистрирует запрос маркетплейса по ключу идемпотентности вместе с отпечатком его содержимого.</summary>
    public Task<PaymentRequestRecord> RegisterRequestAsync(string requestKey, decimal amount, string currency,
        string clientId, string counterpartyId, string newPaymentId, CancellationToken ct)
    {
        var fingerprint = string.Join('|', amount.ToString("0.00", CultureInfo.InvariantCulture), currency, clientId, counterpartyId);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
        return store.RegisterRequestAsync(requestKey, hash, newPaymentId, ct);
    }

    /// <summary>
    /// Закрепляет платёж за экземпляром процесса: дальше повтор запроса маркетплейса сообщение о старте не публикует.
    /// Возвращает экземпляр, за которым платёж закреплён; если это другой экземпляр, текущий — дубликат.
    /// </summary>
    public Task<long?> SetProcessKeyAsync(string paymentId, long processInstanceKey, CancellationToken ct) =>
        store.SetRequestProcessAsync(paymentId, processInstanceKey, ct);

    public Task<long?> FindProcessKeyAsync(string paymentId, CancellationToken ct) => store.FindProcessKeyAsync(paymentId, ct);

    public Task MarkChecksStartedAsync(string paymentId, CancellationToken ct) =>
        store.TransitionAsync(paymentId, PaymentStatus.Checking, ct: ct);

    public Task MarkManualReviewAsync(string paymentId, CancellationToken ct) =>
        store.TransitionAsync(paymentId, PaymentStatus.InManualReview, ct: ct);

    /// <summary>Платёж одобрен без ответа одной из проверок.</summary>
    public Task MarkCutOffApprovedAsync(string paymentId, CancellationToken ct) =>
        store.TransitionAsync(paymentId, PaymentStatus.CutOffApproved, cutOff: true, ct: ct);

    /// <summary>Заморозка суммы. Если ответ банка потерян, заморозка отменяется по тому же ключу.</summary>
    public async Task<(HoldResult Result, bool Replayed)> HoldFundsAsync(string paymentId, CancellationToken ct)
    {
        var key = Key(paymentId, HoldStep);
        if (await store.FindOperationAsync(key, ct) is { } recorded)
        {
            var replayed = Enum.Parse<HoldResult>(recorded);
            if (replayed == HoldResult.Held) await store.TransitionAsync(paymentId, PaymentStatus.FundsHeld, ct: ct);
            return (replayed, true);
        }
        var payment = await RequireAsync(paymentId, ct);
        HoldResult result;
        try
        {
            result = await bank.HoldAsync(paymentId, key, payment.Amount, payment.Currency, ct);
        }
        catch (BankResponseLostException)
        {
            await bank.CancelHoldAsync(paymentId, key, ct);
            result = HoldResult.Cancelled;
        }
        await store.RecordAsync(key, paymentId, HoldStep, result.ToString(), result == HoldResult.Held ? PaymentStatus.FundsHeld : null, ct);
        return (result, false);
    }

    /// <summary>
    /// Списание замороженной суммы и перевод продавцу — точка невозврата. Перевод уходит в банк один раз:
    /// если он уже отправлялся, а итог не записан, итог узнаёт только <see cref="CheckTransferStatusAsync"/>.
    /// </summary>
    /// <exception cref="PaymentNotApprovedException">Платёж не одобрен к переводу.</exception>
    /// <param name="window">Срок исполнения перевода банком, если он задан для этого платежа отдельно.</param>
    public async Task<(TransferOutcome Outcome, bool Replayed)> TransferAsync(string paymentId, TimeSpan? window, CancellationToken ct)
    {
        var key = Key(paymentId, TransferStep);
        if (await store.FindOperationAsync(key, ct) is { } recorded)
        {
            var done = Enum.Parse<TransferResult>(recorded) == TransferResult.Done;
            if (done) await store.TransitionAsync(paymentId, PaymentStatus.Completed, ct: ct);
            return (done ? TransferOutcome.Done : TransferOutcome.Declined, true);
        }
        var payment = await RequireAsync(paymentId, ct);
        if (payment.TransferDeadline is not null)
        {
            await store.TransitionAsync(paymentId, PaymentStatus.TransferStatusUnknown, ct: ct);
            return (TransferOutcome.Unknown, true);
        }

        var executeBefore = DateTimeOffset.UtcNow + (window ?? settings.TransferExecutionWindow);
        await store.TransitionAsync(paymentId, PaymentStatus.Approved, ct: ct); // после cut-off платёж уже CUT_OFF_APPROVED
        if (!await store.TransitionAsync(paymentId, PaymentStatus.Transferring, transferDeadline: executeBefore, ct: ct))
            throw new PaymentNotApprovedException(paymentId, (await RequireAsync(paymentId, ct)).Status);

        TransferResult result;
        try
        {
            result = await bank.TransferAsync(paymentId, key, payment.CounterpartyId, executeBefore, ct);
        }
        catch (BankResponseLostException)
        {
            await store.TransitionAsync(paymentId, PaymentStatus.TransferStatusUnknown, ct: ct);
            return (TransferOutcome.Unknown, false);
        }
        await store.RecordAsync(key, paymentId, TransferStep, result.ToString(), result == TransferResult.Done ? PaymentStatus.Completed : null, ct);
        return (result == TransferResult.Done ? TransferOutcome.Done : TransferOutcome.Declined, false);
    }

    /// <returns><c>null</c>, пока банк не нашёл перевод, а срок его исполнения с запасом ещё не истёк.</returns>
    /// <param name="clockSkewMargin">Запас на расхождение часов, если он задан для этого платежа отдельно.</param>
    public async Task<TransferResult?> CheckTransferStatusAsync(string paymentId, TimeSpan? clockSkewMargin, CancellationToken ct)
    {
        var key = Key(paymentId, TransferStep);
        if (await store.FindOperationAsync(key, ct) is { } recorded)
        {
            var replayed = Enum.Parse<TransferResult>(recorded);
            if (replayed == TransferResult.Done) await store.TransitionAsync(paymentId, PaymentStatus.Completed, ct: ct);
            return replayed;
        }
        var status = await bank.GetTransferStatusAsync(paymentId, key, ct);
        TransferResult result;
        if (status == BankOperationStatus.NotFound)
        {
            var deadline = (await RequireAsync(paymentId, ct)).TransferDeadline;
            if (deadline is null || DateTimeOffset.UtcNow < deadline + (clockSkewMargin ?? settings.ClockSkewMargin)) return null;
            result = TransferResult.NotExecuted;
        }
        else
        {
            result = status == BankOperationStatus.Done ? TransferResult.Done : TransferResult.Declined;
        }
        await store.RecordAsync(key, paymentId, TransferStep, result.ToString(), result == TransferResult.Done ? PaymentStatus.Completed : null, ct);
        return result;
    }

    /// <summary>
    /// Компенсация заморозки. Если перевод уходил в банк, шаг сначала уточняет его итог: размораживать можно
    /// только после однозначного «отклонён» или «не выполнен».
    /// </summary>
    /// <exception cref="TransferMayHaveHappenedException">Итог перевода ещё не известен.</exception>
    /// <exception cref="TransferCompletedException">Перевод выполнен.</exception>
    /// <exception cref="BankUnavailableException">Банк не ответил, деньги остаются замороженными.</exception>
    public async Task<bool> ReleaseFundsAsync(string paymentId, TimeSpan? clockSkewMargin, CancellationToken ct)
    {
        var payment = await RequireAsync(paymentId, ct);
        if (payment.TransferDeadline is not null)
        {
            switch (await CheckTransferStatusAsync(paymentId, clockSkewMargin, ct))
            {
                case null: throw new TransferMayHaveHappenedException(paymentId);
                case TransferResult.Done: throw new TransferCompletedException(paymentId);
            }
        }

        var key = Key(paymentId, ReleaseStep);
        await store.TransitionAsync(paymentId, PaymentStatus.ReleasingFunds, ct: ct);
        if (await store.FindOperationAsync(key, ct) is not null) return true;
        await bank.ReleaseAsync(paymentId, key, ct);
        await store.RecordAsync(key, paymentId, ReleaseStep, "Released", null, ct);
        return false;
    }

    public Task RejectAsync(string paymentId, string? reason, CancellationToken ct) =>
        store.TransitionAsync(paymentId, PaymentStatus.Rejected, rejectReason: reason, ct: ct);

    private async Task<Payment> RequireAsync(string paymentId, CancellationToken ct) =>
        await store.GetAsync(paymentId, ct) ?? throw new InvalidOperationException($"платёж {paymentId} не найден");
}
