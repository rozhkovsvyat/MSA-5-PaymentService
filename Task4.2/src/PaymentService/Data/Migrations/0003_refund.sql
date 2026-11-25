-- Возврат постфактум: не больше одного на платёж. Деньги клиенту и удержание с продавца идут
-- через тот же журнал операций с ключами paymentId:STEP.
create table refund (
    payment_id       text primary key references payment (id),
    status           text           not null,  -- REFUND_REQUESTED, REFUNDING, REFUND_STATUS_UNKNOWN, REFUND_DETAILS_REQUESTED, REFUNDED
    -- номер попытки: растёт, когда клиент указал новые реквизиты; входит в ключ операции возврата
    attempt          integer        not null default 1,
    -- после этого момента банк возврат не исполняет: «не найден» значит «не выполнен»
    refund_deadline  timestamptz,
    recovery_status  text,                     -- RECOVERY_PENDING, RECOVERED, DEBT_RECORDED
    recovered_amount numeric(14, 2),
    debt_amount      numeric(14, 2),
    created_at       timestamptz    not null default now(),
    updated_at       timestamptz    not null default now()
);
