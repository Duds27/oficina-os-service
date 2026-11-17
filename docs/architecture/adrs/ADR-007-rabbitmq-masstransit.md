# ADR-007 — Mensageria com RabbitMQ e MassTransit 8.x

- **Status:** Proposto: implementado e validado na Fase 4 (resultado no fim); aguarda a revisão do grupo para passar a *Aceito*. Spike [S1](../../../spikes/S1-masstransit-saga/README.md) concluído; o S4 (RabbitMQ no EKS) foi validado num kind e falta só o EBS, que depende do EKS (Etapa 6, 6.0).
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
- **Biblioteca: MassTransit**, com versão **fixada em 8.4.1** (licença Apache 2.0) nos três serviços.
  - A partir da v9, a licença é comercial.
  - A partir da 8.5.0, o `MassTransit.EntityFrameworkCore` exige EF Core 9, que o Pomelo 8 (os) e o Npgsql 8 (billing) não aceitam. Por isso a última 8.x compatível é a 8.4.1.
- **Broker validado no spike S1:** imagem `rabbitmq:4.1-management`. O MassTransit não usa recursos depreciados do RabbitMQ 4.x (nada de `transient_nonexcl_queues`).
- **Recursos do MassTransit usados:**
  - State machine de saga com repositório EF Core.
  - **Outbox e inbox transacionais** no EF Core (`os-service` e `billing-service`).
  - Retry exponencial e fila `_error` quando as tentativas se esgotam.
  - *Delayed redelivery* fica para a Etapa 4: no RabbitMQ, ele exige o plugin `rabbitmq_delayed_message_exchange`, que a imagem padrão não traz.
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
  - **Fixação na 8.4.1:** sem as correções da 8.5.x nem as novidades da v9.
    - Para ir à 8.5.x, é preciso migrar os serviços para EF Core 9 (Pomelo 9 / Npgsql 9).
    - Plano de saída de longo prazo: Wolverine (MIT) ou o transporte SQS do próprio MassTransit.
  - **Métricas do management depreciadas no RabbitMQ 4.x** (`management_metrics_collection`): as métricas vão pelo plugin Prometheus (Etapa 5).
  - **O DynamoDB não é suportado pelo outbox do MassTransit:** o `execution-service` implementa um outbox próprio, com deduplicação por `MessageId`.

## Alternativas descartadas

- **Amazon SQS/SNS:** gerenciado e barato. Mas não roda localmente sem LocalStack e não tem UI para a demonstração. Continua como plano B, porque o MassTransit também tem transporte SQS.
- **Kafka (MSK):** caro e complexo demais para o volume do projeto. Log de eventos não é necessário aqui.
- **NServiceBus:** licença comercial.
- **RabbitMQ sem framework** (`RabbitMQ.Client`): seria preciso implementar outbox, retry, saga e test harness à mão.

## Resultado da implementação (Fase 4)

- **Versões.** MassTransit 8.4.1 nos 3 serviços. Outbox e inbox do EF Core no os-service (MySQL) e no billing (PostgreSQL); outbox e inbox próprios no DynamoDB, no execution-service.
- **Filas e mensagens.** Uma fila de comando por serviço e eventos publicados em exchanges, com os nomes versionados do catálogo (`oficina.<evento>.v1`). Retry exponencial por endpoint; esgotado, a mensagem vai para a fila `_error`, que tem alerta.
- **RabbitMQ no EKS.** RabbitMQ Cluster Operator v2.21.1, conferido por SHA-256. A v2.22+ exige cert-manager e RabbitMQ 4.2.4+. Imagem `rabbitmq:4.1-management`, um usuário por serviço via definitions importadas no boot e volume gp3. No kind: operator e `RabbitmqCluster` saudáveis, e a saga completa entre três namespaces.
- **Trace distribuído.** O contexto do trace passa nos headers das mensagens (ADR-010).
