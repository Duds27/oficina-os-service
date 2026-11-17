# ADR-010 — Observabilidade distribuída com OpenTelemetry (OTLP → New Relic)

- **Status:** Proposto: implementado e validado fora do New Relic na Fase 4; aguarda a revisão do grupo para passar a *Aceito*
- **Data:** 2026-11-17
- **Relaciona-se com:** [ADR-004](ADR-004-observabilidade.md) (substitui em parte: o destino continua o New Relic, mas o agente sai) e o spike S2 da [Etapa 0](../../fase4/etapa-0-fundacao.md)

## Contexto

Na Fase 4, uma OS passa por três serviços e pelo RabbitMQ. Para seguir a OS do início ao fim, o trace
precisa atravessar o broker, com o span do publish ligado ao do consumer em outro serviço. O spike S2
perguntava se o agente .NET do New Relic faz isso com o MassTransit ou se seria preciso OpenTelemetry.

## Decisão

**OpenTelemetry .NET nos 3 serviços, exportando por OTLP para o New Relic.**

- O MassTransit 8 emite spans de publish, send e consume (`ActivitySource` "MassTransit") e propaga o contexto W3C nos headers da mensagem.
- **Traces:** ASP.NET Core, HttpClient e os drivers de banco (MySqlConnector, Npgsql e o AWS SDK do DynamoDB).
- **Métricas:** HTTP, runtime, MassTransit e os Meters de negócio (`Oficina.Saga`, `Oficina.Billing`, `Oficina.Execucao`).
- **Logs:** Serilog → OTLP, com `trace.id`, `span.id` e o contexto da mensagem (`NumeroOS`, `CorrelationId`).
- **Exportação.** Só com `OTEL_EXPORTER_OTLP_ENDPOINT`. No EKS, o CD aponta para o New Relic quando há license key. O agente .NET do New Relic saiu da imagem do os-service, para não instrumentar em dobro.
- **Outbox próprio do execution-service.** A publicação acontece em outra thread, então o item grava o `traceparent`, e a publicação abre o span como filho dele.

## Consequências

- **Positivas:**
  - um padrão aberto, com a mesma instrumentação apontando para qualquer backend OTLP;
  - o trace atravessa o broker e os outboxes;
  - logs, métricas e traces saem pelo mesmo caminho, sem depender do `nri-bundle` para logs, que não cabia nos nós pequenos;
  - validado localmente com OpenTelemetry Collector + Jaeger.
- **Negativas e mitigação:**
  - a instrumentação do EF Core só existe em beta, então os spans de banco vêm dos drivers;
  - os Meters de negócio são estáticos, por isso os testes de métrica rodam numa coleção sem paralelismo;
  - o APM "clássico" do New Relic (entidade do agente) dá lugar à entidade OpenTelemetry.
- **Validação.** Em 2026-11-17, `scripts/e2e_observabilidade.py` conferiu, no coletor local:
  - um trace de 159 spans pelos 3 serviços;
  - o outbox do execution dentro dele;
  - as métricas de negócio;
  - os logs com o `NumeroOS` nos 3 serviços.

  A conferência no New Relic, com os serviços no EKS, está na Etapa 6 (6.0).

## Alternativas consideradas

- **Agente .NET do New Relic nos 3 serviços:** é a instrumentação da Fase 3, mas não há garantia de propagação pelo MassTransit sem configuração própria. Além disso, prende a instrumentação ao fornecedor.
- **Correlação só por `CorrelationId` nos logs:** não mostra tempos nem o service map.
