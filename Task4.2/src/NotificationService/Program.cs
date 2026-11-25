using System.Collections.Concurrent;

// Notification Service: уведомления клиенту и службе безопасности. В прототипе уведомление пишется в лог;
// повтор с тем же ключом не отправляется второй раз. Поле emulate.failures — сколько раз подряд отказать (503).
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; o.IncludeScopes = false; });
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

var app = builder.Build();
var sent = new ConcurrentDictionary<string, string>();
var attempts = new ConcurrentDictionary<string, int>();
var log = app.Logger;

app.MapPost("/notifications", (Notification notification) =>
{
    if (attempts.AddOrUpdate(notification.Key, 1, (_, n) => n + 1) <= (notification.Emulate?.Failures ?? 0))
    {
        log.LogWarning("{PaymentId} уведомление {Key} не принято (заглушка отвечает 503)", notification.PaymentId, notification.Key);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    var first = sent.TryAdd(notification.Key, notification.Text);
    log.LogInformation("{PaymentId} {Recipient}: {Text}{Repeat}", notification.PaymentId, notification.Recipient, notification.Text,
        first ? "" : " (повтор, второй раз не отправляется)");
    return Results.Ok(new { sent = first });
});

app.Run();

internal sealed record NotificationBehaviour(int Failures = 0);

internal sealed record Notification(string PaymentId, string Key, string Recipient, string Text, NotificationBehaviour? Emulate = null);
