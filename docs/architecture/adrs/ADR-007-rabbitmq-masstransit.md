# ADR-007 — Mensageria com RabbitMQ e MassTransit 8.x

- **Status:** Proposto (rascunho da Etapa 0; validação nos spikes S1 e S4)
- **Data:** 2026-11-15

## Contexto

A saga ([ADR-006](ADR-006-saga-orquestrada.md)) precisa de mensageria assíncrona confiável entre três serviços .NET:

- **Garantia de entrega:** uma mensagem não pode se perder entre a gravação no banco e a publicação.
- **Robustez:** retry, fila de erro e tratamento de duplicadas.
- **Ambientes:** o mesmo comportamento no docker-compose, nos testes (Testcontainers) e no EKS.

## Decisão

- **Broker: RabbitMQ.**
  - No EKS, via **RabbitMQ Cluster Operator** oficial, com volume EBS (gp3).
  - Localmente, a imagem `rabbitmq:management`.
- **Biblioteca: MassTransit**, com versão **fixada em 8.x** (licença Apache 2.0). A partir da v9, a licença é comercial.
- **Recursos do MassTransit usados:**
  - State machine de saga com repositório EF Core.
  - **Outbox e inbox transacionais** no EF Core (`os-service` e `billing-service`).
  - Retry exponencial + *delayed redelivery*, e fila `_error` quando as tentativas se esgotam.
  - `ITestHarness` para testes de consumers, da saga e BDD.
  - `ActivitySource` para traces distribuídos (OpenTelemetry).
- **Contratos:** sem biblioteca compartilhada. Cada repo declara as classes com `[MessageUrn]` e `[EntityName]` iguais ([contratos de mensagens](../../contratos-mensagens.md)).
- **Topologia:**
  - Vhost `oficina`, com um usuário por serviço.
  - Filas de comando com nome fixo e exchanges de evento nomeados pelo `EntityName`.

## Consequências

- **Positivas:**
  - Paridade entre local, CI e nuvem.
  - O outbox elimina a perda de mensagens entre o commit e a publicação.
  - A UI de management mostra filas e mensagens na demonstração.
  - Pouca configuração manual de topologia.
- **Negativas / mitigação:**
  - **Mais um componente com estado para operar no cluster:** operator oficial, PVC e métricas Prometheus no New Relic.
  - **Custo:** 1 réplica em `homolog`. Para `prod`, 3 réplicas com *quorum queues*, documentadas.
  - **Fixação na 8.x:** sem as novidades da v9. Plano de saída: Wolverine (MIT) ou o transporte SQS do próprio MassTransit.
  - **O DynamoDB não é suportado pelo outbox do MassTransit:** o `execution-service` implementa um outbox próprio, com deduplicação por `MessageId`.

## Alternativas descartadas

- **Amazon SQS/SNS:** gerenciado e barato. Mas não roda localmente sem LocalStack e não tem UI para a demonstração. Continua como plano B, porque o MassTransit também tem transporte SQS.
- **Kafka (MSK):** caro e complexo demais para o volume do projeto. Log de eventos não é necessário aqui.
- **NServiceBus:** licença comercial.
- **RabbitMQ sem framework** (`RabbitMQ.Client`): seria preciso implementar outbox, retry, saga e test harness à mão.
