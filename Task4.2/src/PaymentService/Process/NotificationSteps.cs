using PaymentService.Notifications;

namespace PaymentService.Process;

/// <summary>Уведомления клиенту и службе безопасности. Какое событие — задаёт заголовок задачи event.</summary>
public sealed class NotificationSteps(INotificationClient notifications, ILogger<NotificationSteps> log)
{
    [JobType("NOTIFY_CLIENT")]
    public async Task<StepResult> NotifyClient(JobContext job, CancellationToken ct)
    {
        var text = job.Header("event") switch
        {
            "PAYMENT_COMPLETED" => "платёж проведён",
            "PAYMENT_REJECTED" => "платёж отклонён, деньги разморожены",
            "REFUND_DETAILS_REQUESTED" => "банк не принял возврат, укажите новые реквизиты",
            "REFUND_COMPLETED" => "деньги по платежу возвращены",
            var other => throw new InvalidOperationException($"неизвестное событие уведомления {other}"),
        };
        await SendAsync(job, job.String("clientId") ?? "-", text, ct);
        return new StepResult.Done();
    }

    [JobType("NOTIFY_SECURITY")]
    public async Task<StepResult> NotifySecurity(JobContext job, CancellationToken ct)
    {
        var text = job.Header("event") switch
        {
            "CUT_OFF_APPROVED" => $"платёж проведён без ответа проверки ({SilentCheck(job)})",
            "PAYMENT_REJECTED" => $"платёж отклонён, причина {job.String("rejectReason")}",
            "REFUND_COMPLETED" => $"возврат постфактум выполнен: с продавца удержано {job.Get<decimal>("recoveredAmount"):0.00}, долг продавца {job.Get<decimal>("debtAmount"):0.00}",
            var other => throw new InvalidOperationException($"неизвестное событие уведомления {other}"),
        };
        await SendAsync(job, "служба безопасности", text, ct);
        return new StepResult.Done();
    }

    private async Task SendAsync(JobContext job, string recipient, string text, CancellationToken ct)
    {
        var attempt = job.Get<int>("refundAttempt") is > 0 and var n ? $":{n}" : "";
        var sent = await notifications.SendAsync(job.PaymentId, $"{job.PaymentId}:{job.Step}:{job.Header("event")}{attempt}", recipient, text, ct);
        log.LogInformation("{PaymentId} {Step}: {Recipient} — «{Text}»{Duplicate}",
            job.PaymentId, job.Step, recipient, text, sent ? "" : " (повтор, уже отправлено)");
    }

    private static string SilentCheck(JobContext job)
    {
        var silent = new List<string>();
        if (job.String("fraudDecision") == "CUT_OFF") silent.Add("антифрод");
        if (job.String("fraudDecision") == "MANUAL" && job.String("reviewDecision") is null) silent.Add("оператор");
        if (job.String("externalDecision") == "CUT_OFF") silent.Add("внешние проверки");
        return silent.Count > 0 ? "молчали: " + string.Join(", ", silent) : "cut-off";
    }
}
