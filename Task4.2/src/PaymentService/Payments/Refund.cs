namespace PaymentService.Payments;

/// <summary>Состояния возврата постфактум и разрешённые переходы (Task2).</summary>
public static class RefundStatus
{
    public const string Requested = "REFUND_REQUESTED";
    public const string Refunding = "REFUNDING";
    public const string StatusUnknown = "REFUND_STATUS_UNKNOWN";
    public const string DetailsRequested = "REFUND_DETAILS_REQUESTED";
    public const string Refunded = "REFUNDED";

    public static string[] AllowedFrom(string status) => status switch
    {
        Requested => [StatusUnknown, DetailsRequested],
        Refunding => [Requested],
        StatusUnknown => [Refunding],
        DetailsRequested => [Refunding, StatusUnknown],
        Refunded => [Refunding, StatusUnknown],
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "неизвестное состояние возврата"),
    };
}

/// <summary>Удержание суммы возврата с продавца.</summary>
public static class RecoveryStatus
{
    public const string Pending = "RECOVERY_PENDING";
    public const string Recovered = "RECOVERED";
    public const string DebtRecorded = "DEBT_RECORDED";
}

public sealed record Refund(
    string PaymentId, string Status, int Attempt, DateTimeOffset? RefundDeadline,
    string? RecoveryStatus, decimal? RecoveredAmount, decimal? DebtAmount);

public enum RefundResult { Done, Declined, NotExecuted }

public enum RefundOutcome { Done, Declined, Unknown }

/// <param name="Recovered">Сколько удержано с баланса продавца.</param>
/// <param name="Debt">Сколько записано в долг продавца.</param>
public sealed record RecoveryResult(decimal Recovered, decimal Debt);
