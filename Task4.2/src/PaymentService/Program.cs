using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Npgsql;
using PaymentService;
using PaymentService.Data;
using PaymentService.FraudCheck;
using PaymentService.Notifications;
using PaymentService.Payments;
using PaymentService.Process;
using Zeebe.Client;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.FormatterName = CompactLogFormatter.Name).AddConsoleFormatter<CompactLogFormatter, ConsoleFormatterOptions>();
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
// zb-client пишет ошибку опроса на каждую попытку, пока Zeebe недоступен; недоступность видна по нашим строкам лога
builder.Logging.AddFilter("Zeebe", LogLevel.Critical);
builder.Logging.AddFilter("Grpc", LogLevel.Error);

var missing = new[] { "PAYMENTS_DB", "ZEEBE_ADDRESS", "FRAUD_CHECK_URL", "NOTIFICATIONS_URL" }.Where(name => string.IsNullOrWhiteSpace(config[name])).ToList();
if (missing.Count > 0) return Fail("не заданы " + string.Join(", ", missing));
var settings = new ProcessSettings(
    CheckTimeout: config.GetValue("Checks:Timeout", TimeSpan.FromSeconds(3)),
    RecheckTimeout: config.GetValue("Checks:RecheckTimeout", TimeSpan.FromMilliseconds(500)),
    ManualReviewTimeout: config["ManualReview:Timeout"] ?? "PT20M",
    RetryBackoff: config.GetValue("Retries:Backoff", TimeSpan.FromSeconds(1)),
    RetryMaxBackoff: config.GetValue("Retries:MaxBackoff", TimeSpan.FromMinutes(5)),
    CheckHandlerThreads: config.GetValue<byte>("Workers:CheckHandlerThreads", 32),
    CheckMaxJobsActive: config.GetValue("Workers:CheckMaxJobsActive", 64),
    StartMessageTtl: config.GetValue("Start:MessageTtl", TimeSpan.FromHours(24)));
var paymentSettings = new PaymentSettings(
    TransferExecutionWindow: config.GetValue("Transfer:ExecutionWindow", TimeSpan.FromSeconds(30)),
    ClockSkewMargin: config.GetValue("Transfer:ClockSkewMargin", TimeSpan.FromSeconds(30)),
    MaxUnexecutedRefundSends: config.GetValue("Refund:MaxUnexecutedSends", 3));
// сроки в запросе нужны только стенду: сервис принимает их, если это включено, и только короче своих
var demoTimings = config.GetValue("Demo:TimingOverrides", false);
var manualReviewLimit = XmlConvert.ToTimeSpan(settings.ManualReviewTimeout);

using (var migrationLog = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning)
           .AddConsole(o => o.FormatterName = CompactLogFormatter.Name).AddConsoleFormatter<CompactLogFormatter, ConsoleFormatterOptions>()))
{
    if (!SchemaMigrations.Apply(config["PAYMENTS_DB"]!, migrationLog)) return Fail("миграции схемы не применены");
}

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(config["PAYMENTS_DB"]!));
builder.Services.AddSingleton<PaymentStore>();
builder.Services.AddSingleton<PaymentOperations>();
builder.Services.AddSingleton<RefundStore>();
builder.Services.AddSingleton<RefundOperations>();
// Банк в прототипе — заглушка внутри сервиса; FraudCheck и Notification — отдельные сервисы, их заглушкам
// провайдеров сценарий платежа из переменной emulate уходит в запросе
builder.Services.AddSingleton<StandInScenarios>();
builder.Services.AddSingleton<IJobObserver, StandInScenarioObserver>();
builder.Services.AddSingleton<IBank, BankStandIn>();
// сроки вызовов задают обработчики шагов, поэтому общий тайм-аут HttpClient выключен
builder.Services.AddHttpClient(FraudCheckHttpClient.Name, c => { c.BaseAddress = new Uri(config["FRAUD_CHECK_URL"]!); c.Timeout = Timeout.InfiniteTimeSpan; });
builder.Services.AddHttpClient(NotificationHttpClient.Name, c => { c.BaseAddress = new Uri(config["NOTIFICATIONS_URL"]!); c.Timeout = TimeSpan.FromSeconds(5); });
builder.Services.AddSingleton<IFraudCheckClient, FraudCheckHttpClient>();
builder.Services.AddSingleton<INotificationClient, NotificationHttpClient>();
builder.Services.AddSingleton<IZeebeClient>(sp => ZeebeClient.Builder()
    .UseLoggerFactory(sp.GetRequiredService<ILoggerFactory>())
    .UseGatewayAddress(config["ZEEBE_ADDRESS"])
    .UsePlainText()
    .Build());
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(paymentSettings);
builder.Services.AddSingleton<ProcessModels>();
builder.Services.AddSingleton<ProcessStarter>();
builder.Services.AddSingleton<PaymentSteps>();
builder.Services.AddSingleton<CheckSteps>();
builder.Services.AddSingleton<NotificationSteps>();
builder.Services.AddSingleton<RefundSteps>();
builder.Services.AddSingleton<RefundMessages>();
builder.Services.AddHostedService<JobWorkers>();

var app = builder.Build();

