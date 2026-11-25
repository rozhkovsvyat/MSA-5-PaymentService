# Задание 2. Таблица переходов

Состояния платежа — это статусы платежа в Payment Service. Те же коды используются в [задании 1](../Task1/README.md) и в fitness-функциях [задания 6](../Task6/README.md).

- `CREATED` — платёж создан;
- `FUNDS_HELD` — деньги заморожены на счёте клиента;
- `CHECKING` — идут антифрод и внешние проверки;
- `IN_MANUAL_REVIEW` — платёж ждёт решения оператора;
- `APPROVED` — проверки разрешили платёж;
- `CUT_OFF_APPROVED` — платёж разрешён, но хотя бы одна проверка не ответила к сроку (cut-off);
- `TRANSFERRING` — деньги списываются и переводятся продавцу;
- `TRANSFER_STATUS_UNKNOWN` — ответ о переводе потерян, статус уточняется;
- `COMPLETED` — платёж проведён;
- `RELEASING_FUNDS` — платёж отклонён, деньги размораживаются;
- `REJECTED` — платёж отклонён, деньги у клиента.

| Исходное состояние | Переходное состояние | Событие |
| :- | :- | :- |
| — | `CREATED` | `PaymentInitiated` — маркетплейс начал платёж |
| `CREATED` | `FUNDS_HELD` | `FundsHeld` — сумма заморожена |
| `CREATED` | `REJECTED` | `FundsHoldDeclined` — не хватает денег или банк отказал в заморозке |
| `CREATED` | `REJECTED` | `FundsHoldLost` — ответ банка потерян, заморозка снята тем же ключом |
| `FUNDS_HELD` | `CHECKING` | `ChecksStarted` — запущены антифрод и внешние проверки |
| `CHECKING` | `APPROVED` | `ChecksAllowed` — обе проверки разрешили платёж |
| `CHECKING` | `CUT_OFF_APPROVED` | `CheckCutOff` — одна или обе проверки не ответили за 3 секунды, явного запрета нет |
| `CHECKING` | `IN_MANUAL_REVIEW` | `ManualReviewRequired` — антифрод отправил платёж на ручную проверку |
| `CHECKING` | `RELEASING_FUNDS` | `FraudDenied` — антифрод запретил платёж |
| `CHECKING` | `RELEASING_FUNDS` | `ExternalCheckDenied` — внешняя проверка запретила платёж |
| `IN_MANUAL_REVIEW` | `APPROVED` | `ManualReviewAllowed` — оператор разрешил платёж, внешние проверки тоже разрешили |
| `IN_MANUAL_REVIEW` | `CUT_OFF_APPROVED` | `ManualReviewAllowed` — оператор разрешил платёж, а внешние проверки не ответили за 3 секунды |
| `IN_MANUAL_REVIEW` | `CUT_OFF_APPROVED` | `ManualReviewCutOff` — оператор не принял решение за 20 минут |
| `IN_MANUAL_REVIEW` | `RELEASING_FUNDS` | `ManualReviewDenied` — оператор запретил платёж |
| `IN_MANUAL_REVIEW` | `RELEASING_FUNDS` | `ExternalCheckDenied` — внешняя проверка запретила платёж, задача оператора снимается |
| `APPROVED` | `TRANSFERRING` | `TransferRequested` — отправлен запрос на списание и перевод |
| `CUT_OFF_APPROVED` | `TRANSFERRING` | `TransferRequested` — повторный запрос решений не нашёл запрета или не получил ответа за 0,5 секунды, отправлен запрос на перевод |
| `CUT_OFF_APPROVED` | `RELEASING_FUNDS` | `LateDenyFound` — повторный запрос решений нашёл запрет |
| `TRANSFERRING` | `COMPLETED` | `TransferSucceeded` — деньги списаны и переведены продавцу |
| `TRANSFERRING` | `RELEASING_FUNDS` | `TransferDeclined` — банк отклонил перевод, деньги остались замороженными |
| `TRANSFERRING` | `TRANSFER_STATUS_UNKNOWN` | `TransferResponseLost` — ответ о переводе не пришёл |
| `TRANSFER_STATUS_UNKNOWN` | `COMPLETED` | `TransferConfirmed` — банк подтвердил перевод |
| `TRANSFER_STATUS_UNKNOWN` | `RELEASING_FUNDS` | `TransferDeclined` — банк сообщил, что отклонил перевод |
| `TRANSFER_STATUS_UNKNOWN` | `RELEASING_FUNDS` | `TransferExpired` — банк не нашёл операцию, а срок её исполнения истёк |
| `RELEASING_FUNDS` | `REJECTED` | `FundsReleased` — деньги разморожены |

