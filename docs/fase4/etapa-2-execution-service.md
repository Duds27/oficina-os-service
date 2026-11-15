# Etapa 2 — `execution-service` (Execução e Produção)

> [← Plano geral](README.md) · **Semanas:** 2–3 · **Frente:** C · **Depende de:** [Etapa 0](etapa-0-fundacao.md) (esqueleto + contratos)
> **Repositório:** `oficina-execution-service` · **Banco:** DynamoDB (o NoSQL exigido)

## Objetivo

Gerenciar a fila de execução das OS, registrar o andamento do diagnóstico e do reparo, e comunicar cada marco ao `os-service` por eventos.

## Tarefas

### 2.1 Domínio

- [ ] Agregado `Execucao`:
  - Dados da OS e do veículo: `OsId`, `CorrelationId`, `NumeroOS`, `Veiculo {Placa, Marca, Modelo, Ano}` e `Descricao`.
  - Andamento: `Etapa`, `Mecanico`, `Apontamentos[]` (tipo, texto, autor, data), datas de cada marco, `Version`.
- [ ] Ciclo de etapas:

  ```
  DiagnosticoPendente → EmDiagnostico → DiagnosticoConcluido → AguardandoExecucao → EmReparo → Finalizada
                   (qualquer etapa não final) → Cancelada
  ```

- [ ] Regras:
  - Só conclui diagnóstico se ele estiver em andamento.
  - `IniciarExecucao` só é aceito em `DiagnosticoConcluido`. Fora disso, responde com `ExecucaoRejeitada`.
  - `CancelarExecucao` em etapa final é ignorado (idempotente).
- [ ] *Fault injection* para a demo: com `FaultInjection__RejeitarExecucao=true`, todo `IniciarExecucao` responde `ExecucaoRejeitada` com motivo "falha simulada". O padrão é `false`, e o valor é logado na inicialização.

### 2.2 Persistência (DynamoDB, *single-table*)

| Item | PK | SK | Atributos principais |
| --- | --- | --- | --- |
| Execução | `EXEC#{osId}` | `META` | dados do agregado, `Version`, `GSI1PK = FILA#{etapa}`, `GSI1SK = {dataEntrada}#{osId}` |
| Apontamento | `EXEC#{osId}` | `EVT#{timestamp}#{ulid}` | tipo, texto, autor |
| Mensagem processada | `MSG#{messageId}` | `MSG` | `ExpiraEm` (TTL 7 dias) |
| Outbox | `OUTBOX#{id}` | `OUTBOX` | mensagem serializada, tipo, `GSI2PK = OUTBOX` |

- [ ] Repositório com `AWSSDK.DynamoDBv2` (API de baixo nível, por causa das condições e transações).
  - `GetExecucao(osId)` traz META + apontamentos com um único `Query` por PK.
  - `ListarFila(etapa)` faz `Query` no GSI1, por ordem de chegada.
  - `Salvar` usa `TransactWriteItems`:
    - Update do META com condição `Version = :v` (concorrência otimista).
    - Put do apontamento.
    - Put do outbox.
- [ ] **Idempotência + outbox numa única transação.** Cada consumer grava em `TransactWriteItems`:
  - O `MSG#{messageId}` com `attribute_not_exists(PK)`.
  - A alteração do agregado.
  - O item de outbox.

  Se a condição do `MSG` falhar, a mensagem já foi processada: o consumer só confirma e sai.
- [ ] `OutboxPublisher` (`BackgroundService`): lê o GSI2 `OUTBOX` a cada poucos segundos, publica no RabbitMQ e apaga o item. Garante pelo menos uma entrega, e os consumidores do outro lado já são idempotentes.
- [ ] Execução local: DynamoDB Local (ou LocalStack) no `docker-compose.yml`. A tabela é criada na inicialização **só** no ambiente `Development`. Na AWS, quem cria é o Terraform (2.5).

### 2.3 Mensageria

