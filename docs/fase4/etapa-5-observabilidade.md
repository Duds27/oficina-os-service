# Etapa 5 — Observabilidade distribuída

> [← Plano geral](README.md) · **Semana:** 5 · **Frente:** D (com apoio das frentes A, B e C) · **Depende de:** [Etapas 1–4](README.md#7-etapas) (serviços no EKS, ver 5.0) + spike S2
> **Repositórios:** os 3 serviços + `oficina-infra-k8s` + `docs/observability` no `os-service`

## Objetivo

Seguir uma OS do início ao fim no New Relic, a ferramenta da Fase 3: um trace atravessando os 3 serviços e o RabbitMQ, logs correlacionados, um dashboard da saga e alertas.

## Tarefas

### 5.0 Implantação na AWS

Veio da [Etapa 4](etapa-4-plataforma-cicd.md#pronto-quando) em 2026-11-17. O código, os testes e os pipelines ficaram prontos lá. Aqui fica o que depende da conta AWS e das configurações do GitHub. A validação desta etapa (5.7) roda no EKS, então este bloco vem antes dela. Os itens 5.1 a 5.6 podem andar antes, com a validação local.

- [ ] **Credenciais.** As da AWS na máquina de desenvolvimento estavam inválidas em 2026-11-17 (`InvalidClientTokenId`). Os applies podem rodar pelos workflows (OIDC) ou com credenciais renovadas.
- [ ] **Capacidade dos nós.** Decidir o tipo de instância antes do apply do `infra-k8s`. Com `t3.micro` (cerca de 400 MiB alocáveis), o pod do RabbitMQ (512 MiB) não cabe. No Free Plan, `t3.small` também é elegível. A conta está no [README do infra-k8s](https://github.com/Duds27/oficina-infra-k8s#capacidade-dos-nós-decidir-antes-do-apply).
- [ ] **Role do deploy** (`AWS_DEPLOY_ROLE_ARN`, cadastrada em cada repo). Os secrets e os Environments da Fase 3 não existem mais nos repositórios. A role precisa de:
  - push nos ECRs novos;
  - acesso ao cluster (o criador vira admin pelo `enable_cluster_creator_admin_permissions`);
  - leitura dos secrets novos (`rabbitmq/*`, `billing/*` e a chave interna).
- [ ] **Applies, nesta ordem:**
  1. `oficina-infra-k8s` (homolog);
  2. o `infra/` do billing e do execution, pelo `infra.yml` (variables `AWS_REGION`, `TF_STATE_BUCKET` e `TF_LOCK_TABLE` no repositório);
  3. `oficina-lambda-auth`, pelo CD no push em `homolog`.
- [ ] **Secret da chave interna** (`{"key": "..."}`). Cadastrar o `INTERNAL_KEY_SECRET_ID` na Lambda e no os-service.
- [ ] **GitHub:**
  - criar as branches `homolog` e `prod`, que só recebem merge da `main` por PR;
  - criar os Environments `homolog`, `prod` (com aprovação) e `infra` (com *required reviewers*; hoje ele existe sem regra, e o apply rodaria sem aprovação);
  - cadastrar as variables de cada repo (README, seção CD).
- [ ] **Deploy.** Push em `homolog` em cada serviço, e o CD faz o deploy. Depois, apagar o namespace `oficina` da Fase 3.
- [ ] **Sandbox do Mercado Pago** (era a 4.9; spike S3):
  - contas de teste (vendedor e comprador);
  - token de teste e segredo do webhook no secret `billing/mercadopago`, preenchidos à mão;
  - `Pagamentos__Gateway=MercadoPago` no overlay `aws` do billing (a `NotificationUrl` já sai da URL do API Gateway);
  - os 4 passos do spike;
  - payloads reais nos testes com WireMock;
  - resultado registrado no [ADR-009](../architecture/adrs/ADR-009-mercado-pago.md).

### 5.1 Traces (conforme o resultado do spike S2)

- [x] **Caminho recomendado: OpenTelemetry .NET nos 3 serviços.**
  - Pacotes: `OpenTelemetry.Extensions.Hosting`, `Instrumentation.AspNetCore`, `Instrumentation.Http`, `Instrumentation.EntityFrameworkCore` e `Instrumentation.AWS` (DynamoDB).
  - `AddSource("MassTransit")`: o MassTransit emite spans de publish/consume e propaga o contexto nos headers da mensagem.
  - Exportador OTLP → `https://otlp.nr-data.net:4317`, com header `api-key` = license key (do Secret).
  - Resource: `service.name`, `service.version` (SHA do commit) e `deployment.environment`.
  - **Na implementação:**
    - **Pacotes.** OpenTelemetry 1.20.1 (a 1.21 tinha 5 dias), no target `net8.0`, com `Microsoft.Extensions.*` 8.
    - **Banco.** A instrumentação do EF Core só existe em beta. No lugar dela entraram os `ActivitySource` dos próprios drivers: `MySqlConnector` no os-service e `Npgsql` no billing. O DynamoDB entra pelo `OpenTelemetry.Instrumentation.AWS` 1.20.2.
    - **Quando exporta.** Só com `OTEL_EXPORTER_OTLP_ENDPOINT`. No EKS, o CD grava o endpoint do New Relic e o header `api-key` no Secret, e só quando há license key. O overlay aws passa a temporalidade delta e o limite de atributos (4095). A imagem recebe o SHA em `SERVICE_VERSION`.
    - **Outbox do execution.** Ele publica numa thread separada, então o trace se perdia ali. O item agora grava o `traceparent` de quem escreveu, e a publicação abre o span `outbox publish <Evento>` como filho dele. Os outboxes do MassTransit (os e billing) já guardam o contexto.
- [x] Se o `os-service` trocar o agente New Relic por OTel, remover o agente da imagem para não instrumentar em dobro. O agente e as variáveis `CORECLR_*` saíram do Dockerfile, e o `NEW_RELIC_APP_NAME` saiu do overlay.
- [ ] Na chamada ao Mercado Pago, o span HTTP de saída já aparece via HttpClient. Conferir se ele fica dentro do trace do consumer.
  - O HttpClient está instrumentado. Com o gateway Fake não há chamada HTTP, então a conferência fica para o sandbox (5.0 e 5.7).

### 5.2 Logs correlacionados

- [x] Filtro do MassTransit (consume e publish) que coloca no `LogContext` do Serilog: `CorrelationId`, `NumeroOS`, `MessageType` e `EstadoSaga` (no `os-service`).
  - **Na implementação:** é um filtro de consumo nos 3 serviços. O `EstadoSaga` vem de um filtro no pipe da saga, onde o estado está carregado. No publish, os logs já saem dentro do consumo, com as mesmas propriedades.
- [x] Incluir `trace.id`/`span.id` nos logs (*logs in context*), para o New Relic ligar log ↔ trace. Um enricher do Serilog põe os dois em todo log escrito dentro de um span, inclusive no JSON do console.
- [x] O envio de logs dos containers continua pelo `nri-bundle` da Fase 3. Conferir que os 3 namespaces novos estão incluídos.
  - **Mudou:** os logs vão por OTLP, pelo `Serilog.Sinks.OpenTelemetry` 4.2.0, já ligados ao trace. Isso não depende do `nri-bundle`, que pelo README da Fase 3 não cabia nos nós `t3.micro`. O JSON no stdout continua.

### 5.3 Métricas de negócio

- [x] `Meter("Oficina.Saga")` no `os-service`:
  - Contadores: `saga.iniciada`, `saga.concluida`, `saga.compensada{motivo}`.
  - Histograma: `saga.etapa.duracao{etapa}`.
- [x] `Meter("Oficina.Billing")`: `orcamento.decisao{resultado}`, `pagamento.resultado{status}` e `pagamento.estorno`.
- [x] `Meter("Oficina.Execucao")`: `execucao.fila.tamanho{etapa}` (gauge) e `execucao.duracao`.
- [x] Exportar via OTel metrics para o New Relic.
- **Na implementação:**
  - **`saga.compensada{motivo}`.** O motivo é o evento que disparou a compensação, e não o texto livre, para a métrica ter poucos valores. No prazo, ele leva o estado (`PrazoExpirado.AguardandoPagamento`).
  - **`saga.etapa.duracao{etapa}`.** Usa a coluna nova `EstadoDesde` da saga (migration `AddEstadoDesdeSaga`), gravada ao entrar em cada estado. O Initial do MassTransit fica de fora.
  - **Gauge `saga.prazos.atrasados`.** Entrou a mais, para o alerta de saga parada. O verificador de prazos o alimenta a cada rodada, com as sagas vencidas há mais de duas rodadas.
  - **`orcamento.decisao`** também conta `expirado`, e **`pagamento.resultado`**, `tardio`, que é o pagamento aprovado depois de encerrado e estornado sozinho.
  - **`execucao.fila.tamanho`.** É contado a cada 30 s por um serviço em segundo plano, e a coleta só lê memória. **`execucao.duracao`** só é gravada depois de salvar.
  - **Testes.** As medições têm testes com `MeterListener`, numa coleção sem paralelismo, porque os Meters são estáticos.

### 5.4 RabbitMQ

- [x] Métricas do plugin Prometheus (profundidade das filas, mensagens em `_error`, consumidores, taxa de publish/ack) coletadas pelo Prometheus agent do `nri-bundle` (anotações de scrape no `RabbitmqCluster`).
  - **Na implementação:**
    - **Por fila.** Com `prometheus.return_per_object_metrics = true`, as métricas saem por fila, como `rabbitmq_queue_messages{queue="billing-estornar-pagamento_error"}`. Sem isso, só sai o total do nó. Foi conferido num container `rabbitmq:4.1-management`.
    - **Coleta.** O pod é anotado com `prometheus.io/scrape` (porta 15692), e o `newrelic-prometheus-agent` do `nri-bundle` ficou ligado.

### 5.5 Dashboard "Saga de OS" (NRQL)

- [x] Widgets:
  - Sagas iniciadas, concluídas e compensadas por dia.
  - Motivos de compensação.
  - Tempo médio por etapa da saga.
  - Funil: OS aberta → orçamento aprovado → pago → finalizado.
  - Taxa de aprovação de orçamento e de pagamento.
  - Mensagens em filas `_error` e profundidade das filas.
  - Latência p95 e taxa de erro por serviço.
- [x] Exportar o JSON em `docs/observability/newrelic-dashboard-saga.json`, ao lado do dashboard da Fase 3.
  - **Na implementação:**
    - **Páginas.** São 14 widgets em 3 páginas: Saga, Filas e Serviços.
    - **Funil.** É um gráfico de barras com as quatro contagens. O `funnel()` do NRQL só funciona sobre eventos, e aqui os dados são métricas.
    - **Terraform.** O dashboard também é aplicado pelo Terraform (`newrelic_one_dashboard_json`), que põe a conta em cada consulta.

### 5.6 Alertas (Terraform em `docs/observability/terraform`)

| Condição | Limiar sugerido | Severidade |
| --- | --- | --- |
| Mensagens em qualquer fila `_error` | > 0 por 5 min | Crítico |
| Falha definitiva de estorno (`EstornarPagamento` em `_error`) | ≥ 1 | Crítico |
| Compensações | > 5 por hora | Alerta |
| Webhook do Mercado Pago com 4xx/5xx | > 3 em 10 min | Alerta |
| Latência p95 por serviço | > 1 s por 5 min | Alerta |
| Saga parada além do prazo (sem `PrazoExpirado` processado) | > 0 | Alerta |

- [x] **Na implementação:** `docs/observability/terraform/alertas.tf`.
  - **Políticas.** São duas, crítico e alerta, porque o New Relic exige limiar crítico em toda condição.
  - **Condições.** São as 6 da tabela, mais duas: o log `ALERTA: estorno...` do billing, que dispara antes da métrica da fila, e a falha de OS da Fase 3, agora pelos logs OTLP.
  - **Ambiente.** As condições sobre os serviços filtram `deployment.environment`.
  - **E-mail.** O workflow de e-mail é opcional.
  - **Testes.** O `terraform test` tem 6 casos com o provider mockado e roda no CI do os-service.

### 5.7 Validação

- [ ] Rodar o caminho feliz e o cenário de estorno no EKS.
- [ ] Encontrar o trace pelo `NumeroOS` e conferir o service map com os 3 serviços + RabbitMQ.
- [ ] Capturas para a documentação e o vídeo: trace, service map, dashboard e um alerta disparado.

## PRs sugeridos

Nas branches `feat/etapa-5-observabilidade` (os 3 serviços) e `feat/etapa-5-rabbitmq-metricas` (infra-k8s). Os PRs 1 e 2 viraram um commit por serviço, porque dividem a mesma configuração (`Observabilidade/Telemetria.cs`). No os-service entraram também o coletor local e a verificação de ponta a ponta.

1. Cada serviço: **feat: OpenTelemetry (traces + métricas) exportando para o New Relic**
2. Cada serviço: **feat: logs com contexto da mensagem e trace id**
3. `oficina-infra-k8s`: **infra: scrape das métricas do RabbitMQ**
4. `oficina-os-service`: **docs: dashboard e alertas da saga (Terraform)**

## Pronto quando

- [ ] Da Etapa 4:
  - um merge em `homolog` de qualquer serviço builda, testa, publica a imagem e faz o rollout no EKS sem passo manual;
  - os 3 serviços + RabbitMQ rodam no EKS, acessíveis pelo API Gateway com JWT, e o webhook é acessível sem JWT.
- [ ] O Terraform do `execution-service` aplica sem erro, e o pod acessa a tabela via IRSA, sem chaves de acesso (veio da Etapa 2, via Etapa 4).
- [ ] No sandbox do Mercado Pago: preferência criada, pagamento com cartão de teste, webhook recebido pelo API Gateway e validado, `PagamentoConfirmado` publicado e estorno realizado (veio da Etapa 3, via Etapa 4).
- [ ] Um único trace no New Relic cobre: requisição HTTP → publish → consumer no outro serviço → chamada ao Mercado Pago → evento de volta.
  - **Conferido fora do New Relic em 2026-11-17**, com o perfil `observabilidade` do `docker-compose.full.yml` (OpenTelemetry Collector + Jaeger) e o `scripts/e2e_observabilidade.py`. O trace do pagamento tem 159 spans nos 3 serviços. Ele começa na simulação do pagamento no billing e passa pelo outbox, por `PagamentoConfirmado`, pela saga do os-service, por `IniciarExecucao` e pelo outbox do execution, até `ExecucaoIniciada` voltar ao os-service.
  - Faltam o New Relic (5.0) e a chamada real ao Mercado Pago (sandbox).
- [ ] Filtrar os logs por `NumeroOS` mostra os 3 serviços.
  - No mesmo teste, os logs com o `NumeroOS` da rodada chegaram dos 3 serviços ao coletor, todos com trace id. No New Relic, depende do 5.0.
- [ ] O dashboard "Saga de OS" e os alertas estão versionados e aplicados.
  - Versionados e testados (`terraform test`). O apply precisa da conta e da User key do New Relic, e os dados só chegam com os serviços no EKS (5.0).
