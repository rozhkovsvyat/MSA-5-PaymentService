create table payment (
    id                   text primary key,
    client_id            text           not null,
    counterparty_id      text           not null,
    amount               numeric(14, 2) not null,
    currency             text           not null,
    status               text           not null,
    -- проведён без ответа одной из проверок (cut-off), безопасность получает уведомление
    cut_off              boolean        not null default false,
    reject_reason        text,
    -- после этого момента банк перевод не исполняет: «не найден» значит «не выполнен»
    transfer_deadline    timestamptz,
    accepted_at          timestamptz    not null,
    finished_at          timestamptz,
    updated_at           timestamptz    not null default now()
);

-- Журнал денежных операций. Повтор шага находит запись по ключу и не создаёт второго эффекта.
create table payment_operation (
    id               bigint generated always as identity primary key,
    idempotency_key  text        not null unique,  -- paymentId:STEP
    payment_id       text        not null references payment (id),
    step             text        not null,
    result           text        not null,
    created_at       timestamptz not null default now()
);
create index payment_operation_payment_idx on payment_operation (payment_id);
