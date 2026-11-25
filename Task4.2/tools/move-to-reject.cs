#:package zb-client@2.10.0
#:property PublishAot=false
// Модификация экземпляра payment-process: снять активную задачу и поставить токен на «Вернуть деньги клиенту»
// с причиной отказа TRANSFER_DECLINED. Так на стенде проверяется защита разморозки, когда перевод уже ушёл в банк,
// а его итог неизвестен.
//   dotnet run tools/move-to-reject.cs -- <processInstanceKey> <elementInstanceKey активной задачи>
using Zeebe.Client;

if (args.Length != 2 || !long.TryParse(args[0], out var processInstance) || !long.TryParse(args[1], out var activeElement))
{
    Console.Error.WriteLine("ожидается: <processInstanceKey> <elementInstanceKey>");
    return 1;
}
var zeebe = ZeebeClient.Builder()
    .UseGatewayAddress(Environment.GetEnvironmentVariable("ZEEBE_ADDRESS") ?? "localhost:26500")
    .UsePlainText()
    .Build();
await zeebe.NewSetVariablesCommand(processInstance).Variables("""{"rejectReason":"TRANSFER_DECLINED"}""").Send();
await zeebe.NewModifyProcessInstanceCommand(processInstance)
    .ActivateElement("Event_ReturnMoney")
    .And()
    .TerminateElement(activeElement)
    .Send();
Console.WriteLine($"экземпляр {processInstance}: задача {activeElement} снята, токен на «Вернуть деньги клиенту»");
return 0;