app.MapPost("/payments", async (PaymentRequest request, [FromHeader(Name = "Idempotency-Key")] string? requestKey,
    ProcessStarter starter, ProcessModels models, CancellationToken ct) =>
{
    if (!models.Deployed) return NotReady();
    if (string.IsNullOrWhiteSpace(requestKey)) return Results.BadRequest(new { error = "нужен заголовок Idempotency-Key" });
    var error = Validate(request) ?? ValidateTimings(demoTimings,
        ("manualReviewTimeout", request.ManualReviewTimeout, manualReviewLimit),
        ("transferExecutionWindow", request.TransferExecutionWindow, paymentSettings.TransferExecutionWindow),
        ("clockSkewMargin", request.ClockSkewMargin, paymentSettings.ClockSkewMargin));
    if (error is not null) return Results.BadRequest(new { error });
    var started = await starter.StartAsync(request, requestKey, ct);
    return started.Outcome switch
    {
        StartOutcome.Conflict => Results.UnprocessableEntity(new { error = "ключ Idempotency-Key уже использован для другого платежа" }),
        StartOutcome.NotStarted => Results.Json(new { error = "процесс не запущен, повторите запрос с тем же ключом", paymentId = started.PaymentId },
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Accepted($"/payments/{started.PaymentId}", new { paymentId = started.PaymentId, processInstanceKey = started.ProcessInstanceKey }),
    };
});

app.MapGet("/payments/{id}", async (string id, PaymentOperations payments, RefundOperations refunds, CancellationToken ct) =>
{
    var payment = await payments.GetAsync(id, ct);
    if (payment is null) return Results.NotFound();
    var operations = await payments.OperationsAsync(id, ct);
    var refund = await refunds.GetAsync(id, ct);
    var durationMs = payment.FinishedAt is { } finished ? (long?)(finished - payment.AcceptedAt).TotalMilliseconds : null;
    var processInstanceKey = await payments.FindProcessKeyAsync(id, ct);
    return Results.Ok(new { payment, processInstanceKey, durationMs, refund, operations });
});

// Поздний сигнал о мошенничестве после перевода продавцу: запускает процесс «Возврат постфактум».
app.MapPost("/payments/{id}/fraud-detected", async (string id, FraudDetectedRequest request, RefundMessages messages,
    ProcessModels models, CancellationToken ct) =>
{
    if (!models.Deployed) return NotReady();
    var error = ValidateTimings(demoTimings,
        ("transferExecutionWindow", request.TransferExecutionWindow, paymentSettings.TransferExecutionWindow),
        ("clockSkewMargin", request.ClockSkewMargin, paymentSettings.ClockSkewMargin));
    if (error is not null) return Results.BadRequest(new { error });
    try
    {
        var started = await messages.PublishFraudDetectedAsync(id, request, ct);
        return Results.Accepted($"/payments/{id}", new { paymentId = id, started });
    }
    catch (RefundNotAllowedException e)
    {
        return Results.Conflict(new { error = e.Message });
    }
});

app.MapPost("/payments/{id}/refund-details", async (string id, RefundMessages messages, CancellationToken ct) =>
{
    try
    {
        var attempt = await messages.PublishDetailsProvidedAsync(id, ct);
        return Results.Accepted($"/payments/{id}", new { paymentId = id, refundAttempt = attempt });
    }
    catch (RefundNotAllowedException e)
    {
        return Results.Conflict(new { error = e.Message });
    }
});

app.Logger.LogInformation("payment-service запущен, схема БД актуальна");
app.Run();
return 0;

static string? Validate(PaymentRequest r)
{
    if (r.Amount <= 0) return "amount: ожидается сумма больше нуля";
    if (!Regex.IsMatch(r.Currency ?? "", "^[A-Z]{3}$")) return "currency: ожидается код валюты ISO 4217";
    if (string.IsNullOrWhiteSpace(r.ClientId) || string.IsNullOrWhiteSpace(r.CounterpartyId)) return "clientId и counterpartyId обязательны";
    return null;
}

static string? ValidateTimings(bool allowed, params (string Name, string? Value, TimeSpan Limit)[] timings)
{
    foreach (var (name, value, limit) in timings)
    {
        if (value is null) continue;
        if (!allowed) return $"{name}: срок задаёт сервис";
        TimeSpan requested;
        try { requested = XmlConvert.ToTimeSpan(value); }
        catch (FormatException) { return $"{name}: ожидается длительность ISO 8601, например PT20M"; }
        if (requested <= TimeSpan.Zero || requested >= limit) return $"{name}: срок можно только сократить, он должен быть меньше {XmlConvert.ToString(limit)}";
    }
    return null;
}

static IResult NotReady() => Results.Json(new { error = "схемы процессов ещё не развёрнуты, повторите запрос" },
    statusCode: StatusCodes.Status503ServiceUnavailable);

static int Fail(string reason)
{
    Console.Error.WriteLine($"payment-service не запущен: {reason}");
    return 1;
}

/// <summary>Одна строка на событие: время, уровень, сообщение.</summary>
internal sealed class CompactLogFormatter() : ConsoleFormatter(Name)
{
    public new const string Name = "compact";

    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter writer)
    {
        var message = entry.Formatter?.Invoke(entry.State, entry.Exception);
        if (string.IsNullOrEmpty(message)) return;
        var level = entry.LogLevel switch
        {
            LogLevel.Warning => "WARN ",
            LogLevel.Error or LogLevel.Critical => "ERROR",
            _ => "INFO ",
        };
        writer.Write($"{DateTime.Now:HH:mm:ss.fff} {level} {message}");
        if (entry.Exception is not null) writer.Write($" ({entry.Exception.GetType().Name}: {entry.Exception.Message})");
        writer.WriteLine();
    }
}
