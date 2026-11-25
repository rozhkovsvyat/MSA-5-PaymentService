-- Сколько отправок возврата подряд банк не исполнил. После предела шаг открывает инцидент и счёт начинается заново.
alter table refund add column unexecuted_sends integer not null default 0;
