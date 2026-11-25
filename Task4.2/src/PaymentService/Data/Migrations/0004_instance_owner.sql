-- Поиск запроса по платежу на каждом шаге «Создать платёж» и в GET /payments/{id}.
create unique index payment_request_payment_idx on payment_request (payment_id);

-- Экземпляр процесса возврата, за которым закреплён возврат: второй экземпляр по тому же платежу — дубликат.
alter table refund add column process_instance_key bigint;
