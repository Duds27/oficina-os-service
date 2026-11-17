# Etapa 0 — Fundação e *contract-first*

> [← Plano geral](README.md) · **Semana:** 1 · **Frente:** todas (a Frente D conduz repos e CI) · **Depende de:** —
> **Próximas:** [Etapa 1](etapa-1-os-service.md), [Etapa 2](etapa-2-execution-service.md), [Etapa 3](etapa-3-billing-service.md), [Etapa 4](etapa-4-plataforma-cicd.md)

## Objetivo

Deixar os três repositórios prontos para trabalho em paralelo, com contratos de mensagens fechados e as incertezas técnicas resolvidas antes de escrever código de negócio.

## Tarefas

### 0.1 Organização

- [ ] Confirmar o **prazo oficial**, os integrantes e o responsável de cada frente (A, B, C, D). Ajustar as semanas na [seção 7 do plano](README.md#7-etapas).
- [ ] Decidir os pontos em aberto da [seção 13](README.md#13-pontos-para-confirmar-com-o-grupo): momento do pagamento e RDS separado.
- [ ] (Opcional) Criar um GitHub Project com uma issue por tarefa das etapas 0–6.

### 0.2 Repositórios

- [x] Renomear `tech-challenge-fiap` → `oficina-os-service` (feito com `gh repo rename` em 2026-11-15). O GitHub redireciona as URLs antigas.
  - Nos outros clones locais: `git remote set-url origin https://github.com/Duds27/oficina-os-service.git`.
  - [ ] Atualizar os links nos READMEs de `oficina-lambda-auth`, `oficina-infra-k8s` e `oficina-infra-database` (opcional, porque o redirecionamento funciona).
  - [ ] Conferir a política de confiança da role de deploy (OIDC) na AWS. Se ela citar `repo:Duds27/tech-challenge-fiap:*`, trocar por `repo:Duds27/oficina-os-service:*`.
- [x] Criar [`oficina-billing-service`](https://github.com/Duds27/oficina-billing-service) e [`oficina-execution-service`](https://github.com/Duds27/oficina-execution-service), públicos e vazios. O primeiro push foi o esqueleto, direto na `main`.
- [x] Esqueleto comum nos dois repos novos, copiando o padrão do `os-service`. Clones locais em `../oficina-billing-service` e `../oficina-execution-service`:
  - Solução .NET 8 com `src/Oficina.<Servico>.Domain|Application|Infrastructure|API` e `tests/Oficina.<Servico>.UnitTests|IntegrationTests`.
  - `Directory.Build.props` (nullable, analyzers, code style no build), `src/Directory.Build.props` (`TreatWarningsAsErrors`), `Directory.Packages.props` (versões centralizadas), `global.json` (.NET 8) e `.editorconfig`.
  - Dockerfile multi-stage com usuário não-root.
  - `docker-compose.yml` com o serviço, o banco (PostgreSQL ou DynamoDB Local) e o RabbitMQ (vhost `oficina`).
  - `/health/live` e `/health/ready`.
  - Serilog JSON + `CorrelationIdMiddleware` (cópia do `os-service`) e Swagger com JWT.
  - 5 testes por serviço: middleware (unitários) e health checks + correlação (integração, `WebApplicationFactory`).
  - Build sem avisos, testes verdes e build da imagem Docker aprovado no primeiro CI dos dois.
- [x] CI mínimo (`ci.yml`: restore → build → test → build da imagem) nos dois repos novos, com o job `build-test-quality`, que vira o *required check*.
  - O `os-service` já tem CI equivalente, com o job "Build e Testes".
  - Na Etapa 4, renomear esse job para `build-test-quality` junto com a atualização da proteção da `main`.
- [x] Proteger a `main` nos 3 repos (2026-11-15):
  - PR obrigatório com 1 aprovação.
  - Status check obrigatório: `Build e Testes` no os-service e `build-test-quality` nos novos.
  - Branch atualizada antes do merge.
  - Force-push e exclusão bloqueados.
  - Administradores podem furar a regra, como já era no `tech-challenge-fiap`.

### 0.3 SonarCloud

- [x] Criar a organização no SonarCloud vinculada ao GitHub (`duds27`) e um projeto por microsserviço (`Duds27_oficina-os-service`, `Duds27_oficina-billing-service`, `Duds27_oficina-execution-service`).
  - [x] A importação inicial também criou projetos para `oficina-infra-database`, `oficina-infra-k8s`, `oficina-lambda-auth` e para o repo privado `project-correlated-streams`. Os quatro foram removidos, e a importação automática de repos novos foi desligada (*Organization settings → Organization binding*).
  - [ ] A organização foi criada no período de teste do plano **Team** (pago). Antes de o teste acabar, mudar para o plano **Free**, que atende repos públicos, sem cadastrar cartão.
- [x] **Desligar a "Automatic Analysis"** nos 3 projetos de serviço. Projetos .NET precisam de análise via CI com `dotnet-sonarscanner`.
- [x] Cadastrar o secret `SONAR_TOKEN` nos 3 repos de serviço (2026-11-15). Ele só será testado de fato quando a análise entrar no CI, na [Etapa 4](etapa-4-plataforma-cicd.md).

### 0.4 Contratos de mensagens (v1)

- [x] Criar [`docs/contratos-mensagens.md`](../contratos-mensagens.md) no `os-service`. O documento tem:
  - Convenções: envelope, `[MessageUrn]` + `[EntityName]`, versionamento, serialização e idempotência.
  - Catálogo com 20 mensagens e a definição de referência em C#.
  - Topologia de filas no RabbitMQ.
  - Corridas conhecidas e testes de contrato.
- [x] Desenhar a state machine (mermaid `stateDiagram-v2`) no mesmo documento, a partir da [tabela 5.2](README.md#52-máquina-de-estados-no-os-service). Ela ganhou o estado `AguardandoEstorno` e termina em `Concluida`.
- [ ] **Revisão das frentes A, B e C** (obrigatória). Pontos para decidir na revisão:
  - Saída do campo `Prioridade`.
  - Regra da nova tentativa de pagamento no Checkout Pro ([seção 5 do documento](../contratos-mensagens.md#5-corridas-conhecidas-e-regras-de-cada-lado)).

### 0.5 Spikes (½ dia cada; registrar a conclusão no ADR correspondente)

| Spike | Pergunta | Pronto quando |
| --- | --- | --- |
| **S1** — MassTransit + saga ✅ ([resultado](../../spikes/S1-masstransit-saga/README.md): aprovado, versão fixada em 8.4.1) | A state machine persistida em MySQL (EF Core, concorrência otimista) + outbox funciona com o `AppDbContext` atual? O `[EntityName]` faz produtor e consumidor com namespaces diferentes usarem o mesmo exchange? | PoC: evento publicado → saga criada no MySQL → comando enviado, com o RabbitMQ em Docker. Mais uma mensagem entregue entre dois projetos com namespaces diferentes. Confirmar também a imagem `rabbitmq:4.1-management` dos composes com o MassTransit 8.x. |
| **S2** — Trace pelo broker ✅ (Etapa 5: OpenTelemetry → OTLP. O MassTransit propaga o contexto nos headers, e o trace atravessou os 3 serviços num coletor local; falta conferir no New Relic, 5.7) | O agente .NET do New Relic propaga o trace pelo RabbitMQ/MassTransit? Ou precisamos de OpenTelemetry → OTLP? | Um trace no New Relic com o span do publisher e o do consumer ligados |
| **S3** — Mercado Pago sandbox | Conseguimos criar preferência, pagar com cartão de teste, receber o webhook e estornar? | Os 4 passos executados; payloads de exemplo salvos para os testes com WireMock |
| **S4** — RabbitMQ no EKS | O RabbitMQ Cluster Operator sobe com volume persistente (EBS CSI) no cluster atual? | `RabbitmqCluster` saudável, UI acessível por port-forward e mensagem publicada de um pod |

### 0.6 ADRs (rascunho)

- [x] [`ADR-005`](../architecture/adrs/ADR-005-microsservicos.md) divisão em microsserviços.
- [x] [`ADR-006`](../architecture/adrs/ADR-006-saga-orquestrada.md) saga orquestrada.
- [x] [`ADR-007`](../architecture/adrs/ADR-007-rabbitmq-masstransit.md) RabbitMQ + MassTransit.
- [x] [`ADR-008`](../architecture/adrs/ADR-008-persistencia-poliglota.md) persistência poliglota.
- [x] [`ADR-009`](../architecture/adrs/ADR-009-mercado-pago.md) Mercado Pago.
- [x] Ficam em `docs/architecture/adrs/` do `os-service`, com status *Proposto*.
- [ ] Mudar para *Aceito* depois da revisão do grupo e do spike correspondente (S1 → 006/007, S3 → 009, S4 → 007).

## PRs sugeridos

1. `oficina-billing-service` e `oficina-execution-service`: **chore: esqueleto do serviço + CI mínimo**.
2. `oficina-os-service`: **docs: contratos de mensagens v1 + state machine**.
3. `oficina-os-service`: **docs: ADRs 005–009 (rascunho) + resultado dos spikes**.

## Pronto quando

- [x] Os 3 repos de serviço existem, com `main` protegida e CI verde.
- [x] O SonarCloud está configurado nos 3 (projeto criado e token cadastrado).
- [ ] Os contratos v1 estão aprovados pelas frentes A, B e C.
- [ ] Os 4 spikes estão concluídos, com a decisão registrada. Só o S1 foi feito.
