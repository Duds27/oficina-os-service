# Documentação de arquitetura

A Oficina Mecânica tem três microsserviços, cada um com o seu banco, e uma saga orquestrada pelo
os-service. Os serviços rodam no EKS e se comunicam por RabbitMQ. Plano da Fase 4:
[`docs/fase4`](../fase4/README.md).

## Fase 4: microsserviços e saga

- [Diagrama de componentes](component-diagram.md): os 3 serviços, bancos, RabbitMQ, API Gateway, Lambda, Mercado Pago e New Relic.
- [Diagramas de sequência](sequence-diagrams.md): o caminho feliz da saga e as compensações (C1 orçamento recusado, C2 pagamento recusado ou prazo, C3 execução rejeitada com estorno).
- [Modelo de dados](database-model.md): ER do MySQL (os) e do PostgreSQL (billing) e os padrões de acesso do DynamoDB (execution).
- [Contratos de mensagens v1](../contratos-mensagens.md): catálogo, filas do RabbitMQ, máquina de estados da saga e regras de corrida.
- [Observabilidade](../observability/README.md): OpenTelemetry → New Relic, dashboard "Saga de OS" e alertas.
- Contratos HTTP: `docs/openapi.json` de cada serviço e a collection Postman em [`postman/`](../../postman).

**ADRs da Fase 4.** O status é *Proposto* até a revisão do grupo. As de 005 a 010 estão implementadas, e
cada uma termina com o resultado da implementação. A 011 adapta a infra à conta AWS do grupo.

- [ADR-005 — Divisão em três microsserviços](adrs/ADR-005-microsservicos.md)
- [ADR-006 — Saga orquestrada no `os-service`](adrs/ADR-006-saga-orquestrada.md)
- [ADR-007 — Mensageria com RabbitMQ e MassTransit 8.x](adrs/ADR-007-rabbitmq-masstransit.md)
- [ADR-008 — Persistência poliglota](adrs/ADR-008-persistencia-poliglota.md)
- [ADR-009 — Pagamento com Mercado Pago (Checkout Pro)](adrs/ADR-009-mercado-pago.md)
- [ADR-010 — Observabilidade distribuída com OpenTelemetry](adrs/ADR-010-opentelemetry.md)
- [ADR-011 — Conta AWS restrita: `us-east-2`, EKS Pod Identity e chave de acesso no CI](adrs/ADR-011-conta-aws-restrita.md)

## Mapa dos repositórios

| Repositório | Papel |
| --- | --- |
| [`oficina-os-service`](https://github.com/Duds27/oficina-os-service) | Clientes, veículos, catálogo, OS e a saga. Também guarda a documentação geral, o compose completo e a collection Postman. |
| [`oficina-billing-service`](https://github.com/Duds27/oficina-billing-service) | Orçamento, aprovação, pagamento e estorno (Mercado Pago). Terraform do RDS PostgreSQL. |
| [`oficina-execution-service`](https://github.com/Duds27/oficina-execution-service) | Fila da oficina, diagnóstico, reparo e finalização. Terraform da tabela DynamoDB e da role do pod (Pod Identity). |
| [`oficina-lambda-auth`](https://github.com/Duds27/oficina-lambda-auth) | Lambda de autenticação por CPF e API Gateway (rotas por serviço) |
| [`oficina-infra-k8s`](https://github.com/Duds27/oficina-infra-k8s) | VPC, EKS, add-ons, RabbitMQ (Cluster Operator), namespaces e NLB por serviço |
| [`oficina-infra-database`](https://github.com/Duds27/oficina-infra-database) | RDS MySQL do os-service |

## Fase 3 (histórico)

A Fase 3 tinha um serviço só, a API .NET (`oficina-app`, hoje `oficina-os-service`). Os diagramas daquela
fase estão no fim de cada documento acima.

- **RFCs:**
  - [RFC-001 — Escolha da nuvem (AWS)](rfcs/RFC-001-escolha-nuvem.md)
  - [RFC-002 — Escolha do banco de dados (RDS MySQL)](rfcs/RFC-002-escolha-banco.md)
  - [RFC-003 — Estratégia de autenticação (CPF + Lambda + JWT)](rfcs/RFC-003-estrategia-autenticacao.md)
- **ADRs:**
  - [ADR-001 — Padrão de comunicação (API Gateway + VPC Link)](adrs/ADR-001-padrao-comunicacao.md)
  - [ADR-002 — Escalabilidade com HPA](adrs/ADR-002-hpa.md)
  - [ADR-003 — Segregação em 4 repositórios](adrs/ADR-003-quatro-repositorios.md), parcialmente substituída pela ADR-005
  - [ADR-004 — Observabilidade com New Relic](adrs/ADR-004-observabilidade.md), parcialmente substituída pela ADR-010