- **Отказ антифрода.** `CHECKING` → `RELEASING_FUNDS` → `REJECTED`. Внешняя проверка прерывается, уведомления уходят клиенту и в безопасность.
- **Cut-off.** Проверка, не ответившая к сроку, считается разрешением, и платёж переходит в `CUT_OFF_APPROVED`, а не в `APPROVED`: ни одна проверка его не одобрила. Перед переводом решения проверок запрашиваются ещё раз, а после перевода безопасность получает уведомление. Таймер никогда не ведёт к отказу и сам по себе не двигает деньги.
- **Подтверждение.** `CHECKING` → `APPROVED` → `TRANSFERRING` → `COMPLETED`.
- **Ручная проверка.** Платёж ждёт оператора в `IN_MANUAL_REVIEW` до 20 минут. Если в это время внешняя проверка запретит платёж, ожидание прерывается.
- В `TRANSFER_STATUS_UNKNOWN` статус запрашивается повторно, пока банк не даст ответ или не истечёт срок исполнения перевода. Разморозка и повторный перевод в это время запрещены.
- В `RELEASING_FUNDS` разморозка повторяется с растущей паузой, затем открывается инцидент для дежурного SRE. Если перевод уходил в банк, шаг сначала уточняет его итог и размораживает деньги только после «отклонён» или «не выполнен». Если перевод выполнен, открывается инцидент: такое расхождение разбирают вручную.
- Конечные состояния `COMPLETED` и `REJECTED` не меняются: запоздавшее событие не переводит платёж обратно.
- Между проверками и переводом в схеме нет отдельного шага, поэтому в прототипе `APPROVED` записывает шаг перевода перед отправкой в банк. Пока этот шаг не начался, платёж, прошедший проверки, виден как `CHECKING` или `IN_MANUAL_REVIEW`.
- Отмена платежа клиентом во время проверок в этот процесс не входит.

## Возврат постфактум

Отдельный процесс со своими состояниями. Удержание суммы с продавца идёт параллельно с уведомлением клиента и на состояние возврата клиенту не влияет, у него своя таблица.

| Исходное состояние | Переходное состояние | Событие |
| :- | :- | :- |
| — | `REFUND_REQUESTED` | `FraudDetectedAfterTransfer` — поздний запрет антифрода или решение безопасности |
| `REFUND_REQUESTED` | `REFUNDING` | `RefundSent` — запрос на возврат отправлен в банк |
| `REFUNDING` | `REFUNDED` | `RefundSucceeded` — деньги возвращены клиенту |
| `REFUNDING` | `REFUND_STATUS_UNKNOWN` | `RefundResponseLost` — ответ о возврате не пришёл |
| `REFUND_STATUS_UNKNOWN` | `REFUNDED` | `RefundConfirmed` — банк подтвердил возврат |
| `REFUND_STATUS_UNKNOWN` | `REFUND_REQUESTED` | `RefundNotExecuted` — банк точно не выполнил возврат, запрос повторяется с тем же ключом; после трёх раз подряд открывается инцидент |
| `REFUNDING` | `REFUND_DETAILS_REQUESTED` | `RefundDeclined` — банк отклонил возврат, например карта закрыта; клиента просят указать новые реквизиты |
| `REFUND_DETAILS_REQUESTED` | `REFUND_REQUESTED` | `RefundDetailsProvided` — клиент указал новые реквизиты |

Удержание с продавца:

| Исходное состояние | Переходное состояние | Событие |
| :- | :- | :- |
| — | `RECOVERY_PENDING` | `RefundSucceeded` — деньги клиенту возвращены, сумму нужно удержать с продавца |
| `RECOVERY_PENDING` | `RECOVERED` | `CounterpartyDebited` — сумма списана с баланса продавца |
| `RECOVERY_PENDING` | `DEBT_RECORDED` | `CounterpartyBalanceShort` — на балансе не хватило денег, остаток записан в долг |
| `DEBT_RECORDED` | `RECOVERED` | `DebtSettled` — долг погашен из следующих выплат продавцу |

Погашение долга из следующих выплат — целевое поведение: в прототип оно не входит, прототип доводит удержание до `DEBT_RECORDED`.
