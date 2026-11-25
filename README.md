# MSA-5-PaymentService

Проектная работа четвёртого спринта курса «Микросервисная архитектура»: Saga-оркестрация платёжного процесса OrchestrPay на Camunda 8.

Процессом управляет Camunda, шаги выполняет Payment Service. Деньги клиента замораживаются на время проверок и уходят продавцу последним шагом.

| Задание | Что внутри |
| :- | :- |
| [Task1](Task1/README.md) | Шаги Saga: тип и компенсация каждого шага, почему заморозка, сроки проверок |
| [Task2](Task2/README.md) | Таблица переходов состояний платежа |
| [Task3](Task3/ADR-orchestrator-placement.md) | ADR: где разместить оркестрацию и какой тип саги выбран |
| [Task4.1](Task4.1/README.md) | BPMN-схемы: обработка платежа и возврат постфактум |
| [Task4.2](Task4.2/README.md) | Прототип на Camunda 8 и .NET: Payment Service, docker-compose, прогон сценариев, скриншоты Tasklist и Operate |
| [Task5](Task5/README.md) | Интеграционные и end-to-end тест-кейсы |
| [Task6](Task6/README.md) | Fitness-функции |

Вклад ИИ и мои решения — в [AI-LOG.md](AI-LOG.md).
