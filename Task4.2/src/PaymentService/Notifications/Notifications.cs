using System.Net;
using System.Net.Http.Json;

namespace PaymentService.Notifications;

/// <summary>Notification Service: уведомления клиенту и службе безопасности. О процессе платежа не знает.</summary>
public interface INotificationClient
{
    /// <returns><c>false</c>, если уведомление с этим ключом уже отправлено.</returns>
    /// <exception cref="NotificationFailedException">Сервис не принял уведомление.</exception>
    Task<bool> SendAsync(string paymentId, string key, string recipient, string text, CancellationToken ct);
}

public sealed class NotificationFailedException(string message) : Exception(message);

/// <summary>
/// Синхронный вызов Notification Service по HTTP. Повтор с тем же ключом сервис второй раз не отправляет.
/// Сколько раз подряд сервису отказать в прототипе, задаёт сценарий платежа из <see cref="StandInScenarios"/>.
/// </summary>
public sealed class NotificationHttpClient(IHttpClientFactory http, StandInScenarios scenarios) : INotificationClient
{
    public const string Name = "notifications";

    public async Task<bool> SendAsync(string paymentId, string key, string recipient, string text, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.CreateClient(Name).PostAsJsonAsync("notifications", new
            {
                paymentId, key, recipient, text,
                emulate = new { failures = scenarios.For(paymentId).NotifyFailures },
            }, ct);
        }
        catch (HttpRequestException e)
        {
            throw new NotificationFailedException($"Notification Service недоступен: {e.Message}");
        }
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable || !response.IsSuccessStatusCode)
            throw new NotificationFailedException($"Notification Service ответил {(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<SendAnswer>(ct))!.Sent;
    }

    private sealed record SendAnswer(bool Sent);
}
