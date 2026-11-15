# Etapa 5 — Observabilidade distribuída

> [← Plano geral](README.md) · **Semana:** 5 · **Frente:** D (com apoio das frentes A, B e C) · **Depende de:** [Etapas 1–4](README.md#7-etapas) (serviços no EKS) + spike S2
> **Repositórios:** os 3 serviços + `oficina-infra-k8s` + `docs/observability` no `os-service`

## Objetivo

Seguir uma OS do início ao fim no New Relic, a ferramenta da Fase 3: um trace atravessando os 3 serviços e o RabbitMQ, logs correlacionados, um dashboard da saga e alertas.

## Tarefas

### 5.1 Traces (conforme o resultado do spike S2)

- [ ] **Caminho recomendado: OpenTelemetry .NET nos 3 serviços.**
  - Pacotes: `OpenTelemetry.Extensions.Hosting`, `Instrumentation.AspNetCore`, `Instrumentation.Http`, `Instrumentation.EntityFrameworkCore` e `Instrumentation.AWS` (DynamoDB).
  - `AddSource("MassTransit")`: o MassTransit emite spans de publish/consume e propaga o contexto nos headers da mensagem.
  - Exportador OTLP → `https://otlp.nr-data.net:4317`, com header `api-key` = license key (do Secret).
  - Resource: `service.name`, `service.version` (SHA do commit) e `deployment.environment`.
- [ ] Se o `os-service` trocar o agente New Relic por OTel, remover o agente da imagem para não instrumentar em dobro.
- [ ] Na chamada ao Mercado Pago, o span HTTP de saída já aparece via HttpClient. Conferir se ele fica dentro do trace do consumer.

### 5.2 Logs correlacionados

- [ ] Filtro do MassTransit (consume e publish) que coloca no `LogContext` do Serilog: `CorrelationId`, `NumeroOS`, `MessageType` e `EstadoSaga` (no `os-service`).
- [ ] Incluir `trace.id`/`span.id` nos logs (*logs in context*), para o New Relic ligar log ↔ trace.
- [ ] O envio de logs dos containers continua pelo `nri-bundle` da Fase 3. Conferir que os 3 namespaces novos estão incluídos.

### 5.3 Métricas de negócio

- [ ] `Meter("Oficina.Saga")` no `os-service`:
  - Contadores: `saga.iniciada`, `saga.concluida`, `saga.compensada{motivo}`.
  - Histograma: `saga.etapa.duracao{etapa}`.
- [ ] `Meter("Oficina.Billing")`: `orcamento.decisao{resultado}`, `pagamento.resultado{status}` e `pagamento.estorno`.
- [ ] `Meter("Oficina.Execucao")`: `execucao.fila.tamanho{etapa}` (gauge) e `execucao.duracao`.
- [ ] Exportar via OTel metrics para o New Relic.

### 5.4 RabbitMQ

- [ ] Métricas do plugin Prometheus (profundidade das filas, mensagens em `_error`, consumidores, taxa de publish/ack) coletadas pelo Prometheus agent do `nri-bundle` (anotações de scrape no `RabbitmqCluster`).

### 5.5 Dashboard "Saga de OS" (NRQL)

- [ ] Widgets:
  - Sagas iniciadas, concluídas e compensadas por dia.
  - Motivos de compensação.
  - Tempo médio por etapa da saga.
  - Funil: OS aberta → orçamento aprovado → pago → finalizado.
  - Taxa de aprovação de orçamento e de pagamento.
  - Mensagens em filas `_error` e profundidade das filas.
  - Latência p95 e taxa de erro por serviço.
- [ ] Exportar o JSON em `docs/observability/newrelic-dashboard-saga.json`, ao lado do dashboard da Fase 3.

### 5.6 Alertas (Terraform em `docs/observability/terraform`)

| Condição | Limiar sugerido | Severidade |
| --- | --- | --- |
| Mensagens em qualquer fila `_error` | > 0 por 5 min | Crítico |
| Falha definitiva de estorno (`EstornarPagamento` em `_error`) | ≥ 1 | Crítico |
| Compensações | > 5 por hora | Alerta |
| Webhook do Mercado Pago com 4xx/5xx | > 3 em 10 min | Alerta |
| Latência p95 por serviço | > 1 s por 5 min | Alerta |
| Saga parada além do prazo (sem `PrazoExpirado` processado) | > 0 | Alerta |

### 5.7 Validação

- [ ] Rodar o caminho feliz e o cenário de estorno no EKS.
- [ ] Encontrar o trace pelo `NumeroOS` e conferir o service map com os 3 serviços + RabbitMQ.
- [ ] Capturas para a documentação e o vídeo: trace, service map, dashboard e um alerta disparado.

## PRs sugeridos

1. Cada serviço: **feat: OpenTelemetry (traces + métricas) exportando para o New Relic**
2. Cada serviço: **feat: logs com contexto da mensagem e trace id**
3. `oficina-infra-k8s`: **infra: scrape das métricas do RabbitMQ**
4. `oficina-os-service`: **docs: dashboard e alertas da saga (Terraform)**

## Pronto quando

- [ ] Um único trace no New Relic cobre: requisição HTTP → publish → consumer no outro serviço → chamada ao Mercado Pago → evento de volta.
- [ ] Filtrar os logs por `NumeroOS` mostra os 3 serviços.
- [ ] O dashboard "Saga de OS" e os alertas estão versionados e aplicados.
