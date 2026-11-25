using System.Collections.Concurrent;

namespace PaymentService.Payments;

public enum HoldResult { Held, Declined, Cancelled }

public enum TransferResult { Done, Declined, NotExecuted }

public enum BankOperationStatus { Done, Declined, NotFound }

/// <summary>Банк или эквайер. Каждый вызов несёт ключ идемпотентности paymentId:STEP.</summary>
public interface IBank
{
    /// <exception cref="BankResponseLostException">Запрос ушёл, ответа нет: заморозка могла пройти.</exception>
    Task<HoldResult> HoldAsync(string paymentId, string key, decimal amount, string currency, CancellationToken ct);

    /// <summary>Отменить заморозку по ключу. Безопасно, даже если заморозки не было; повторная заморозка с этим ключом будет отклонена.</summary>
    Task CancelHoldAsync(string paymentId, string key, CancellationToken ct);

    /// <param name="executeBefore">После этого момента банк перевод не исполняет.</param>
    /// <exception cref="BankResponseLostException">Запрос ушёл, ответа нет: перевод мог пройти.</exception>
    Task<TransferResult> TransferAsync(string paymentId, string key, string counterpartyId, DateTimeOffset executeBefore, CancellationToken ct);

    Task<BankOperationStatus> GetTransferStatusAsync(string paymentId, string key, CancellationToken ct);

    /// <exception cref="BankUnavailableException">Банк не ответил.</exception>
    Task ReleaseAsync(string paymentId, string key, CancellationToken ct);

    /// <summary>Вернуть деньги клиенту после перевода продавцу.</summary>
    /// <exception cref="BankResponseLostException">Запрос ушёл, ответа нет: возврат мог пройти.</exception>
    Task<RefundResult> RefundAsync(string paymentId, string key, decimal amount, DateTimeOffset executeBefore, CancellationToken ct);

    Task<BankOperationStatus> GetRefundStatusAsync(string paymentId, string key, CancellationToken ct);

    /// <summary>Списать с баланса продавца сколько есть, но не больше суммы.</summary>
    /// <returns>Сколько списано.</returns>
    Task<decimal> DebitCounterpartyAsync(string paymentId, string key, string counterpartyId, decimal amount, CancellationToken ct);
}

public sealed class BankResponseLostException(string message) : Exception(message);

public sealed class BankUnavailableException(string message) : Exception(message);

/// <summary>
/// Заглушка банка для прототипа: операции идемпотентны по ключу, отменённый ключ заморозки больше не принимается.
/// Поведение на конкретном платеже задаёт <see cref="StandInScenarios"/>.
/// </summary>
public sealed class BankStandIn(StandInScenarios scenarios, ILogger<BankStandIn> log) : IBank
{
    // через сколько «опоздавший» запрос заморозки доходит до банка в сценарии LATE
    private static readonly TimeSpan LateHoldDelay = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, bool> holds = new();
    private readonly ConcurrentDictionary<string, bool> voidedHolds = new();
    private readonly ConcurrentDictionary<string, TransferResult> transfers = new();
    private readonly ConcurrentDictionary<string, RefundResult> refunds = new();
    private readonly ConcurrentDictionary<string, int> statusQueries = new();
    private readonly ConcurrentDictionary<string, int> releaseAttempts = new();
    private readonly ConcurrentDictionary<string, bool> refundDeclined = new();
    private readonly ConcurrentDictionary<string, int> refundSends = new();
    private readonly ConcurrentDictionary<string, decimal> debits = new();
    private readonly ConcurrentDictionary<string, decimal> balances = new();

    public Task<HoldResult> HoldAsync(string paymentId, string key, decimal amount, string currency, CancellationToken ct)
    {
        var mode = scenarios.For(paymentId).Hold;
        if (mode == "LATE")
        {
            _ = Task.Delay(LateHoldDelay, CancellationToken.None).ContinueWith(_ => HoldArrived(paymentId, key), TaskScheduler.Default);
            throw new BankResponseLostException("запрос заморозки задержался в сети, ответа нет");
        }
        if (mode == "DECLINE") return Task.FromResult(HoldResult.Declined);
        var result = HoldArrived(paymentId, key);
        if (mode == "LOST" && result == HoldResult.Held) throw new BankResponseLostException("ответ банка о заморозке потерян");
        return Task.FromResult(result);
    }

