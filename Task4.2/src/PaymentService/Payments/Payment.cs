namespace PaymentService.Payments;

/// <summary>Состояния платежа и разрешённые переходы (таблица переходов — Task2).</summary>
public static class PaymentStatus
{
    public const string Created = "CREATED";
    public const string FundsHeld = "FUNDS_HELD";
    public const string Checking = "CHECKING";
    public const string InManualReview = "IN_MANUAL_REVIEW";
    public const string Approved = "APPROVED";
    public const string CutOffApproved = "CUT_OFF_APPROVED";
    public const string Transferring = "TRANSFERRING";
    public const string TransferStatusUnknown = "TRANSFER_STATUS_UNKNOWN";
    public const string Completed = "COMPLETED";
    public const string ReleasingFunds = "RELEASING_FUNDS";
    public const string Rejected = "REJECTED";

    /// <summary>Из каких состояний можно перейти в <paramref name="status"/>. Из COMPLETED и REJECTED переходов нет.</summary>
    public static string[] AllowedFrom(string status) => status switch
    {
        FundsHeld => [Created],
        Checking => [FundsHeld],
        InManualReview => [FundsHeld, Checking],
        Approved or CutOffApproved => [FundsHeld, Checking, InManualReview],
        Transferring => [Approved, CutOffApproved],
        TransferStatusUnknown => [Transferring],
        Completed => [Transferring, TransferStatusUnknown],
        ReleasingFunds => [FundsHeld, Checking, InManualReview, Approved, CutOffApproved, Transferring, TransferStatusUnknown],
        Rejected => [Created, ReleasingFunds],
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "неизвестное состояние платежа"),
    };
}

public sealed record NewPayment(
    string PaymentId, string ClientId, string CounterpartyId, decimal Amount, string Currency, DateTimeOffset AcceptedAt);

public sealed record Payment(
    string Id, string ClientId, string CounterpartyId, decimal Amount, string Currency, string Status,
    bool CutOff, string? RejectReason, DateTimeOffset? TransferDeadline, DateTimeOffset AcceptedAt, DateTimeOffset? FinishedAt);

public sealed record PaymentOperation(string Step, string Result, DateTimeOffset CreatedAt);

/// <param name="Created">Запрос пришёл впервые.</param>
/// <param name="Conflict">Ключ уже использован для запроса с другими суммой, валютой или участниками.</param>
public sealed record PaymentRequestRecord(string PaymentId, long? ProcessInstanceKey, bool Created, bool Conflict);
