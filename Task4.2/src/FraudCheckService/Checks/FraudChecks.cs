using System.Collections.Concurrent;
using System.Xml;

namespace FraudCheckService.Checks;

/// <summary>Как отвечают провайдеры проверок в прототипе; задаётся в запросе вместо настоящих правил и провайдеров.</summary>
/// <param name="Fraud">ALLOW | DENY | MANUAL | SILENT | SILENT_DENY (молчит, но потом сохраняет запрет).</param>
/// <param name="Aml">ALLOW | DENY | SILENT.</param>
/// <param name="Limits">Лимиты и GeoRisk: ALLOW | DENY | SILENT.</param>
/// <param name="ExternalDelay">Задержка ответа внешних провайдеров, ISO 8601.</param>
public sealed record ProviderBehaviour(string Fraud = "ALLOW", string Aml = "ALLOW", string Limits = "ALLOW", string? ExternalDelay = null);

public sealed record CheckRequest(
    string PaymentId, string ClientId, string CounterpartyId, decimal Amount, string Currency, ProviderBehaviour? Emulate = null);

public sealed record FraudResult(string Decision);

/// <param name="AmlSource">provider — ответил AML-провайдер, cache — последний известный результат, none — ответа нет.</param>
/// <param name="Decision">Итоговое решение внешних проверок по платежу. Запрет повторным запросом не отменяется.</param>
public sealed record ExternalResult(string? Aml, string AmlSource, string? LimitsAndGeo, string? Decision);

public sealed record ReviewDecision(string Decision);

public sealed record FinalDecisions(string? Fraud, string? External);

/// <summary>
/// Антифрод, внешние проверки и итоговые решения по платежу. О процессе платежа сервис не знает: на каждый запрос
/// он отвечает решением и хранит итоговое решение, которое потом можно запросить повторно.
/// </summary>
public sealed class FraudChecks(AmlScreeningCache amlCache, ILogger<FraudChecks> log)
{
    private readonly ConcurrentDictionary<string, string> fraudDecisions = new();
    private readonly ConcurrentDictionary<string, string> externalDecisions = new();

    public async Task<string> CheckFraudAsync(CheckRequest request, CancellationToken ct)
    {
        var decision = (request.Emulate ?? new ProviderBehaviour()).Fraud;
        if (decision == "SILENT_DENY")
        {
            fraudDecisions[request.PaymentId] = "DENY";
            log.LogInformation("{PaymentId} антифрод: правила сработали поздно, сохранён запрет, ответа вызывающему нет", request.PaymentId);
        }
        if (decision is "SILENT" or "SILENT_DENY") await Task.Delay(Timeout.Infinite, ct);
        // запрет окончателен: повторный запрос его не отменяет
        if (decision == "DENY") fraudDecisions[request.PaymentId] = decision;
        else if (decision != "MANUAL") fraudDecisions.TryAdd(request.PaymentId, decision);
        if (fraudDecisions.GetValueOrDefault(request.PaymentId) == "DENY") decision = "DENY";
        log.LogInformation("{PaymentId} антифрод: {Decision}", request.PaymentId, decision);
        return decision;
    }

    /// <summary>
    /// AML-скрининг пары клиент–продавец и проверки лимитов и GeoRisk платежа. Отвечает к сроку <paramref name="deadline"/>:
    /// если AML-провайдер не успел, берётся последний известный результат из кеша; лимиты и GeoRisk не кешируются.
    /// </summary>
    public async Task<ExternalResult> CheckExternalAsync(CheckRequest request, CancellationToken deadline)
    {
        var behaviour = request.Emulate ?? new ProviderBehaviour();
        var cached = amlCache.GetAsync(request.ClientId, request.CounterpartyId);
        var delay = behaviour.ExternalDelay is { } d ? XmlConvert.ToTimeSpan(d) : TimeSpan.Zero;
        var amlAnswer = ProviderAsync(behaviour.Aml, delay, deadline);
        var limitsAnswer = ProviderAsync(behaviour.Limits, delay, deadline);

        var aml = await amlAnswer;
        var source = "provider";
        if (aml is not null) await amlCache.PutAsync(request.ClientId, request.CounterpartyId, aml);
        else (aml, source) = await cached is { } last ? (last, "cache") : (null, "none");

        var limits = await limitsAnswer;
        if (aml == "DENY" || limits == "DENY") externalDecisions[request.PaymentId] = "DENY";
        else if (aml is not null && limits is not null) externalDecisions.TryAdd(request.PaymentId, "ALLOW");
        log.LogInformation("{PaymentId} внешние проверки: AML {Aml} ({Source}), лимиты и GeoRisk {Limits}",
            request.PaymentId, aml ?? "нет ответа", source, limits ?? "нет ответа");
        return new ExternalResult(aml, source, limits, externalDecisions.GetValueOrDefault(request.PaymentId));
    }

    /// <returns>Итоговое решение антифрода после ручной проверки: первое записанное.</returns>
    public string RecordReview(string paymentId, string decision)
    {
        var final = fraudDecisions.GetOrAdd(paymentId, decision);
        log.LogInformation("{PaymentId} решение оператора {Decision} сохранено, итоговое решение {Final}", paymentId, decision, final);
        return final;
    }

    public FinalDecisions Decisions(string paymentId) =>
        new(fraudDecisions.GetValueOrDefault(paymentId), externalDecisions.GetValueOrDefault(paymentId));

    private static async Task<string?> ProviderAsync(string answer, TimeSpan delay, CancellationToken deadline)
    {
        try
        {
            await Task.Delay(answer == "SILENT" ? Timeout.InfiniteTimeSpan : delay, deadline);
            return answer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
