# Observabilidade (New Relic)

Na Fase 4, os 3 microsserviços mandam **traces, métricas e logs por OpenTelemetry (OTLP)** para o New
Relic. O RabbitMQ entra pelas métricas Prometheus, coletadas pelo `nri-bundle`. O objetivo é seguir uma OS
do início ao fim: um trace atravessando os serviços e o broker, os logs ligados a ele, e um dashboard e
alertas da saga.

## De onde vem cada dado

| Dado | Origem | No New Relic |
| --- | --- | --- |
| Traces | OpenTelemetry .NET nos 3 serviços: HTTP, HttpClient (Mercado Pago), MassTransit (contexto nos headers da mensagem), MySQL, PostgreSQL e DynamoDB | `Span` (`service.name`, `span.kind`, `duration.ms`, `http.route`...) |
| Métricas de negócio | Meters `Oficina.Saga`, `Oficina.Billing` e `Oficina.Execucao` | `Metric` (ex.: `saga.compensada` com o atributo `motivo`) |
| Métricas técnicas | HTTP, runtime do .NET e MassTransit | `Metric` |
| Logs | Serilog → OTLP. Todo log tem `trace.id`/`span.id`; os do consumo têm `MessageType`, `CorrelationId` e `NumeroOS` (e `EstadoSaga` na saga) | `Log`, ligado ao trace |
| RabbitMQ | Plugin Prometheus por fila (`rabbitmq_queue_messages{queue}`), Prometheus agent do `nri-bundle` | `Metric` |
| Kubernetes | `nri-bundle` (Fase 3) | `K8sContainerSample` etc. |

O CD liga o envio quando há a license key: ele grava `OTEL_EXPORTER_OTLP_ENDPOINT` e
`OTEL_EXPORTER_OTLP_HEADERS=api-key=...` no Secret do pod. Sem a key, nada é exportado.

### Métricas de negócio

| Métrica | Atributos | Significado |
| --- | --- | --- |
| `saga.iniciada`, `saga.concluida` | | Sagas abertas e concluídas |
| `saga.compensada` | `motivo` | Evento que disparou a compensação (`OrcamentoRecusado`, `PagamentoRecusado`, `ExecucaoRejeitada`, `CancelamentoSolicitado`, `PrazoExpirado.<estado>`) |
| `saga.etapa.duracao` (s) | `etapa` | Tempo que a saga passou em cada estado |
| `saga.prazos.atrasados` | | Sagas com prazo vencido há mais de duas rodadas do verificador |
| `orcamento.decisao` | `resultado` | `aprovado`, `recusado` ou `expirado` |
| `pagamento.resultado` | `status` | `aprovado`, `recusado` ou `tardio` (aprovado depois de encerrado, estornado sozinho) |
| `pagamento.estorno` | | Estornos (compensação da saga ou automáticos) |
| `execucao.duracao` (s) | | Do início do reparo à finalização |
| `execucao.fila.tamanho` | `etapa` | OS em cada etapa da fila (gauge; use `latest()`) |

## Dashboard "Saga de OS"

[`newrelic-dashboard-saga.json`](newrelic-dashboard-saga.json), em três páginas:

- **Saga:**
  - sagas iniciadas, concluídas e compensadas por dia;
  - motivos de compensação;
  - funil (OS aberta → orçamento aprovado → pago → finalizado);
  - tempo médio por etapa;
  - taxas de aprovação de orçamento e de pagamento;
  - prazos atrasados e estornos.
- **Filas:**
  - mensagens nas filas `_error`;
  - profundidade das filas;
  - fila da oficina por etapa;
  - duração da execução.
- **Serviços:**
  - latência p95 e taxa de erro por serviço;
  - webhook do Mercado Pago por status;
  - logs de erro.

Para importar à mão, use *New Relic → Dashboards → Import*. As consultas vêm com `accountIds: [0]`; troque
pela sua conta. O Terraform abaixo já faz essa troca.

## Alertas

Em [`terraform/alertas.tf`](terraform/alertas.tf), com duas políticas: **crítico** e **alerta**. O New
Relic exige um limiar crítico em toda condição, então a severidade fica na política.

| Condição | Limiar | Política |
| --- | --- | --- |
| Mensagens em qualquer fila `_error` | > 0 por 5 min | crítico |
| Falha definitiva de estorno (`billing-estornar-pagamento_error`) | ≥ 1 | crítico |
| Falha definitiva de estorno (log `ALERTA: estorno...` do billing) | > 0 | crítico |
| Compensações | > 5 por hora | alerta |
| Webhook do Mercado Pago com 4xx/5xx | > 3 em 10 min | alerta |
| Latência p95 por serviço | > 1 s por 5 min | alerta |
| Saga parada além do prazo (`saga.prazos.atrasados`) | > 0 por 5 min | alerta |
| Falha no processamento de OS (log da Fase 3) | > 0 | alerta |

As condições sobre os serviços filtram `deployment.environment` (variável `ambiente`). Com
`email_alertas`, um workflow manda os incidentes das duas políticas por e-mail.

## Aplicar

```bash
cd docs/observability/terraform
terraform init
TF_VAR_newrelic_api_key=<User key NRAK-...> terraform apply -var="newrelic_account_id=<id>" -var="ambiente=homolog"
```

A User key é a da API NerdGraph, não a license key de ingestão. `terraform test` roda sem conta, com o
provider mockado (6 casos), e o CI do os-service roda `fmt`, `validate` e `test`.

## Conferir sem New Relic

O perfil `observabilidade` do `docker-compose.full.yml` troca o New Relic por um OpenTelemetry Collector
local com Jaeger. O [`scripts/e2e_observabilidade.py`](../../scripts/e2e_observabilidade.py) roda a saga
e confere quatro coisas:

- um trace com spans dos 3 serviços;
- o outbox do execution dentro dele;
- as métricas de negócio no coletor;
- os logs da OS nos 3 serviços, com trace id.

Em 2026-11-17, o trace do pagamento passou por todo o caminho. Ele começa na simulação do pagamento no
billing e passa pelo outbox, por `PagamentoConfirmado`, pela saga do os-service, por `IniciarExecucao` e
pelo outbox do execution, até `ExecucaoIniciada` voltar ao os-service.

## Fase 3 (histórico)

A Fase 3 tinha um serviço só, monitorado pelo agente .NET do New Relic (APM). Os logs eram JSON do Serilog,
com os eventos `os_criada`, `os_status_alterado` e `os_falha_processamento`. O dashboard dessa fase está
em [`newrelic-dashboard.json`](newrelic-dashboard.json). Na Fase 4, o agente saiu da imagem: o
OpenTelemetry instrumentaria tudo de novo. Os eventos de log continuam e agora chegam por OTLP, então as
consultas `FROM Log WHERE Evento = ...` seguem valendo.
