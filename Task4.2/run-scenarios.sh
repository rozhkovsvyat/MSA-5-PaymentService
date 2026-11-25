#!/usr/bin/env bash
# Прогон сценариев платежа на поднятом стенде (docker compose up -d). Нужны curl, jq и .NET SDK 10
# (сценарий 14 переводит экземпляр процесса в путь отказа маленькой утилитой tools/move-to-reject.cs).
# Каждый сценарий — один и тот же процесс payment-process; поведение банка и FraudCheck
# задаёт поле emulate в запросе (заглушки внешних сервисов). Каждый запрос несёт свой Idempotency-Key.
set -euo pipefail
cd "$(dirname "$0")"

API=${API:-http://localhost:3000}
OPERATE=${OPERATE:-http://localhost:8081}
TASKLIST=${TASKLIST:-http://localhost:8082}
REVIEW_PAUSE=${REVIEW_PAUSE:-15}     # сколько секунд задача ручной проверки висит открытой, чтобы её посмотреть в Tasklist
INCIDENT_PAUSE=${INCIDENT_PAUSE:-15} # сколько секунд инцидент висит в Operate до устранения
SUMMARY=results/scenarios.txt
COOKIES=$(mktemp -d)
trap 'rm -rf "$COOKIES"' EXIT
mkdir -p results

log() { printf '%s  %s\n' "$(date +%H:%M:%S)" "$*"; }

wait_for() { # описание, команда...
  local what=$1; shift
  for _ in $(seq 1 120); do "$@" >/dev/null 2>&1 && return 0; sleep 2; done
  log "не дождались: $what"; exit 1
}

zbctl() { docker compose --progress quiet run --rm -T deploy-process "zbctl $* --host zeebe --port 26500 --insecure"; }

new_key() {
  if command -v uuidgen >/dev/null; then uuidgen | tr 'A-Z' 'a-z'; else cat /proc/sys/kernel/random/uuid; fi
}

post_payment() { # тело запроса, Idempotency-Key -> "HTTP-код ответ"
  curl -s -o "$COOKIES/response" -w '%{http_code}' -X POST "$API/payments" \
    -H 'Content-Type: application/json' -H "Idempotency-Key: $2" -d "$1"
  echo " $(cat "$COOKIES/response")"
}

start_payment() { # тело запроса[, Idempotency-Key] -> "paymentId processInstanceKey"
  local reply key=${2:-$(new_key)}
  for _ in 1 2 3; do # 503 — процесс не запущен: клиент повторяет запрос с тем же ключом
    reply=$(post_payment "$1" "$key")
    [ "${reply%% *}" = 503 ] || break
    log "  POST /payments: ${reply} — повтор с тем же ключом" >&2
    sleep 2
  done
  [ "${reply%% *}" = 202 ] || { log "POST /payments: ${reply}" >&2; exit 1; }
  with_process "$(echo "${reply#* }" | jq -r .paymentId)"
}

with_process() { # paymentId -> "paymentId processInstanceKey"; экземпляр записывает шаг «Создать платёж»
  local key
  for _ in $(seq 1 120); do
    key=$(curl -sf "$API/payments/$1" | jq -r '.processInstanceKey // empty')
    [ -n "$key" ] && { echo "$1 $key"; return 0; }
    sleep 0.5
  done
  log "  платёж $1 не появился" >&2
  exit 1
}

payment() { curl -sf "$API/payments/$1"; }

wait_final() { # paymentId -> итоговый статус платежа
  local status
  for _ in $(seq 1 240); do
    status=$(payment "$1" | jq -r '.payment.status' 2>/dev/null || true)
    case "$status" in COMPLETED|REJECTED) echo "$status"; return 0 ;; esac
    sleep 0.5
  done
  echo "TIMEOUT"
}

review_task() { # processInstanceKey -> id открытой задачи ручной проверки
  local id
  for _ in $(seq 1 60); do
    id=$(curl -sf -b "$COOKIES/tasklist" -X POST "$TASKLIST/v1/tasks/search" -H 'Content-Type: application/json' \
      -d "{\"state\":\"CREATED\",\"processInstanceKey\":\"$1\"}" | jq -r '.[0].id // empty')
    [ -n "$id" ] && { echo "$id"; return 0; }
    sleep 1
  done
  return 1
}

operator_decides() { # processInstanceKey, ALLOW|DENY — оператор demo берёт задачу в Tasklist и выбирает решение
  local task
  task=$(review_task "$1")
  log "  задача ручной проверки открыта: $TASKLIST/$task (demo/demo), решение через ${REVIEW_PAUSE} с"
  sleep "$REVIEW_PAUSE"
  curl -sf -b "$COOKIES/tasklist" -X PATCH "$TASKLIST/v1/tasks/$task/assign" -H 'Content-Type: application/json' \
    -d '{"assignee":"demo","allowOverrideAssignment":true}' >/dev/null
  curl -sf -b "$COOKIES/tasklist" -X PATCH "$TASKLIST/v1/tasks/$task/complete" -H 'Content-Type: application/json' \
    -d "{\"variables\":[{\"name\":\"reviewDecision\",\"value\":\"\\\"$2\\\"\"}]}" >/dev/null
  log "  оператор demo: $2"
}

active_incident() { # processInstanceKey -> "incidentKey jobKey"
  local found
  for _ in $(seq 1 120); do
    found=$(curl -sf -b "$COOKIES/operate" -X POST "$OPERATE/v1/incidents/search" -H 'Content-Type: application/json' \
      -d "{\"filter\":{\"processInstanceKey\":$1,\"state\":\"ACTIVE\"}}" | jq -r '.items[0] // empty | "\(.key) \(.jobKey)"')
    [ -n "$found" ] && { echo "$found"; return 0; }
    sleep 1
  done
  return 1
}

operate() { # путь, тело запроса -> ответ Operate API
  curl -sf -b "$COOKIES/operate" -X POST "$OPERATE$1" -H 'Content-Type: application/json' -d "$2"
}

active_element() { # processInstanceKey, id элемента схемы -> ключ активного экземпляра элемента
  local found
  for _ in $(seq 1 60); do
    found=$(operate /v1/flownode-instances/search "{\"filter\":{\"processInstanceKey\":$1,\"flowNodeId\":\"$2\",\"state\":\"ACTIVE\"}}" \
      | jq -r '.items[0].key // empty')
    [ -n "$found" ] && { echo "$found"; return 0; }
    sleep 1
  done
  return 1
}

payment_instances() { # paymentId -> ключи экземпляров payment-process по этому платежу
  local main
  main=$(operate /v1/process-instances/search '{"filter":{"bpmnProcessId":"payment-process"},"size":1000}' | jq -r '[.items[].key]')
  operate /v1/variables/search "{\"filter\":{\"name\":\"paymentId\",\"value\":\"\\\"$1\\\"\"},\"size\":100}" \
    | jq -r --argjson main "$main" '[.items[].processInstanceKey] | unique | map(select(. as $k | $main | index($k))) | .[]'
}

count_instances() { # функция поиска экземпляров, paymentId -> сколько их; Operate видит экземпляры с задержкой
  local n=0
  for _ in $(seq 1 60); do
    n=$("$1" "$2" | wc -l | tr -d ' ')
    [ "$n" -ge 1 ] && break
    sleep 1
  done
  sleep 5 # если бы второй экземпляр был, за это время Operate увидел бы и его
  "$1" "$2" | wc -l | tr -d ' '
}

refund_instances() { # paymentId -> ключи экземпляров post-factum-refund по этому платежу
  local refunds
  refunds=$(operate /v1/process-instances/search '{"filter":{"bpmnProcessId":"post-factum-refund"},"size":100}' | jq -r '[.items[].key]')
  operate /v1/variables/search "{\"filter\":{\"name\":\"paymentId\",\"value\":\"\\\"$1\\\"\"},\"size\":100}" \
    | jq -r --argjson refunds "$refunds" '[.items[].processInstanceKey] | unique | map(select(. as $k | $refunds | index($k))) | .[]'
}

wait_payment() { # paymentId, jq-условие над ответом GET /payments/{id}
  for _ in $(seq 1 240); do
    payment "$1" | jq -e "$2" >/dev/null 2>&1 && return 0
    sleep 0.5
  done
  log "  не дождались: $2"
  return 1
}

fraud_detected() { # paymentId[, emulate[, срок исполнения возврата, запас на расхождение часов]] -> HTTP-код
  local request
  request=$(jq -nc --argjson e "${2:-null}" --arg w "${3:-}" --arg m "${4:-}" \
    '{emulate: $e} + (if $w == "" then {} else {transferExecutionWindow: $w, clockSkewMargin: $m} end)')
  curl -s -o /dev/null -w '%{http_code}' -X POST "$API/payments/$1/fraud-detected" \
    -H 'Content-Type: application/json' -d "$request"
}

completed_payment() { # clientId, сумма -> paymentId проведённого платежа
  local ids
  ids=$(start_payment "$(body "$1" "$2" '{}')")
  wait_payment "${ids% *}" '.payment.status == "COMPLETED"' || exit 1
  echo "${ids% *}"
}

record_refund() { # №, сценарий, paymentId
  local refund key
  wait_payment "$3" '.refund.status == "REFUNDED" and .refund.recoveryStatus != "RECOVERY_PENDING" and .refund.recoveryStatus != null' || true
  refund=$(payment "$3" | jq -r '"\(.refund.status) / \(.refund.recoveryStatus) (удержано \(.refund.recoveredAmount), долг \(.refund.debtAmount))"')
  key=$(wait_refund_instance "$3")
  printf '%s\t%s\t%s\t%s\t%s\t-\t-\t-\n' "$1" "$2" "$3" "$key" "$refund" >> "$SUMMARY"
  log "  итог: $refund, процесс возврата $key"
}

wait_refund_instance() { # paymentId -> ключ экземпляра возврата (Operate видит его с задержкой)
  local key
  for _ in $(seq 1 60); do
    key=$(refund_instances "$1" | head -1)
    [ -n "$key" ] && { echo "$key"; return 0; }
    sleep 1
  done
  echo "-"
}

record() { # №, сценарий, paymentId, processInstanceKey
  local status=$(wait_final "$3") details
  details=$(payment "$3" | jq -r '[.payment.rejectReason // "-", (if .payment.cutOff then "да" else "нет" end), (.durationMs // "-" | tostring)] | @tsv')
  printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" "$status" "$details" >> "$SUMMARY"
  log "  итог: $status ($(echo "$details" | tr '\t' ' '))"
}

run() { # №, сценарий, тело запроса
  local ids
  ids=$(start_payment "$3")
  log "$1. $2: платёж ${ids% *}, процесс ${ids#* }"
  record "$1" "$2" ${ids}
}

body() { # clientId, сумма, emulate[, manualReviewTimeout]
  jq -nc --arg c "$1" --argjson a "$2" --argjson e "$3" --arg t "${4:-}" \
    '{amount: $a, currency: "RUB", clientId: $c, counterpartyId: ("seller-" + ($c | ltrimstr("client-"))), emulate: $e}
     + (if $t == "" then {} else {manualReviewTimeout: $t} end)'
}

log "ждём стенд"
wait_for "payment-service" sh -c "curl -s -o /dev/null -w '%{http_code}' '$API/payments/-' | grep -q 404"
wait_for "Zeebe" curl -sf http://localhost:9600/actuator/health/readiness
wait_for "Tasklist" curl -sf -c "$COOKIES/tasklist" -X POST "$TASKLIST/api/login" -d 'username=demo&password=demo'
wait_for "Operate" curl -sf -c "$COOKIES/operate" -X POST "$OPERATE/api/login?username=demo&password=demo"
# deploy-process при первом старте может опередить готовность Zeebe; та же схема повторно новую версию не создаёт.
# Схему возврата постфактум deploy-process из задания не разворачивает — разворачиваем её здесь.
docker compose --progress quiet run --rm -T -v "$PWD/post-factum-refund.bpmn:/post-factum-refund.bpmn:ro" deploy-process "
  echo '$ zbctl deploy process.bpmn'; zbctl deploy /process.bpmn --host zeebe --port 26500 --insecure;
  echo '$ zbctl deploy post-factum-refund.bpmn'; zbctl deploy /post-factum-refund.bpmn --host zeebe --port 26500 --insecure" > results/deploy.txt
log "схемы payment-process и post-factum-refund развёрнуты (results/deploy.txt)"

printf '№\tсценарий\tpaymentId\tprocessInstanceKey\tстатус\tпричина отказа\tcut-off\tмс до итога\n' > "$SUMMARY"

run 1 "успех: проверки разрешили" "$(body client-01 1500 '{}')"
run 2 "запрет антифрода" "$(body client-02 2500 '{"fraud":"DENY"}')"

ids=$(start_payment "$(body client-03 32000 '{"fraud":"MANUAL"}')")
log "3. ручная проверка, оператор разрешил: платёж ${ids% *}, процесс ${ids#* }"
operator_decides "${ids#* }" ALLOW
record 3 "ручная проверка: разрешено" ${ids}

ids=$(start_payment "$(body client-04 45000 '{"fraud":"MANUAL"}')")
log "4. ручная проверка, оператор запретил: платёж ${ids% *}, процесс ${ids#* }"
REVIEW_PAUSE=0 operator_decides "${ids#* }" DENY
record 4 "ручная проверка: запрет" ${ids}

run 5 "антифрод молчит: cut-off 3 с" "$(body client-05 1200 '{"fraud":"SILENT"}')"
run 5a "оператор молчит: cut-off ручной проверки" "$(body client-55 51000 '{"fraud":"MANUAL"}' PT10S)"
run 6 "ответ о переводе потерян" "$(body client-06 3300 '{"transfer":"LOST"}')"

ids=$(start_payment "$(body client-07 2700 '{"fraud":"DENY","releaseFailures":10}')")
log "7. разморозка не проходит: платёж ${ids% *}, процесс ${ids#* }"
read -r incident job < <(active_incident "${ids#* }")
log "  инцидент $incident в Operate: $OPERATE/processes/${ids#* } (задача $job), устранение через ${INCIDENT_PAUSE} с"
sleep "$INCIDENT_PAUSE"
zbctl update retries "$job" --retries 1 >/dev/null
zbctl resolve incident "$incident" >/dev/null
log "  SRE: банк снова отвечает, попытка возвращена, инцидент снят"
record 7 "разморозка не проходит → инцидент → повтор" ${ids}

run 8a "AML: первый платёж пары, результат в кеше" "$(body client-08 900 '{}')"
run 8b "AML-провайдер молчит, лимиты и GeoRisk ответили" "$(body client-08 950 '{"aml":"SILENT"}')"
run 8c "лимиты и GeoRisk молчат, AML есть в кеше" "$(body client-08 990 '{"aml":"SILENT","limits":"SILENT"}')"
run 8d "AML запретил, лимиты и GeoRisk молчат" "$(body client-08d 970 '{"aml":"DENY","limits":"SILENT"}')"

docker compose stop redis >/dev/null 2>&1
run 9 "Redis остановлен" "$(body client-09 700 '{}')"
docker compose start redis >/dev/null 2>&1
wait_for "Redis" docker compose exec -T redis redis-cli ping
sleep 3 # сервис переподключается к Redis сам, без перезапуска

key=$(new_key)
for i in 1 2 3 4 5; do post_payment "$(body client-10 1800 '{}')" "$key" > "$COOKIES/concurrent-$i" & done
wait
answers=$(for i in 1 2 3 4 5; do cut -d' ' -f1 "$COOKIES/concurrent-$i"; done | sort | uniq -c | tr -s ' ' | tr '\n' ';')
payments=$(for i in 1 2 3 4 5; do cut -d' ' -f2- "$COOKIES/concurrent-$i" | jq -r .paymentId; done | sort -u)
[ "$(echo "$payments" | wc -l | tr -d ' ')" = 1 ] || { log "  одновременные запросы получили разные платежи: $payments"; exit 1; }
ids=$(with_process "$payments")
count=$(count_instances payment_instances "${ids% *}")
log "10. пять одновременных запросов с одним ключом: ответы HTTP [$answers], платёж ${ids% *}, экземпляров процесса: $count"
[ "$count" = 1 ] || { log "  ожидался один экземпляр"; exit 1; }
record 10 "пять одновременных запросов с одним ключом" ${ids}

reply=$(post_payment "$(body client-10 9999 '{}')" "$key")
log "11. тот же ключ, другая сумма: HTTP ${reply%% *} ${reply#* }"
printf '11\tтот же ключ, другая сумма\t-\t-\tHTTP %s\t-\t-\t-\n' "${reply%% *}" >> "$SUMMARY"

# --- отказы Camunda и банка

docker compose stop zeebe >/dev/null 2>&1
key=$(new_key)
reply=$(post_payment "$(body client-12 1300 '{}')" "$key")
log "12. Camunda недоступна при приёме платежа: HTTP ${reply%% *} ${reply#* }"
[ "${reply%% *}" = 503 ] || { log "  ожидался ответ 503"; exit 1; }
pending=$(echo "${reply#* }" | jq -r .paymentId)
docker compose start zeebe >/dev/null 2>&1
wait_for "Zeebe" curl -sf http://localhost:9600/actuator/health/readiness
for attempt in $(seq 1 20); do
  reply=$(post_payment "$(body client-12 1300 '{}')" "$key")
  [ "${reply%% *}" = 202 ] && break
  sleep 3
done
ids=$(with_process "$(echo "${reply#* }" | jq -r .paymentId)")
log "  Zeebe снова доступен, повтор с тем же ключом (попытка $attempt): платёж ${ids% *}, процесс ${ids#* }"
[ "${ids% *}" = "$pending" ] || { log "  повтор получил другой платёж"; exit 1; }
record 12 "Camunda недоступна: 503, повтор с тем же ключом" ${ids}

key=$(new_key)
reply=$(post_payment "$(body client-12a 1350 '{"start":"LOST"}')" "$key")
log "12a. Zeebe принял сообщение о старте, ответ потерян: HTTP ${reply%% *} ${reply#* }"
[ "${reply%% *}" = 503 ] || { log "  ожидался ответ 503"; exit 1; }
lost=$(echo "${reply#* }" | jq -r .paymentId)
reply=$(post_payment "$(body client-12a 1350 '{}')" "$key")
[ "${reply%% *}" = 202 ] || { log "  повтор: HTTP ${reply}"; exit 1; }
ids=$(with_process "$(echo "${reply#* }" | jq -r .paymentId)")
[ "${ids% *}" = "$lost" ] || { log "  повтор получил другой платёж"; exit 1; }
count=$(count_instances payment_instances "$lost")
log "  повтор с тем же ключом: HTTP 202, тот же платёж, экземпляров процесса: $count"
[ "$count" = 1 ] || { log "  ожидался один экземпляр"; exit 1; }
record 12a "ответ Zeebe о старте потерян, повтор с тем же ключом" ${ids}

ids=$(start_payment "$(body client-13 1400 '{"hold":"LATE"}')")
log "13. ответ о заморозке потерян: платёж ${ids% *}, процесс ${ids#* }"
record 13 "ответ о заморозке потерян" ${ids}
sleep 2
docker compose logs --no-color --no-log-prefix payment-service | grep "${ids% *} банк: заморозка" | sed 's/^/  лог: /' || true

ids=$(start_payment "$(jq -c '. + {transferExecutionWindow: "PT25S", clockSkewMargin: "PT5S"}' <<<"$(body client-14 2100 '{"transfer":"NOT_RECEIVED"}')")")
log "14. защита разморозки (окно 25 с + запас 5 с): платёж ${ids% *}, процесс ${ids#* }"
wait_payment "${ids% *}" '.payment.status == "TRANSFER_STATUS_UNKNOWN"' || exit 1
element=$(active_element "${ids#* }" Task_CheckTransferStatus)
log "  перевод ушёл в банк, итог неизвестен; переводим экземпляр в путь отказа (задача $element)"
dotnet run tools/move-to-reject.cs -- "${ids#* }" "$element" | sed 's/^/  /'
record 14 "защита разморозки: перевод мог пройти" ${ids}
docker compose logs --no-color --no-log-prefix payment-service | grep "${ids% *} RELEASE_FUNDS" | sed 's/^/  лог: /' || true

ids=$(start_payment "$(body client-15 99000 '{"hold":"DECLINE"}')")
log "15. не хватает денег: платёж ${ids% *}, процесс ${ids#* }"
record 15 "не хватает денег" ${ids}

run 16 "банк отклонил перевод" "$(body client-16 2600 '{"transfer":"DECLINE"}')"

ids=$(start_payment "$(jq -c '. + {transferExecutionWindow: "PT2S", clockSkewMargin: "PT2S"}' <<<"$(body client-17 2700 '{"transfer":"NOT_RECEIVED"}')")")
log "17. перевод не дошёл до банка (окно 2 с + запас 2 с): платёж ${ids% *}, процесс ${ids#* }"
record 17 "перевод не найден после окна и запаса" ${ids}

ids=$(start_payment "$(body client-18 41000 '{"fraud":"MANUAL","limits":"DENY","externalDelay":"PT2S"}')")
log "18. запрет внешних проверок при открытой ручной проверке: платёж ${ids% *}, процесс ${ids#* }"
record 18 "запрет внешних проверок снимает ручную проверку" ${ids}
for _ in $(seq 1 30); do
  task=$(curl -sf -b "$COOKIES/tasklist" -X POST "$TASKLIST/v1/tasks/search" -H 'Content-Type: application/json' \
    -d "{\"state\":\"CANCELED\",\"processInstanceKey\":\"${ids#* }\"}" | jq -r '.[0] | select(.) | "\(.name): \(.taskState)"')
  [ -n "$task" ] && break
  sleep 1
done
log "  Tasklist: ${task:-задача не найдена}"

run 19 "поздний запрет антифрода после cut-off" "$(body client-19 1900 '{"fraud":"SILENT_DENY"}')"
run 20 "уведомление не прошло дважды" "$(body client-20 800 '{"notifyFailures":2}')"

docker compose stop fraud-check-service >/dev/null 2>&1
run 21 "FraudCheck Service остановлен: проверки по cut-off" "$(body client-21 1100 '{}')"
docker compose start fraud-check-service >/dev/null 2>&1
wait_for "FraudCheck Service" curl -sf http://localhost:3001/fraud-checks/ready/decisions

# --- возврат постфактум

id=$(completed_payment client-r1 1500)
log "R1. возврат: платёж $id проведён, сигнал о мошенничестве → HTTP $(fraud_detected "$id")"
record_refund R1 "возврат, сумма удержана с продавца" "$id"

id=$(completed_payment client-r2 1600)
log "R2. ответ о возврате потерян: платёж $id → HTTP $(fraud_detected "$id" '{"refund":"LOST"}')"
record_refund R2 "ответ о возврате потерян" "$id"
log "  возвратов в журнале: $(payment "$id" | jq '[.operations[] | select(.step == "REFUND_TO_CLIENT")] | length')"

id=$(completed_payment client-r3 1700)
log "R3. банк отклонил возврат: платёж $id → HTTP $(fraud_detected "$id" '{"refund":"DECLINE"}')"
wait_payment "$id" '.refund.status == "REFUND_DETAILS_REQUESTED"' || exit 1
first=$(wait_refund_instance "$id")
log "  клиента попросили указать новые реквизиты, процесс возврата $first ждёт"
log "R5. повторный сигнал о мошенничестве, пока возврат ждёт: HTTP $(fraud_detected "$id")"
sleep 5
count=$(refund_instances "$id" | wc -l | tr -d ' ')
log "  экземпляров возврата по платежу: $count"
printf 'R5\tповторный сигнал, пока возврат идёт\t%s\t%s\tэкземпляров возврата: %s\t-\t-\t-\n' "$id" "$first" "$count" >> "$SUMMARY"
log "  клиент указал реквизиты: $(curl -s -X POST "$API/payments/$id/refund-details")"
record_refund R3 "банк отклонил возврат, новые реквизиты" "$id"

id=$(completed_payment client-r6 1550)
for i in 1 2 3; do fraud_detected "$id" > "$COOKIES/signal-$i" & done
wait
log "R6. три одновременных сигнала о мошенничестве по платежу $id: HTTP $(cat "$COOKIES"/signal-* | tr '\n' ' ')"
wait_payment "$id" '.refund.status == "REFUNDED"' || exit 1
count=$(count_instances refund_instances "$id")
refunds=$(payment "$id" | jq '[.operations[] | select(.step == "REFUND_TO_CLIENT")] | length')
log "  экземпляров возврата: $count, возвратов в журнале: $refunds"
[ "$count" = 1 ] && [ "$refunds" = 1 ] || { log "  ожидался один возврат"; exit 1; }
printf 'R6\tтри одновременных сигнала о мошенничестве\t%s\t%s\tэкземпляров возврата: %s, возвратов: %s\t-\t-\t-\n' "$id" "$(refund_instances "$id")" "$count" "$refunds" >> "$SUMMARY"

id=$(completed_payment client-r4 1500)
log "R4. на балансе продавца не хватает: платёж $id → HTTP $(fraud_detected "$id" '{"sellerBalance":500}')"
record_refund R4 "на балансе продавца не хватило" "$id"

id=$(completed_payment client-r7 1650)
log "R7. банк не исполняет возврат (окно 2 с + запас 2 с): платёж $id → HTTP $(fraud_detected "$id" '{"refundNotReceived":3}' PT2S PT2S)"
refund=$(wait_refund_instance "$id")
read -r incident job < <(active_incident "$refund")
log "  три отправки возврата банк не исполнил: инцидент $incident в Operate: $OPERATE/processes/$refund (задача $job), устранение через ${INCIDENT_PAUSE} с"
sleep "$INCIDENT_PAUSE"
zbctl update retries "$job" --retries 1 >/dev/null
zbctl resolve incident "$incident" >/dev/null
log "  SRE: банк снова принимает возвраты, попытка возвращена, инцидент снят"
record_refund R7 "банк трижды не исполнил возврат → инцидент → повтор" "$id"

log "готово: $SUMMARY"
column -t -s $'\t' "$SUMMARY"