    public Task CancelHoldAsync(string paymentId, string key, CancellationToken ct)
    {
        voidedHolds.TryAdd(key, true);
        holds.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<TransferResult> TransferAsync(string paymentId, string key, string counterpartyId, DateTimeOffset executeBefore, CancellationToken ct)
    {
        var mode = scenarios.For(paymentId).Transfer;
        if (mode == "NOT_RECEIVED") throw new BankResponseLostException("запрос перевода не дошёл до банка");
        var result = transfers.GetOrAdd(key, mode == "DECLINE" ? TransferResult.Declined : TransferResult.Done);
        if (mode == "LOST") throw new BankResponseLostException("перевод отправлен, ответ банка потерян");
        return Task.FromResult(result);
    }

    public Task<BankOperationStatus> GetTransferStatusAsync(string paymentId, string key, CancellationToken ct) =>
        Task.FromResult(Status(key, transfers.TryGetValue(key, out var r) ? r == TransferResult.Done : null));

    public Task ReleaseAsync(string paymentId, string key, CancellationToken ct)
    {
        if (releaseAttempts.AddOrUpdate(key, 1, (_, n) => n + 1) <= scenarios.For(paymentId).ReleaseFailures)
            throw new BankUnavailableException("банк не отвечает на разморозку");
        holds.TryRemove(PaymentOperations.Key(paymentId, PaymentOperations.HoldStep), out _);
        return Task.CompletedTask;
    }

    public Task<RefundResult> RefundAsync(string paymentId, string key, decimal amount, DateTimeOffset executeBefore, CancellationToken ct)
    {
        var scenario = scenarios.For(paymentId);
        if (refundSends.AddOrUpdate(key, 1, (_, n) => n + 1) <= scenario.RefundNotReceived)
            throw new BankResponseLostException("запрос возврата не дошёл до банка");
        var mode = scenario.Refund;
        // DECLINE: по старым реквизитам банк отказывает один раз, по новым — возвращает
        var result = refunds.GetOrAdd(key, _ => mode == "DECLINE" && refundDeclined.TryAdd(paymentId, true) ? RefundResult.Declined : RefundResult.Done);
        if (mode == "LOST") throw new BankResponseLostException("возврат отправлен, ответ банка потерян");
        return Task.FromResult(result);
    }

    public Task<BankOperationStatus> GetRefundStatusAsync(string paymentId, string key, CancellationToken ct) =>
        Task.FromResult(Status(key, refunds.TryGetValue(key, out var r) ? r == RefundResult.Done : null));

    public Task<decimal> DebitCounterpartyAsync(string paymentId, string key, string counterpartyId, decimal amount, CancellationToken ct)
    {
        var debited = debits.GetOrAdd(key, _ =>
        {
            lock (balances)
            {
                var balance = balances.GetOrAdd(counterpartyId, scenarios.For(paymentId).SellerBalance);
                var take = Math.Min(balance, amount);
                balances[counterpartyId] = balance - take;
                return take;
            }
        });
        return Task.FromResult(debited);
    }

    private HoldResult HoldArrived(string paymentId, string key)
    {
        if (voidedHolds.ContainsKey(key))
        {
            log.LogWarning("{PaymentId} банк: заморозка по ключу {Key} пришла после отмены — ключ отменён, заморозка отклонена", paymentId, key);
            return HoldResult.Declined;
        }
        holds.TryAdd(key, true);
        return HoldResult.Held;
    }

    // На первый запрос статуса после потери ответа банк операцию ещё не нашёл.
    private BankOperationStatus Status(string key, bool? done)
    {
        if (statusQueries.AddOrUpdate(key, 1, (_, n) => n + 1) == 1 || done is null) return BankOperationStatus.NotFound;
        return done.Value ? BankOperationStatus.Done : BankOperationStatus.Declined;
    }
}
