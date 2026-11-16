# Etapa 1 — `os-service` + orquestrador da saga

> [← Plano geral](README.md) · **Semanas:** 1–3 · **Frente:** A · **Depende de:** [Etapa 0](etapa-0-fundacao.md) (contratos + spike S1)
> **Repositórios:** `oficina-os-service` (antigo `tech-challenge-fiap`) e `oficina-lambda-auth`

## Objetivo

Transformar o monólito no `os-service`, dono do ciclo de vida da OS, e implementar nele a **saga orquestrada** com todas as compensações. Os outros serviços entram simulados (test harness) até as etapas 2 e 3 ficarem prontas.

## Tarefas

### 1.1 Modelo de domínio e migrations

- [ ] Adicionar `CorrelationId` (Guid, índice único) em `OrdemServico`, gerado na abertura. É o id da saga.
- [ ] Incluir no `StatusOrdemServico` os valores `AguardandoPagamento = 6` e `Cancelada = 7`, **no fim do enum**.
  - O status é gravado como inteiro (`HasConversion<int>`), então não se pode renumerar os valores existentes.
- [ ] Atualizar `OrdemServico.ValidarTransicao`:
  - `AguardandoAprovacao → AguardandoPagamento` e `AguardandoPagamento → EmExecucao`.
  - Qualquer status não final → `Cancelada`.
  - Remover a regra baseada em `OrcamentoAprovado`, que agora é decidida pela saga.
- [ ] Ajustar `PrioridadeListagem` e `VisivelNaListagem` para os novos status. `Cancelada` sai da listagem padrão.
- [ ] Criar a entidade `HistoricoStatusOS`: `Id`, `OrdemServicoId`, `StatusAnterior`, `StatusNovo`, `Origem` (Usuario/Saga), `Motivo`, `CorrelationId`, `OcorridoEm`.
  - Gravar um registro em **toda** transição. Centralizar isso num método de domínio que aplica o status e gera o histórico.
- [ ] Restringir a inclusão e a remoção de itens a `Recebida`/`EmDiagnostico`. Hoje só bloqueia `Finalizada`/`Entregue`.
- [ ] Extrair a devolução de estoque de `RemoverItemAsync` para um método reutilizável (ex.: `EstoqueService.DevolverPecas(ordem)`), usado também pela compensação.
- [ ] Migration EF Core cobrindo os itens acima, mais as tabelas da saga e do MassTransit (1.2).

### 1.2 Mensageria (MassTransit 8.4.1 + RabbitMQ)

- [ ] Pacotes, todos fixados em **8.4.1**: `MassTransit`, `MassTransit.RabbitMQ`, `MassTransit.EntityFrameworkCore`. A 8.5.x exige EF Core 9 ([spike S1](../../spikes/S1-masstransit-saga/README.md)). O spike tem o código de referência da configuração abaixo.
- [ ] No `AppDbContext`:
  - `modelBuilder.AddInboxStateEntity()`, `AddOutboxMessageEntity()` e `AddOutboxStateEntity()`.
  - Mapeamento de `OrdemServicoSagaState`: `CorrelationId` PK, `CurrentState`, `OsId`, `OrcamentoId`, `PagamentoId`, `PrazoExpiraEm`, `RowVersion` para concorrência otimista (`DateTime` + `IsRowVersion()`, que o Pomelo mapeia para `timestamp(6) ... ON UPDATE`; validado no spike S1), datas.
- [ ] Registro no `Infrastructure/DependencyInjection.cs`:
  - `AddEntityFrameworkOutbox<AppDbContext>(o => { o.UseMySql(); o.UseBusOutbox(); })`.
  - `AddSagaStateMachine<OrdemServicoStateMachine, OrdemServicoSagaState>()` com `EntityFrameworkRepository` (`ExistingDbContext<AppDbContext>`, `UseMySql`, concorrência otimista).
  - `UsingRabbitMq` com host/usuário/senha vindos de configuração (`RabbitMq__*`) e `ConfigureEndpoints` (nomes kebab-case).
  - Retry: `UseMessageRetry` com backoff exponencial (ex.: 5 tentativas) + `UseDelayedRedelivery` (minutos). Esgotou → fila `_error`.
