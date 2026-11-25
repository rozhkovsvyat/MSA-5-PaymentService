-- Запросы маркетплейса по ключу идемпотентности: повторный запрос получает тот же платёж.
-- process_instance_key записывает шаг «Создать платёж»; до него процесс мог уже стартовать.
create table payment_request (
    request_key          text primary key,
    -- отпечаток суммы, валюты и участников: тот же ключ с другим содержимым — ошибка маркетплейса
    request_hash         text        not null,
    payment_id           text        not null,
    process_instance_key bigint,
    created_at           timestamptz not null default now()
);
