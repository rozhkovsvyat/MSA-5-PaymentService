using PaymentService.Payments;

namespace PaymentService.Tests;

/// <summary>Банк-заглушка, который считает вызовы; может «потерять» запрос перевода до банка.</summary>
internal sealed class CountingBank(IBank inner) : IBank
{
    public int Holds, Transfers, Releases, Refunds, Debits;
    public bool LoseTransferRequests;

    public Task<HoldResult> HoldAsync(string paymentId, string key, decimal amount, string currency, CancellationToken ct)
    {
        Interlocked.Increment(ref Holds);
        return inner.HoldAsync(paymentId, key, amount, currency, ct);
    }

    public Task CancelHoldAsync(string paymentId, string key, CancellationToken ct) => inner.CancelHoldAsync(paymentId, key, ct);

    public Task<TransferResult> TransferAsync(string paymentId, string key, string counterpartyId, DateTimeOffset executeBefore, CancellationToken ct)
    {
        Interlocked.Increment(ref Transfers);
        if (LoseTransferRequests) throw new BankResponseLostException("запрос не дошёл до банка");
        return inner.TransferAsync(paymentId, key, counterpartyId, executeBefore, ct);
    }

    public Task<BankOperationStatus> GetTransferStatusAsync(string paymentId, string key, CancellationToken ct) =>
        inner.GetTransferStatusAsync(paymentId, key, ct);

    public Task ReleaseAsync(string paymentId, string key, CancellationToken ct)
    {
        Interlocked.Increment(ref Releases);
        return inner.ReleaseAsync(paymentId, key, ct);
    }

    public Task<RefundResult> RefundAsync(string paymentId, string key, decimal amount, DateTimeOffset executeBefore, CancellationToken ct)
    {
        Interlocked.Increment(ref Refunds);
        return inner.RefundAsync(paymentId, key, amount, executeBefore, ct);
    }

    public Task<BankOperationStatus> GetRefundStatusAsync(string paymentId, string key, CancellationToken ct) =>
        inner.GetRefundStatusAsync(paymentId, key, ct);

    public Task<decimal> DebitCounterpartyAsync(string paymentId, string key, string counterpartyId, decimal amount, CancellationToken ct)
    {
        Interlocked.Increment(ref Debits);
        return inner.DebitCounterpartyAsync(paymentId, key, counterpartyId, amount, ct);
    }
}
