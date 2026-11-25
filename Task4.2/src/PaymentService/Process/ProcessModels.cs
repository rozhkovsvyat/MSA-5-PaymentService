using Grpc.Core;
using Zeebe.Client;

namespace PaymentService.Process;

/// <summary>
/// Схемы процессов, которые Payment Service разворачивает в Zeebe сам, до приёма платежей и сигналов о мошенничестве:
/// сообщение о старте, опубликованное раньше схемы, к ней уже не привяжется. Имена ресурсов те же, что у zbctl
/// в deploy-process, иначе Zeebe завёл бы вторую версию той же схемы.
/// </summary>
public sealed class ProcessModels(IZeebeClient zeebe, ILogger<ProcessModels> log)
{
    private static readonly string[] Files = ["process.bpmn", "post-factum-refund.bpmn"];
    private volatile bool deployed;

    public bool Deployed => deployed;

    public async Task DeployAsync(CancellationToken ct)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "models");
        while (true)
        {
            try
            {
                var command = zeebe.NewDeployCommand().AddResourceBytes(await File.ReadAllBytesAsync(Path.Combine(directory, Files[0]), ct), "/" + Files[0]);
                foreach (var file in Files.Skip(1))
                    command = command.AddResourceBytes(await File.ReadAllBytesAsync(Path.Combine(directory, file), ct), "/" + file);
                var response = await command.Send(TimeSpan.FromSeconds(10), ct);
                deployed = true;
                log.LogInformation("схемы развёрнуты: {Processes}",
                    string.Join(", ", response.Processes.Select(p => $"{p.BpmnProcessId} v{p.Version}")));
                return;
            }
            catch (RpcException e) when (!ct.IsCancellationRequested)
            {
                log.LogInformation("схемы не развёрнуты ({Status}), повтор через 3 с", e.StatusCode);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }
}
