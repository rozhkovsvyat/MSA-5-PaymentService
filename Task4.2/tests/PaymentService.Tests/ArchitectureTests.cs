using Assembly = System.Reflection.Assembly;
using ArchUnitNET.Domain;
using FraudCheckService.Checks;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using Npgsql;
using PaymentService.Payments;
using Zeebe.Client;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace PaymentService.Tests;

/// <summary>
/// Границы модулей: денежный код не знает о процессе, шаги процесса не ходят в базу мимо Payments,
/// FraudCheck Service и Notification Service не знают о процессе вовсе.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Architecture Service = new ArchLoader()
        .LoadAssemblies(typeof(PaymentOperations).Assembly, typeof(IZeebeClient).Assembly, typeof(NpgsqlDataSource).Assembly)
        .Build();

    private static readonly IObjectProvider<IType> Zeebe =
        Types().That().ResideInAssembly(typeof(IZeebeClient).Assembly).As("Zeebe.Client");

    private static readonly IObjectProvider<IType> ProcessModule =
        Types().That().ResideInNamespace("PaymentService.Process").As("модуль Process");

    [Fact]
    public void Payments_do_not_depend_on_the_process_engine() =>
        Types().That().ResideInNamespace("PaymentService.Payments")
            .Should().NotDependOnAny(Zeebe).AndShould().NotDependOnAny(ProcessModule)
            .Check(Service);

    [Fact]
    public void Process_steps_reach_the_database_only_through_payments() =>
        Types().That().ResideInNamespace("PaymentService.Process")
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(NpgsqlDataSource).Assembly).As("Npgsql"))
            .AndShould().NotDependOnAny(Types().That().ResideInNamespace("PaymentService.Data").As("модуль Data"))
            .Check(Service);

    [Fact]
    public void Process_steps_move_money_only_through_payment_operations() =>
        Types().That().ResideInNamespace("PaymentService.Process")
            .Should().NotDependOnAny(Types().That().Are(typeof(PaymentStore), typeof(RefundStore), typeof(IBank)).As("PaymentStore, RefundStore и IBank"))
            .Check(Service);

    [Fact]
    public void Clients_of_fraud_check_and_notifications_know_nothing_about_the_process() =>
        Types().That().ResideInNamespace("PaymentService.FraudCheck").Or().ResideInNamespace("PaymentService.Notifications")
            .Should().NotDependOnAny(Zeebe).AndShould().NotDependOnAny(ProcessModule)
            .Check(Service);

    private static readonly Assembly FraudCheckService = typeof(FraudChecks).Assembly;
    private static readonly Assembly NotificationService = Assembly.Load("NotificationService");

    private static readonly Architecture Participants = new ArchLoader()
        .LoadAssemblies(FraudCheckService, NotificationService, typeof(IZeebeClient).Assembly)
        .Build();

    [Fact]
    public void Fraud_check_and_notification_services_know_nothing_about_the_process() =>
        Types().That().ResideInAssembly(FraudCheckService).Or().ResideInAssembly(NotificationService)
            .Should().NotDependOnAny(Zeebe)
            .Check(Participants);
}
