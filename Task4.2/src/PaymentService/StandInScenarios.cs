using System.Collections.Concurrent;
using PaymentService.Process;

namespace PaymentService;

/// <summary>
/// Как заглушки банка и FraudCheck ведут себя на конкретном платеже. Приходит в переменной процесса
/// <c>emulate</c>; у настоящих интеграций такого параметра нет.
/// </summary>
public sealed record StandInScenario
{
    public string Start { get; init; } = "OK";         // OK | LOST (Zeebe принял сообщение о старте, ответ до сервиса не дошёл)
    public string Hold { get; init; } = "OK";          // OK | DECLINE | LOST | LATE (запрос дошёл до банка уже после отмены)
    public string Fraud { get; init; } = "ALLOW";      // ALLOW | DENY | MANUAL | SILENT | SILENT_DENY (молчит, но потом сохраняет запрет)
    public string Aml { get; init; } = "ALLOW";        // ALLOW | DENY | SILENT
    public string Limits { get; init; } = "ALLOW";     // лимиты и GeoRisk: ALLOW | DENY | SILENT
    public string? ExternalDelay { get; init; }        // задержка ответа внешних проверок, ISO 8601
    public string Transfer { get; init; } = "OK";      // OK | DECLINE | LOST | NOT_RECEIVED (запрос не дошёл до банка)
    public int ReleaseFailures { get; init; }          // сколько раз подряд банк не ответит на разморозку
    public int NotifyFailures { get; init; }           // сколько раз подряд Notification Service не примет уведомление
    public string Refund { get; init; } = "OK";        // OK | DECLINE (по старым реквизитам) | LOST
    public int RefundNotReceived { get; init; }        // сколько отправок возврата подряд не дойдёт до банка
    public decimal SellerBalance { get; init; } = 1_000_000m; // баланс продавца в банке для удержания возврата
}

public sealed class StandInScenarios
{
    private readonly ConcurrentDictionary<string, StandInScenario> byPayment = new();

    public void Remember(string paymentId, StandInScenario scenario) => byPayment[paymentId] = scenario;

    public StandInScenario For(string paymentId) => byPayment.GetValueOrDefault(paymentId) ?? new StandInScenario();
}

/// <summary>Передаёт заглушкам сценарий платежа из переменных задачи. Подключается только вместе с заглушками.</summary>
public sealed class StandInScenarioObserver(StandInScenarios scenarios) : IJobObserver
{
    public void Observe(JobContext job)
    {
        if (job.String("paymentId") is { } paymentId)
            scenarios.Remember(paymentId, job.Get<StandInScenario>("emulate") ?? new StandInScenario());
    }
}
