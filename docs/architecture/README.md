# Documentação Arquitetural — Fase 3

Documentação da arquitetura corporativa em nuvem da Oficina Mecânica.

## Índice

- [Diagrama de Componentes](component-diagram.md) — visão de nuvem (API Gateway, Lambda, EKS, RDS, observabilidade).
- [Diagramas de Sequência](sequence-diagrams.md) — autenticação por CPF e abertura de ordem de serviço.
- [Modelo de Dados](database-model.md) — diagrama ER, relacionamentos e justificativa do banco.
- **RFCs** (decisões técnicas relevantes):
  - [RFC-001 — Escolha da nuvem (AWS)](rfcs/RFC-001-escolha-nuvem.md)
  - [RFC-002 — Escolha do banco de dados (RDS MySQL)](rfcs/RFC-002-escolha-banco.md)
  - [RFC-003 — Estratégia de autenticação (CPF + Lambda + JWT)](rfcs/RFC-003-estrategia-autenticacao.md)
- **ADRs** (decisões arquiteturais permanentes):
  - [ADR-001 — Padrão de comunicação (API Gateway + VPC Link)](adrs/ADR-001-padrao-comunicacao.md)
  - [ADR-002 — Escalabilidade com HPA](adrs/ADR-002-hpa.md)
  - [ADR-003 — Segregação em 4 repositórios](adrs/ADR-003-quatro-repositorios.md)
  - [ADR-004 — Observabilidade com New Relic](adrs/ADR-004-observabilidade.md)

## Fase 4 — microsserviços e saga (rascunho)

Plano da fase: [`docs/fase4`](../fase4/README.md).

- [Contratos de mensagens v1](../contratos-mensagens.md): catálogo, filas do RabbitMQ e máquina de estados da saga.
- **ADRs** (status *Proposto* até a revisão do grupo e os spikes da Etapa 0):
  - [ADR-005 — Divisão em três microsserviços](adrs/ADR-005-microsservicos.md)
  - [ADR-006 — Saga orquestrada no `os-service`](adrs/ADR-006-saga-orquestrada.md)
  - [ADR-007 — Mensageria com RabbitMQ e MassTransit 8.x](adrs/ADR-007-rabbitmq-masstransit.md)
  - [ADR-008 — Persistência poliglota](adrs/ADR-008-persistencia-poliglota.md)
  - [ADR-009 — Pagamento com Mercado Pago (Checkout Pro)](adrs/ADR-009-mercado-pago.md)

## Mapa dos repositórios

| Repositório | Papel |
| --- | --- |
| [`oficina-lambda-auth`](https://github.com/Duds27/oficina-lambda-auth) | Function Serverless de autenticação por CPF + API Gateway |
| [`oficina-infra-k8s`](https://github.com/Duds27/oficina-infra-k8s) | Terraform: VPC + EKS + add-ons |
| [`oficina-infra-database`](https://github.com/Duds27/oficina-infra-database) | Terraform: RDS MySQL gerenciado |
| [`oficina-app`](https://github.com/Duds27/tech-challenge-fiap) | Aplicação .NET (Clean Architecture) + manifestos K8s |
