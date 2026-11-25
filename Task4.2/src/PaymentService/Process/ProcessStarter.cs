using System.Text.Json;
using Grpc.Core;
using PaymentService.Payments;
using Zeebe.Client;

namespace PaymentService.Process;

/// <param name="ManualReviewTimeout">Срок ручной проверки, ISO 8601; в демо его сокращают.</param>
/// <param name="TransferExecutionWindow">Срок исполнения перевода банком, ISO 8601; в демо его сокращают.</param>
/// <param name="ClockSkewMargin">Запас на расхождение часов с банком, ISO 8601; в демо его сокращают.</param>
public sealed record PaymentRequest(
    decimal Amount, string Currency, string ClientId, string CounterpartyId,
    string? ManualReviewTimeout = null, string? TransferExecutionWindow = null, string? ClockSkewMargin = null,
    StandInScenario? Emulate = null);

/// <param name="CheckTimeout">Срок ответа антифрода и внешних проверок, дальше — cut-off.</param>
/// <param name="RecheckTimeout">Срок ответа FraudCheck при повторном запросе решений после cut-off.</param>
/// <param name="ManualReviewTimeout">Срок ручной проверки по умолчанию, ISO 8601.</param>
/// <param name="RetryBackoff">Первая пауза перед повтором задачи после сбоя, дальше удваивается.</param>
/// <param name="RetryMaxBackoff">Предел паузы между повторами.</param>
/// <param name="CheckHandlerThreads">Сколько проверок один экземпляр сервиса ждёт одновременно.</param>
/// <param name="CheckMaxJobsActive">Сколько задач проверок обработчик держит за собой.</param>
/// <param name="StartMessageTtl">Сколько Zeebe помнит сообщение о старте платежа или возврата и отклоняет его повтор.</param>
public sealed record ProcessSettings(
    TimeSpan CheckTimeout, TimeSpan RecheckTimeout, string ManualReviewTimeout, TimeSpan RetryBackoff, TimeSpan RetryMaxBackoff,
    byte CheckHandlerThreads, int CheckMaxJobsActive, TimeSpan StartMessageTtl);

public enum StartOutcome { Started, Repeated, Conflict, NotStarted }

public sealed record StartResult(StartOutcome Outcome, string PaymentId, long? ProcessInstanceKey);

/// <summary>
/// Запускает процесс payment-process сообщением payment-initiated. Идентификатор сообщения — paymentId:
/// Zeebe отклоняет второе сообщение с тем же идентификатором, пока помнит первое, поэтому одновременные повторы
/// и повтор после потерянного ответа не создают второй экземпляр. Когда «Создать платёж» записал экземпляр,
/// повтор запроса сообщение уже не публикует.
/// </summary>
public sealed class ProcessStarter(IZeebeClient zeebe, PaymentOperations payments, ProcessSettings settings, ILogger<ProcessStarter> log)
{
    public const string ProcessId = "payment-process";
    public const string StartMessage = "payment-initiated";

    public async Task<StartResult> StartAsync(PaymentRequest request, string requestKey, CancellationToken ct)
    {
        var registered = await payments.RegisterRequestAsync(requestKey, request.Amount, request.Currency,
            request.ClientId, request.CounterpartyId, Guid.NewGuid().ToString(), ct);
        if (registered.Conflict)
        {
            log.LogWarning("{PaymentId} ключ {RequestKey} уже использован для другого платежа, запрос отклонён", registered.PaymentId, requestKey);
            return new StartResult(StartOutcome.Conflict, registered.PaymentId, null);
        }
        if (registered.ProcessInstanceKey is { } existing)
        {
            log.LogInformation("{PaymentId} повторный запрос с тем же ключом, процесс {ProcessInstanceKey} уже запущен", registered.PaymentId, existing);
            return new StartResult(StartOutcome.Repeated, registered.PaymentId, existing);
        }

        var paymentId = registered.PaymentId;
        try
        {
            await zeebe.NewPublishMessageCommand().MessageName(StartMessage).CorrelationKey(paymentId)
                .MessageId(paymentId).TimeToLive(settings.StartMessageTtl)
                .Variables(JsonSerializer.Serialize(Variables(paymentId, request), JobContext.Json)).Send(ct);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.AlreadyExists)
        {
            log.LogInformation("{PaymentId} повторный запрос: сообщение о старте уже принято Zeebe, второй экземпляр не создаётся", paymentId);
            return new StartResult(StartOutcome.Repeated, paymentId, await payments.FindProcessKeyAsync(paymentId, ct));
        }
        catch (RpcException e)
        {
            // регистрация остаётся: повтор с тем же ключом опубликует то же сообщение, Zeebe примет его один раз
            log.LogWarning("{PaymentId} Zeebe не принял сообщение о старте ({Status}), запрос можно повторить с тем же ключом", paymentId, e.StatusCode);
            return new StartResult(StartOutcome.NotStarted, paymentId, null);
        }
        if ((request.Emulate ?? new StandInScenario()).Start == "LOST")
        {
            log.LogWarning("{PaymentId} Zeebe принял сообщение о старте, но ответ до сервиса не дошёл (заглушка): маркетплейс получит 503", paymentId);
            return new StartResult(StartOutcome.NotStarted, paymentId, null);
        }
        log.LogInformation("{PaymentId} платёж принят, процесс запущен сообщением {Message}", paymentId, StartMessage);
        return new StartResult(StartOutcome.Started, paymentId, null);
    }

    private object Variables(string paymentId, PaymentRequest request) => new
    {
        paymentId,
        amount = request.Amount,
        currency = request.Currency,
        clientId = request.ClientId,
        counterpartyId = request.CounterpartyId,
        acceptedAt = DateTimeOffset.UtcNow,
        manualReviewTimeout = request.ManualReviewTimeout ?? settings.ManualReviewTimeout,
        transferExecutionWindow = request.TransferExecutionWindow,
        clockSkewMargin = request.ClockSkewMargin,
        cutOff = false,
        emulate = request.Emulate ?? new StandInScenario(),
    };
}
