using System.Xml.Linq;
using PaymentService.Process;
using Shouldly;
using Xunit;

namespace PaymentService.Tests;

/// <summary>Правила саги, которые должна соблюдать схема process.bpmn.</summary>
public sealed class ProcessModelTests
{
    private static readonly XNamespace Bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private static readonly XNamespace Zeebe = "http://camunda.org/schema/zeebe/1.0";
    private static readonly XDocument Model = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "process.bpmn"));
    private static readonly XElement[] Nodes = [.. Model.Descendants().Where(e => e.Name.Namespace == Bpmn && e.Attribute("id") is not null)];
    private static readonly XElement[] Flows = [.. Model.Descendants(Bpmn + "sequenceFlow")];

    [Fact]
    public void Every_job_type_of_both_processes_has_a_worker_and_back()
    {
        var refund = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "post-factum-refund.bpmn"));
        Nodes.Concat(refund.Descendants()).Select(JobType).OfType<string>().Distinct().Order()
            .ShouldBe(JobWorkers.JobTypes.Distinct().Order());
    }

    [Fact]
    public void Hold_funds_is_compensated_by_release_funds()
    {
        var hold = TaskOfType("HOLD_FUNDS");
        var handler = CompensationHandler(hold).ShouldNotBeNull("у «Заморозить деньги» нет обработчика компенсации");
        JobType(handler).ShouldBe("RELEASE_FUNDS");
    }

    [Fact]
    public void Every_compensation_boundary_has_a_handler_marked_for_compensation()
    {
        foreach (var boundary in Nodes.Where(n => n.Name == Bpmn + "boundaryEvent" && n.Element(Bpmn + "compensateEventDefinition") is not null))
        {
            var handler = CompensationHandler(Node(boundary.Attribute("attachedToRef")!.Value)).ShouldNotBeNull();
            handler.Attribute("isForCompensation")?.Value.ShouldBe("true");
        }
    }

    [Fact]
    public void Create_payment_is_undone_by_reject_payment_after_the_money_is_returned()
    {
        var returnMoney = Nodes.Single(n => n.Name == Bpmn + "intermediateThrowEvent" && n.Element(Bpmn + "compensateEventDefinition") is not null);
        Reachable(returnMoney).Select(JobType).ShouldContain("REJECT_PAYMENT");
    }

    [Fact]
    public void Timers_are_only_on_user_tasks()
    {
        // срок проверок считает обработчик от отправки запроса, а не таймер от создания задачи
        foreach (var timer in Timers())
            Node(timer.Attribute("attachedToRef")!.Value).Name.ShouldBe(Bpmn + "userTask");
    }

    [Fact]
    public void Timers_lead_only_to_the_allowed_path()
    {
        foreach (var timer in Timers())
        {
            var node = Next(timer).Single();
            while (node.Name == Bpmn + "intermediateThrowEvent")
            {
                node.Elements().Any(e => e.Name.LocalName.EndsWith("EventDefinition")).ShouldBeFalse("после таймера только событие без действий");
                node = Next(node).Single();
            }
            node.Name.ShouldBe(Bpmn + "exclusiveGateway", "таймер должен вливаться в путь «разрешено»");
            Flows.Where(f => f.Attribute("targetRef")!.Value == Id(node)).Select(f => f.Attribute("name")?.Value)
                .ShouldContain("разрешено");
        }
    }

    [Fact]
    public void Timer_and_cut_off_paths_never_reach_compensation_or_release()
    {
        var cutOffFlows = Flows.Where(f => f.Attribute("name")?.Value.Contains("cut-off") == true);
        var starts = Timers().Concat(cutOffFlows.Select(f => Node(f.Attribute("targetRef")!.Value))).ToList();
        starts.ShouldNotBeEmpty();
        foreach (var start in starts)
        {
            var reached = ReachableOnNormalCompletion(start);
            reached.ShouldNotContain(n => n.Name == Bpmn + "intermediateThrowEvent" && n.Element(Bpmn + "compensateEventDefinition") != null,
                $"от «{start.Attribute("name")?.Value}» есть путь к компенсации");
            reached.Select(JobType).ShouldNotContain("RELEASE_FUNDS");
        }
    }

    [Theory]
    [InlineData("process.bpmn", "payment-process.bpmn")]
    [InlineData("post-factum-refund.bpmn", "post-factum-refund.bpmn")]
    public void Task42_deploys_exactly_the_diagrams_from_Task41(string deployed, string drawn)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(repo.FullName, "Task4.1"))) repo = repo.Parent ?? throw new DirectoryNotFoundException("Task4.1");
        File.ReadAllBytes(Path.Combine(repo.FullName, "Task4.2", deployed))
            .ShouldBe(File.ReadAllBytes(Path.Combine(repo.FullName, "Task4.1", drawn)));
    }

    [Fact]
    public void Transfer_is_not_retried_blindly()
    {
        var transfer = TaskOfType("TRANSFER_TO_COUNTERPARTY");
        transfer.Descendants(Zeebe + "taskDefinition").Single().Attribute("retries")?.Value.ShouldBe("1");
        var lostAnswer = Nodes.Single(n => n.Attribute("attachedToRef")?.Value == Id(transfer)
            && ErrorCode(n) == "TRANSFER_STATUS_UNKNOWN");
        Next(lostAnswer).Select(JobType).ShouldBe(["CHECK_TRANSFER_STATUS"]);
    }

    private static IEnumerable<XElement> Timers() =>
        Nodes.Where(n => n.Name == Bpmn + "boundaryEvent" && n.Element(Bpmn + "timerEventDefinition") is not null);

    private static XElement TaskOfType(string type) => Nodes.Single(n => JobType(n) == type && n.Attribute("isForCompensation") is null);

    private static XElement? CompensationHandler(XElement activity)
    {
        var boundary = Nodes.SingleOrDefault(n => n.Attribute("attachedToRef")?.Value == Id(activity)
            && n.Element(Bpmn + "compensateEventDefinition") is not null);
        if (boundary is null) return null;
        var association = Model.Descendants(Bpmn + "association").SingleOrDefault(a => a.Attribute("sourceRef")!.Value == Id(boundary));
        return association is null ? null : Node(association.Attribute("targetRef")!.Value);
    }

    private static IEnumerable<XElement> Next(XElement node) =>
        Flows.Where(f => f.Attribute("sourceRef")!.Value == Id(node)).Select(f => Node(f.Attribute("targetRef")!.Value));

    private static HashSet<XElement> Reachable(XElement from)
    {
        var seen = new HashSet<XElement>();
        var queue = new Queue<XElement>([from]);
        while (queue.TryDequeue(out var node))
            foreach (var next in Next(node))
                if (seen.Add(next)) queue.Enqueue(next);
        return seen;
    }

    /// <summary>Узлы, куда можно дойти по потокам при обычном завершении шагов: без ошибок на границах.</summary>
    private static HashSet<XElement> ReachableOnNormalCompletion(XElement from)
    {
        var seen = new HashSet<XElement> { from };
        var queue = new Queue<XElement>([from]);
        while (queue.TryDequeue(out var node))
        {
            var next = Next(node).ToList();
            // конец подпроцесса без события ошибки — продолжение после подпроцесса
            if (node.Name == Bpmn + "endEvent" && !node.Elements().Any(e => e.Name.LocalName.EndsWith("EventDefinition"))
                && node.Parent?.Name == Bpmn + "subProcess")
                next.AddRange(Next(node.Parent));
            foreach (var n in next)
                if (seen.Add(n)) queue.Enqueue(n);
        }
        return seen;
    }

    private static XElement Node(string id) => Nodes.Single(n => Id(n) == id);

    private static string Id(XElement node) => node.Attribute("id")!.Value;

    private static string? JobType(XElement node) =>
        node.Element(Bpmn + "extensionElements")?.Element(Zeebe + "taskDefinition")?.Attribute("type")?.Value;

    private static string? ErrorCode(XElement node)
    {
        var errorRef = node.Element(Bpmn + "errorEventDefinition")?.Attribute("errorRef")?.Value;
        return errorRef is null ? null : Model.Descendants(Bpmn + "error").Single(e => Id(e) == errorRef).Attribute("errorCode")!.Value;
    }
}