- [ ] Contratos em `Application/Contracts`, copiados da [definição de referência](../contratos-mensagens.md#21-definição-de-referência-c), com `[MessageUrn]` e `[EntityName]`. Enviar cada comando com endereço explícito (`GetSendEndpoint(new Uri("queue:<fila>"))`), usando as filas da [seção 3 do documento](../contratos-mensagens.md#3-topologia-no-rabbitmq).
- [ ] Health check do bus no `/health/ready` (o MassTransit registra o check; conferir a tag usada no filtro).
- [ ] `docker-compose.yml`: adicionar `rabbitmq:management` e as variáveis `RabbitMq__*`.

### 1.3 State machine `OrdemServicoStateMachine`

- [ ] Início: `CriarAsync` publica `OrdemServicoAberta` **pelo outbox, na mesma transação da OS**. A saga nasce com `Initially(When(OrdemServicoAberta))` → envia `SolicitarDiagnostico` → `AguardandoDiagnostico`.
- [ ] Transições do caminho feliz conforme a [tabela 5.2](README.md#52-máquina-de-estados-no-os-service).
  - A cada transição, uma *activity* (`AtualizarStatusOsActivity`) muda o status da OS e grava o histórico.
  - Usa o mesmo `DbContext`, então saga, OS e outbox são salvos na mesma transação.
- [ ] `DiagnosticoConcluido` → monta `GerarOrcamento` a partir dos itens da OS (preço-snapshot) e dos dados do cliente.
- [ ] Compensações conforme a [tabela 5.3](README.md#53-compensações):

  | Gatilho | Ações da saga |
  | --- | --- |
  | `OrcamentoRecusado` | `DevolverPecasActivity` + `Send(CancelarExecucao)` + OS → `Cancelada` |
  | `PagamentoRecusado` | `Send(CancelarOrcamento)` + devolver peças + `CancelarExecucao` + OS → `Cancelada` |
  | `ExecucaoRejeitada` | `Send(EstornarPagamento)` + devolver peças + OS → `Cancelada` (aguarda `PagamentoEstornado` para fechar) |
  | `PrazoExpirado` | Compensações conforme o estado em que a saga parou |

- [ ] Eventos fora de ordem ou duplicados: `Ignore(...)` explícito nos estados em que não fazem sentido. Nada de exceção que mande a mensagem para `_error` sem necessidade.
- [ ] Timeouts:
  - Ao entrar em `AguardandoAprovacao`, `AguardandoPagamento` e `AguardandoExecucao`, gravar `PrazoExpiraEm`.
  - `VerificadorPrazosSaga` (`BackgroundService`, a cada 30 s) busca sagas vencidas e publica `PrazoExpirado`.
  - Prazos em `Saga:Prazos:*`: 48 h em produção, 2 min na demo.
- [ ] Cancelamento manual (opcional): `POST /api/ordens-servico/{id}/cancelar` publica `CancelamentoSolicitado`, que dispara as mesmas compensações.

### 1.4 Ajustes de API

- [ ] **Remover** `PUT /api/ordens-servico/{id}/aprovar-orcamento`. A aprovação passa para o `billing-service`.
- [ ] Substituir o `PUT /{id}/status` genérico por `POST /api/ordens-servico/{id}/entregar` (`Finalizada → Entregue`). As outras transições são da saga.
- [ ] `GET /api/ordens-servico/{id}/historico`: lista de transições com origem e motivo.
- [ ] `GET /api/ordens-servico/{id}/saga`: estado atual da saga e prazo. Endpoint administrativo, útil no vídeo.
- [ ] `GET /internal/clientes/por-cpf/{cpf}`: retorna `{id, nome, ativo}`.
  - Exige o header `X-Internal-Key`, comparado com segredo vindo do Secrets Manager.
  - **Não** é roteado pelo API Gateway.
- [ ] Atualizar Swagger (summaries XML) e a tabela de endpoints do README.

### 1.5 Lambda de autenticação (`oficina-lambda-auth`)

- [ ] Trocar a consulta `mysql2` ao RDS por `GET {OS_SERVICE_URL}/internal/clientes/por-cpf/{cpf}` com `X-Internal-Key`. Timeout curto, e erro claro se o serviço não responder.
- [ ] Garantir que o JWT emitido tenha o claim `clienteId`. O `billing-service` usa esse claim para checar se quem aprova o orçamento é o dono dele.
- [ ] Remover credenciais de banco da Lambda e a regra de security group Lambda → RDS (Terraform do repo).
- [ ] Adaptar os testes da Lambda (mock do HTTP no lugar do banco).

### 1.6 Testes

- [ ] **Unitários de domínio:** novas transições, `Cancelada`, histórico, bloqueio de itens fora do diagnóstico.
- [ ] **State machine** (`MassTransit.Testing` / `ITestHarness`): cada transição, cada compensação, `PrazoExpirado` por estado e mensagem duplicada (idempotência).
- [ ] **BDD** em um novo projeto `tests/OficinaMecanica.BddTests` (Reqnroll + xUnit):
  - Feature "Ciclo de vida da OS coordenado por Saga" com os 4 cenários da [seção 8 do plano](README.md#8-estratégia-de-testes-e-qualidade).
  - Infra do teste: test harness + SQLite em memória (mesmo padrão dos testes de casos de uso atuais).
  - Consumidores-stub configuráveis por cenário simulam o `billing-service` e o `execution-service`.
- [ ] **Integração:**
  - Manter os testes atuais (Testcontainers MySQL).
  - Adicionar um teste com `Testcontainers.RabbitMq`: `POST /ordens-servico` → mensagem `SolicitarDiagnostico` na fila → saga persistida.
- [ ] **Cobertura ≥ 80%:**
  - Testes novos para `ClienteUseCases`, `VeiculoUseCases`, `ServicoUseCases`, validators sem teste e controllers.
  - Exclusões documentadas: `Migrations/`, `*.Designer.cs`, `Program.cs`, DTOs.

## PRs sugeridos

1. **feat: novos status, histórico de status e CorrelationId na OS** (1.1 + testes de domínio)
2. **feat: MassTransit + RabbitMQ + outbox/inbox** (1.2)
3. **feat: saga orquestrada da OS com compensações e timeouts** (1.3 + testes da state machine)
4. **feat: ajustes de API (entregar, histórico, saga, endpoint interno)** (1.4)
5. **test: BDD do ciclo de vida da OS** (1.6)
6. **test: cobertura ≥ 80%** (1.6)
7. `oficina-lambda-auth`: **feat: autenticação consulta o os-service via HTTP** (1.5)

## Pronto quando

- [ ] A saga completa roda com os outros serviços simulados: caminho feliz e as 3 compensações.
- [ ] O BDD está verde no CI e a cobertura passa de 80%.
- [ ] Nenhum código do `os-service` decide aprovação ou pagamento. Só reage a eventos.
- [ ] A Lambda autentica sem acessar banco.
- [ ] O `docker-compose up` sobe API + MySQL + RabbitMQ, e a abertura de OS publica `SolicitarDiagnostico`.
