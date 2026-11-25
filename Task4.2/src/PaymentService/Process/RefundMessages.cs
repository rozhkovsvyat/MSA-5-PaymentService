using System.Text.Json;
using Grpc.Core;
using PaymentService.Payments;
using Zeebe.Client;

namespace PaymentService.Process;

/// <param name="TransferExecutionWindow">Срок исполнения возврата банком, ISO 8601; в демо его сокращают.</param>
/// <param name="ClockSkewMargin">Запас на расхождение часов с банком, ISO 8601; в демо его сокращают.</param>
public sealed record FraudDetectedRequest(StandInScenario? Emulate = null, string? TransferExecutionWindow = null, string? ClockSkewMargin = null);

/// <summary>Сообщения процессу «Возврат постфактум»: поздний сигнал о мошенничестве и новые реквизиты клиента.</summary>
public sealed class RefundMessages(IZeebeClient zeebe, PaymentOperations payments, RefundOperations refunds, ProcessSettings settings,
    ILogger<RefundMessages> log)
{
    public const string FraudDetected = "fraud-detected-after-transfer";
    public const string DetailsProvided = "refund-details-provided";

    // реквизиты могут прийти раньше, чем процесс начнёт их ждать: сообщение подождёт подписку
    private static readonly TimeSpan DetailsTimeToLive = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Запускает возврат. Идентификатор сообщения — refund:paymentId: Zeebe отклоняет второе сообщение с тем же
    /// идентификатором, поэтому одновременные сигналы запускают один возврат. Если возврат уже зарегистрирован,
    /// сигнал сообщение не публикует.
    /// </summary>
    /// <returns><c>false</c>, если возврат по платежу уже идёт.</returns>
    /// <exception cref="RefundNotAllowedException">Платёж не проведён или возврат по нему уже выполнен.</exception>
    public async Task<bool> PublishFraudDetectedAsync(string paymentId, FraudDetectedRequest signal, CancellationToken ct)
    {
        await refunds.EnsureRefundableAsync(paymentId, ct);
        if (await refunds.GetAsync(paymentId, ct) is { } running)
        {
            log.LogInformation("{PaymentId} повторный сигнал о мошенничестве: возврат уже идёт ({Status}), второй не запускается",
                paymentId, running.Status);
            return false;
        }
        var payment = (await payments.GetAsync(paymentId, ct))!;
        var variables = new
        {
            paymentId,
            amount = payment.Amount,
            currency = payment.Currency,
            clientId = payment.ClientId,
            counterpartyId = payment.CounterpartyId,
            transferExecutionWindow = signal.TransferExecutionWindow,
            clockSkewMargin = signal.ClockSkewMargin,
            emulate = signal.Emulate ?? new StandInScenario(),
        };
        try
        {
            await zeebe.NewPublishMessageCommand().MessageName(FraudDetected).CorrelationKey(paymentId)
                .MessageId($"refund:{paymentId}").TimeToLive(settings.StartMessageTtl)
                .Variables(JsonSerializer.Serialize(variables, JobContext.Json)).Send(ct);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.AlreadyExists)
        {
            log.LogInformation("{PaymentId} повторный сигнал о мошенничестве: сообщение уже принято Zeebe, второй возврат не запускается", paymentId);
            return false;
        }
        log.LogInformation("{PaymentId} мошенничество обнаружено после перевода, отправлено сообщение {Message}", paymentId, FraudDetected);
        return true;
    }

    /// <returns>Номер новой попытки возврата.</returns>
    /// <exception cref="RefundNotAllowedException">Новые реквизиты по этому платежу не запрашивались.</exception>
    public async Task<int> PublishDetailsProvidedAsync(string paymentId, CancellationToken ct)
    {
        var attempt = await refunds.NextAttemptForNewDetailsAsync(paymentId, ct);
        await zeebe.NewPublishMessageCommand().MessageName(DetailsProvided).CorrelationKey(paymentId)
            .TimeToLive(DetailsTimeToLive).Variables(JsonSerializer.Serialize(new { refundAttempt = attempt }, JobContext.Json)).Send(ct);
        log.LogInformation("{PaymentId} клиент указал новые реквизиты, попытка возврата {Attempt}", paymentId, attempt);
        return attempt;
    }
}
