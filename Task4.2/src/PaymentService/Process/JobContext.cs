using System.Text.Json;
using Zeebe.Client.Api.Responses;

namespace PaymentService.Process;

/// <summary>Обработчик задачи процесса с типом из zeebe:taskDefinition.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class JobTypeAttribute(string type) : Attribute
{
    public string Type { get; } = type;
}

/// <summary>Итог обработчика: завершить задачу, бросить BPMN-ошибку или вернуть задачу Zeebe на повтор.</summary>
public abstract record StepResult
{
    public sealed record Done(object? Variables = null) : StepResult;

    public sealed record BpmnError(string Code, string Message, object? Variables = null) : StepResult;

    /// <summary>Технический сбой: Zeebe повторит задачу после растущей паузы, когда попытки кончатся — инцидент.</summary>
    public sealed record Retry(string Reason) : StepResult;

    /// <summary>Итога ещё нет: спросить снова после паузы, попытка не расходуется.</summary>
    public sealed record RetryLater(string Reason, TimeSpan Pause) : StepResult;

    /// <summary>Повтор не поможет: сразу инцидент в Operate.</summary>
    public sealed record Incident(string Reason) : StepResult;
}

/// <summary>Видит каждую задачу до обработчика.</summary>
public interface IJobObserver
{
    void Observe(JobContext job);
}

/// <summary>Переменные и заголовки задачи процесса.</summary>
public sealed class JobContext(IJob job)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly JsonElement variables = JsonDocument.Parse(job.Variables).RootElement;
    private readonly JsonElement headers = JsonDocument.Parse(string.IsNullOrEmpty(job.CustomHeaders) ? "{}" : job.CustomHeaders).RootElement;

    public string Step => job.Type;

    public long ProcessInstanceKey => job.ProcessInstanceKey;

    public string PaymentId => String("paymentId") ?? throw new InvalidOperationException("в задаче нет paymentId");

    public string? String(string name) =>
        variables.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public bool Flag(string name) => variables.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public T? Get<T>(string name) =>
        variables.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.Deserialize<T>(Json) : default;

    /// <summary>Длительность ISO 8601 из переменной, если она задана.</summary>
    public TimeSpan? Duration(string name) => String(name) is { } value ? System.Xml.XmlConvert.ToTimeSpan(value) : null;

    public string? Header(string name) =>
        headers.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
