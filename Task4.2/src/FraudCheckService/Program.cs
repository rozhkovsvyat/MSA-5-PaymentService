using FraudCheckService.Checks;
using StackExchange.Redis;

// FraudCheck Service: антифрод и внешние проверки (AML, лимиты, GeoRisk). Провайдеры проверок в прототипе — заглушки,
// их ответы задаёт поле emulate запроса. Кеш последних результатов AML — Redis.
var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; o.IncludeScopes = false; });
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

if (string.IsNullOrWhiteSpace(config["REDIS"]))
{
    Console.Error.WriteLine("fraud-check-service не запущен: не задан REDIS");
    return 1;
}
var redis = ConfigurationOptions.Parse(config["REDIS"]!);
var redisTimeout = (int)config.GetValue("Redis:Timeout", TimeSpan.FromMilliseconds(300)).TotalMilliseconds;
redis.AbortOnConnectFail = false;
redis.ConnectTimeout = redis.SyncTimeout = redis.AsyncTimeout = redisTimeout;
redis.BacklogPolicy = BacklogPolicy.FailFast; // недоступный Redis не должен съедать срок проверки
// ответ уходит чуть раньше срока вызывающего, чтобы успеть дойти до него
var responseMargin = config.GetValue("Checks:ResponseMargin", TimeSpan.FromMilliseconds(100));

builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
builder.Services.AddSingleton(sp => new AmlScreeningCache(sp.GetRequiredService<IConnectionMultiplexer>(),
    config.GetValue("Aml:CacheTtl", TimeSpan.FromHours(24)), sp.GetRequiredService<ILogger<AmlScreeningCache>>()));
builder.Services.AddSingleton<FraudChecks>();

var app = builder.Build();

app.MapPost("/fraud-checks", async (CheckRequest request, FraudChecks checks, HttpContext http) =>
    Results.Ok(new FraudResult(await checks.CheckFraudAsync(request, http.RequestAborted))));

// Request-Timeout-Ms — сколько вызывающий готов ждать ответа (протяжка дедлайна)
app.MapPost("/external-checks", async (CheckRequest request, FraudChecks checks, HttpContext http) =>
{
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
    if (int.TryParse(http.Request.Headers["Request-Timeout-Ms"], out var timeoutMs))
        deadline.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs) - responseMargin);
    return Results.Ok(await checks.CheckExternalAsync(request, deadline.Token));
});

app.MapPost("/fraud-checks/{paymentId}/review", (string paymentId, ReviewDecision review, FraudChecks checks) =>
    Results.Ok(new ReviewDecision(checks.RecordReview(paymentId, review.Decision))));

app.MapGet("/fraud-checks/{paymentId}/decisions", (string paymentId, FraudChecks checks) => Results.Ok(checks.Decisions(paymentId)));

app.Run();
return 0;
