using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Grpc.Core;
using Zeebe.Client;
using Zeebe.Client.Api.Responses;
using Zeebe.Client.Api.Worker;

namespace PaymentService.Process;

using Handler = Func<JobContext, CancellationToken, Task<StepResult>>;

/// <summary>Подключается к Zeebe и открывает job worker на каждый тип задачи процесса payment-process.</summary>
public sealed class JobWorkers(
    IZeebeClient zeebe, PaymentSteps payments, CheckSteps checks, NotificationSteps notifications, RefundSteps refunds,
    ProcessModels models, IEnumerable<IJobObserver> observers, ProcessSettings settings, ILogger<JobWorkers> log) : BackgroundService
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(30);
    private readonly List<IJobWorker> workers = [];
    // сбои подряд по ключу задачи: пауза перед повтором удваивается до RetryMaxBackoff
    private readonly ConcurrentDictionary<long, int> failures = new();

    /// <summary>Типы задач, у которых есть обработчик.</summary>
    public static IReadOnlyList<string> JobTypes { get; } = [.. StepClasses.SelectMany(HandlerMethods).Select(m => m.Type)];

    private static Type[] StepClasses => [typeof(PaymentSteps), typeof(CheckSteps), typeof(NotificationSteps), typeof(RefundSteps)];

    private static IEnumerable<(string Type, MethodInfo Method)> HandlerMethods(Type steps) =>
        steps.GetMethods().Select(m => (m.GetCustomAttribute<JobTypeAttribute>()?.Type, m))
            .Where(x => x.Type is not null).Select(x => (x.Type!, x.m));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var topology = await WaitForGatewayAsync(stoppingToken);
        log.LogInformation("подключён к Zeebe {Version}", topology.GatewayVersion);
        await models.DeployAsync(stoppingToken);
        foreach (var steps in new object[] { payments, checks, notifications, refunds })
        foreach (var (type, method) in HandlerMethods(steps.GetType()))
        {
            var handler = method.CreateDelegate<Handler>(steps);
            // проверки ждут ответа до нескольких секунд, поэтому им нужно больше параллельных обработчиков
            var (threads, maxJobs) = steps is CheckSteps ? (settings.CheckHandlerThreads, settings.CheckMaxJobsActive) : ((byte)4, 32);
            workers.Add(zeebe.NewWorker()
                .JobType(type)
                .Handler((client, job) => RunAsync(client, job, handler, stoppingToken))
                .MaxJobsActive(maxJobs)
                .HandlerThreads(threads)
                .Timeout(JobTimeout)
                .PollInterval(TimeSpan.FromMilliseconds(100))
                .StreamEnabled(true)
                // без явного срока шлюз 8.5 отклоняет поток задач: «timeout ... but it was 0»
                .StreamTimeout(JobTimeout)
                .Name("payment-service")
                .Open());
        }
        log.LogInformation("обработчики шагов: {Types}", string.Join(", ", JobTypes));
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
    }

    public override void Dispose()
    {
        foreach (var worker in workers) worker.Dispose();
        base.Dispose();
    }

    private TimeSpan Backoff(int failuresInRow) =>
        TimeSpan.FromTicks(Math.Min(settings.RetryBackoff.Ticks << Math.Min(failuresInRow - 1, 20), settings.RetryMaxBackoff.Ticks));

    private static string Serialize(object? variables) => JsonSerializer.Serialize(variables, JobContext.Json);

    private async Task<ITopology> WaitForGatewayAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                return await zeebe.TopologyRequest().Send(TimeSpan.FromSeconds(5), ct);
            }
            catch (RpcException e) when (!ct.IsCancellationRequested)
            {
                log.LogInformation("Zeebe ещё недоступен ({Status}), жду", e.StatusCode);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    private async Task RunAsync(IJobClient client, IJob job, Handler handler, CancellationToken ct)
    {
        var context = new JobContext(job);
        var paymentId = context.String("paymentId") ?? "-";
        foreach (var observer in observers) observer.Observe(context);
        StepResult result;
        try
        {
            result = await handler(context, ct);
        }
        catch (Exception e)
        {
            result = new StepResult.Retry(e.Message);
        }

        try
        {
            switch (result)
            {
                case StepResult.Done { Variables: null }:
                    await client.NewCompleteJobCommand(job.Key).Send(ct);
                    break;
                case StepResult.Done done:
                    await client.NewCompleteJobCommand(job.Key).Variables(Serialize(done.Variables)).Send(ct);
                    break;
                case StepResult.BpmnError { Variables: null } error:
                    await client.NewThrowErrorCommand(job.Key).ErrorCode(error.Code).ErrorMessage(error.Message).Send(ct);
                    break;
                case StepResult.BpmnError error:
                    await client.NewThrowErrorCommand(job.Key).ErrorCode(error.Code).ErrorMessage(error.Message)
                        .Variables(Serialize(error.Variables)).Send(ct);
                    break;
                case StepResult.RetryLater later:
                    await client.NewFailCommand(job.Key).Retries(job.Retries).ErrorMessage(later.Reason).RetryBackOff(later.Pause).Send(ct);
                    log.LogInformation("{PaymentId} {Step}: {Reason}, спросим снова через {Pause} с", paymentId, job.Type, later.Reason, later.Pause.TotalSeconds);
                    return;
                case StepResult.Retry retry:
                    var left = Math.Max(job.Retries - 1, 0);
                    var pause = Backoff(failures.AddOrUpdate(job.Key, 1, (_, n) => n + 1));
                    await client.NewFailCommand(job.Key).Retries(left).ErrorMessage(retry.Reason).RetryBackOff(pause).Send(ct);
                    if (left > 0)
                    {
                        log.LogWarning("{PaymentId} {Step}: {Reason}, повтор через {Pause} с (осталось попыток: {Left})",
                            paymentId, job.Type, retry.Reason, pause.TotalSeconds, left);
                        return;
                    }
                    log.LogError("{PaymentId} {Step}: {Reason}, попытки кончились — инцидент в Operate, нужен дежурный SRE", paymentId, job.Type, retry.Reason);
                    break;
                case StepResult.Incident incident:
                    await client.NewFailCommand(job.Key).Retries(0).ErrorMessage(incident.Reason).Send(ct);
                    log.LogError("{PaymentId} {Step}: {Reason} — инцидент в Operate", paymentId, job.Type, incident.Reason);
                    break;
            }
            failures.TryRemove(job.Key, out _);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.NotFound)
        {
            // задачу уже закрыл движок: сработал таймер или граница подпроцесса
            log.LogInformation("{PaymentId} {Step}: задача уже закрыта процессом, ответ не нужен", paymentId, job.Type);
        }
    }
}