- [ ] MassTransit 8.x + RabbitMQ, com retry/redelivery no mesmo padrão do `os-service`.
- [ ] Consumers e eventos que geram (via outbox):

  | Consumer | Efeito | Evento publicado |
  | --- | --- | --- |
  | `SolicitarDiagnostico` | Cria a execução em `DiagnosticoPendente` | — |
  | `IniciarExecucao` | Leva para `AguardandoExecucao` | `ExecucaoIniciada` ou `ExecucaoRejeitada` |
  | `CancelarExecucao` | Leva para `Cancelada` | — |

- [ ] Eventos publicados a partir da API: `DiagnosticoIniciado`, `DiagnosticoConcluido` e `ExecucaoFinalizada`.
- [ ] Logs com `CorrelationId`, `NumeroOS` e tipo da mensagem em todo consumo.

### 2.4 API

| Método | Rota | Descrição |
| --- | --- | --- |
| GET | `/api/execucoes/fila?etapa=` | Fila por etapa, por ordem de chegada |
| GET | `/api/execucoes/{osId}` | Execução + linha do tempo de apontamentos |
| POST | `/api/execucoes/{osId}/diagnostico/iniciar` | Mecânico inicia o diagnóstico → `DiagnosticoIniciado` |
| POST | `/api/execucoes/{osId}/diagnostico/concluir` | Conclui o diagnóstico (com observações) → `DiagnosticoConcluido` |
| POST | `/api/execucoes/{osId}/reparo/iniciar` | Marca o início do reparo (apontamento) |
| POST | `/api/execucoes/{osId}/apontamentos` | Registra uma observação livre |
| POST | `/api/execucoes/{osId}/finalizar` | Finaliza o reparo → `ExecucaoFinalizada` |

- [ ] Todas as rotas exigem JWT. Os prefixos são exclusivos deste serviço, o que permite o roteamento por caminho no API Gateway.

### 2.5 Infraestrutura própria (`/infra`, Terraform)

- [ ] `aws_dynamodb_table`:
  - `PAY_PER_REQUEST`, `hash_key = PK`, `range_key = SK`.
  - GSI1 (`GSI1PK`/`GSI1SK`) e GSI2 (`GSI2PK`).
  - TTL em `ExpiraEm` e *point-in-time recovery* ligado.
- [ ] IAM role para a ServiceAccount (IRSA):
  - *Trust* no OIDC provider do EKS, lido via `terraform_remote_state` do `oficina-infra-k8s`.
  - Política de menor privilégio só na tabela e nos índices: `GetItem`, `Query`, `PutItem`, `UpdateItem`, `DeleteItem`, `TransactWriteItems`.
- [ ] Repositório ECR do serviço.
- [ ] Estado remoto no bucket S3 + trava DynamoDB já usados na Fase 3.
- [ ] Outputs: nome da tabela, ARN da role e URL do ECR.

### 2.6 Testes

- [ ] **Unitários:** transições do agregado, regras de rejeição e *fault injection*.
- [ ] **Consumers** com `ITestHarness` e repositório em memória: mensagens geradas, idempotência e rejeição.
- [ ] **Integração:**
  - Testcontainers com DynamoDB Local (ou `Testcontainers.LocalStack`) + `Testcontainers.RabbitMq`.
  - Cobrir: transação condicional, conflito de `Version`, dedup de mensagem, publicação pelo outbox e ordenação da fila.
- [ ] **Contrato:** snapshot JSON das mensagens publicadas e consumidas.
- [ ] Cobertura ≥ 80%.

## PRs sugeridos

1. **feat: domínio Execucao e regras de etapas**
2. **feat: repositório DynamoDB com transações condicionais e outbox**
3. **feat: consumers da saga + publicação de eventos**
4. **feat: API da fila de execução**
5. **infra: tabela DynamoDB, IRSA e ECR**
6. **test: integração com DynamoDB Local e RabbitMQ**

## Pronto quando

- [ ] No `docker-compose`, o `execution-service` cria a execução ao receber `SolicitarDiagnostico` e publica os eventos nos endpoints de diagnóstico e finalização.
- [ ] Mensagem reentregue não duplica efeito nem evento.
- [ ] A flag de *fault injection* força `ExecucaoRejeitada`.
- [ ] O Terraform aplica sem erro, e o pod acessa a tabela via IRSA, sem chaves de acesso.
- [ ] CI verde com cobertura ≥ 80%.
