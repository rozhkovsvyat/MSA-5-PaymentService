using System.Net.Http.Json;

namespace PaymentService.FraudCheck;

public sealed record CheckRequest(string PaymentId, string ClientId, string CounterpartyId, decimal Amount, string Currency);

public enum AmlSource { Provider, Cache, None }

/// <param name="Aml">Итог AML-скрининга пары клиент–продавец; <c>null</c> — провайдер не ответил и в кеше пусто.</param>
/// <param name="AmlSource">Откуда взят итог AML: от провайдера или последний известный из кеша.</param>
/// <param name="LimitsAndGeo">Итог проверок лимитов и GeoRisk этого платежа; <c>null</c> — провайдер не ответил.</param>
/// <param name="Decision">Итоговое решение внешних проверок, которое хранит FraudCheck Service; запрет не отменяется повтором.</param>
public sealed record ExternalCheckResult(string? Aml, AmlSource AmlSource, string? LimitsAndGeo, string? Decision = null)
{
    public static readonly ExternalCheckResult NoAnswer = new(null, AmlSource.None, null);
}

public sealed record FinalDecisions(string? Fraud, string? External);

/// <summary>FraudCheck Service: антифрод, внешние проверки и итоговые решения по платежу. О процессе платежа не знает.</summary>
public interface IFraudCheckClient
{
    /// <returns>ALLOW, DENY или MANUAL.</returns>
    Task<string> CheckFraudAsync(CheckRequest request, CancellationToken ct);

    /// <summary>AML-скрининг и проверки лимитов и GeoRisk. Срок <paramref name="timeout"/> передаётся сервису; нет ответа к сроку — <see cref="ExternalCheckResult.NoAnswer"/>.</summary>
    Task<ExternalCheckResult> CheckExternalAsync(CheckRequest request, TimeSpan timeout, CancellationToken ct);

    /// <returns>Итоговое решение антифрода после ручной проверки.</returns>
    Task<string> RecordReviewDecisionAsync(string paymentId, string decision, CancellationToken ct);

    Task<FinalDecisions> GetFinalDecisionsAsync(string paymentId, CancellationToken ct);
}

/// <summary>
/// Синхронные вызовы FraudCheck Service по HTTP. Поведение провайдеров проверок в прототипе задаёт сценарий платежа
/// из <see cref="StandInScenarios"/>, он уходит в поле emulate запроса.
/// </summary>
public sealed class FraudCheckHttpClient(IHttpClientFactory http, StandInScenarios scenarios) : IFraudCheckClient
{
    public const string Name = "fraud-check";
    private static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(200);

    public async Task<string> CheckFraudAsync(CheckRequest request, CancellationToken ct)
    {
        var response = await Client.PostAsJsonAsync("fraud-checks", Body(request), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FraudAnswer>(ct))!.Decision;
    }

    public async Task<ExternalCheckResult> CheckExternalAsync(CheckRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        while (true)
        {
            // сервису передаётся остаток срока: он ответит тем, что успел получить от провайдеров
            var left = timeout - (DateTime.UtcNow - started);
            using var message = new HttpRequestMessage(HttpMethod.Post, "external-checks") { Content = JsonContent.Create(Body(request)) };
            message.Headers.Add("Request-Timeout-Ms", ((int)Math.Max(left.TotalMilliseconds, 0)).ToString());
            try
            {
                var response = await Client.SendAsync(message, deadline.Token);
                response.EnsureSuccessStatusCode();
                var answer = (await response.Content.ReadFromJsonAsync<ExternalAnswer>(deadline.Token))!;
                var source = answer.AmlSource switch { "provider" => AmlSource.Provider, "cache" => AmlSource.Cache, _ => AmlSource.None };
                return new ExternalCheckResult(answer.Aml, source, answer.LimitsAndGeo, answer.Decision);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return ExternalCheckResult.NoAnswer;
            }
            catch (HttpRequestException)
            {
                // технический сбой — это тоже «нет ответа»: повторяем, пока не истёк срок
                try { await Task.Delay(RetryPause, deadline.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ExternalCheckResult.NoAnswer; }
            }
        }
    }

    public async Task<string> RecordReviewDecisionAsync(string paymentId, string decision, CancellationToken ct)
    {
        var response = await Client.PostAsJsonAsync($"fraud-checks/{paymentId}/review", new { decision }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReviewAnswer>(ct))!.Decision;
    }

    public async Task<FinalDecisions> GetFinalDecisionsAsync(string paymentId, CancellationToken ct) =>
        (await Client.GetFromJsonAsync<FinalDecisions>($"fraud-checks/{paymentId}/decisions", ct))!;

    private HttpClient Client => http.CreateClient(Name);

    private object Body(CheckRequest request)
    {
        var scenario = scenarios.For(request.PaymentId);
        return new
        {
            request.PaymentId, request.ClientId, request.CounterpartyId, request.Amount, request.Currency,
            Emulate = new { scenario.Fraud, scenario.Aml, scenario.Limits, scenario.ExternalDelay },
        };
    }

    private sealed record FraudAnswer(string Decision);

    private sealed record ExternalAnswer(string? Aml, string AmlSource, string? LimitsAndGeo, string? Decision);

    private sealed record ReviewAnswer(string Decision);
}
