# Etapa 2 — `execution-service` (Execução e Produção)

> [← Plano geral](README.md) · **Semanas:** 2–3 · **Frente:** C · **Depende de:** [Etapa 0](etapa-0-fundacao.md) (esqueleto + contratos)
> **Repositório:** `oficina-execution-service` · **Banco:** DynamoDB (o NoSQL exigido)

## Objetivo

Gerenciar a fila de execução das OS, registrar o andamento do diagnóstico e do reparo, e comunicar cada marco ao `os-service` por eventos.

## Tarefas

### 2.1 Domínio

- [x] Agregado `Execucao`:
  - Dados da OS e do veículo: `OsId`, `CorrelationId`, `NumeroOS`, `Veiculo {Placa, Marca, Modelo, Ano}` e `Descricao`.
  - Andamento: `Etapa`, `Mecanico`, `Apontamentos[]` (tipo, texto, autor, data), datas de cada marco, `Version`.
- [x] Ciclo de etapas:

  ```
  DiagnosticoPendente → EmDiagnostico → DiagnosticoConcluido → AguardandoExecucao → EmReparo → Finalizada
                   (qualquer etapa não final) → Cancelada
  ```

- [x] Regras:
  - Só conclui diagnóstico se ele estiver em andamento.
  - `IniciarExecucao` só é aceito em `DiagnosticoConcluido`. Fora disso, responde com `ExecucaoRejeitada`.
  - `CancelarExecucao` em etapa final é ignorado (idempotente).
  - **Decidido na implementação:** a rejeição de uma execução em `DiagnosticoConcluido` (na prática, a falha simulada) também a encerra como `Cancelada`. A saga compensa com C3 sem mandar `CancelarExecucao`, e a execução ficaria presa na fila. Nas outras etapas, a rejeição não muda nada.
  - **Regra 5 da seção 5 dos contratos:** `CancelarExecucao` antes do `SolicitarDiagnostico` registra a execução já cancelada, e o pedido atrasado é ignorado.
- [x] *Fault injection* para a demo: com `FaultInjection__RejeitarExecucao=true`, todo `IniciarExecucao` responde `ExecucaoRejeitada` com motivo "falha simulada". O padrão é `false`, e o valor é logado na inicialização.

### 2.2 Persistência (DynamoDB, *single-table*)

| Item | PK | SK | Atributos principais |
| --- | --- | --- | --- |
| Execução | `EXEC#{osId}` | `META` | dados do agregado, `Version`, `GSI1PK = FILA#{etapa}`, `GSI1SK = {dataEntrada}#{osId}` |
| Apontamento | `EXEC#{osId}` | `EVT#{timestamp}#{id}` | tipo, texto, autor |
| Mensagem processada | `MSG#{messageId}` | `MSG` | `ExpiraEm` (TTL 7 dias) |
| Outbox | `OUTBOX#{id}` | `OUTBOX` | mensagem serializada, tipo, `GSI2PK = OUTBOX`, `GSI2SK = {criadoEm}#{id}` |

- [x] Repositório com `AWSSDK.DynamoDBv2` (API de baixo nível, por causa das condições e transações).
  - `GetExecucao(osId)` traz META + apontamentos com um único `Query` por PK.
  - `ListarFila(etapa)` faz `Query` no GSI1, por ordem de chegada.
  - `Salvar` usa `TransactWriteItems`:
    - Update do META com condição `Version = :v` (concorrência otimista).
    - Put do apontamento.
    - Put do outbox.
  - **Na implementação:** o META é gravado com `Put` condicional (substitui o item inteiro, com a mesma condição na `Version`). O sufixo do apontamento é um GUID: a ordem vem do timestamp, e o sufixo só garante unicidade, sem pacote extra de ULID. O GSI2 ganhou a chave de ordenação `GSI2SK`, para o outbox sair na ordem de criação.
  - SDK v4 (`AWSSDK.DynamoDBv2` 4.0) + `AWSSDK.SecurityToken`, sem o qual o SDK não usa as credenciais do IRSA.
- [x] **Idempotência + outbox numa única transação.** Cada consumer grava em `TransactWriteItems`:
  - O `MSG#{messageId}` com `attribute_not_exists(PK)`.
  - A alteração do agregado.
  - O item de outbox.

  Se a condição do `MSG` falhar, a mensagem já foi processada: o consumer só confirma e sai.
- [x] `OutboxPublisher` (`BackgroundService`): lê o GSI2 `OUTBOX` a cada poucos segundos, publica no RabbitMQ e apaga o item. Garante pelo menos uma entrega, e os consumidores do outro lado já são idempotentes.
  - O `MessageId` publicado é o id do item do outbox: se cair entre publicar e apagar, a republicação tem o mesmo `MessageId`, e o inbox do os-service descarta a repetida.
- [x] Execução local: DynamoDB Local (ou LocalStack) no `docker-compose.yml`. A tabela é criada na inicialização **só** no ambiente `Development`. Na AWS, quem cria é o Terraform (2.5).

### 2.3 Mensageria

- [x] MassTransit 8.4.1 + RabbitMQ, com retry/redelivery no mesmo padrão do `os-service`.
  - Como no os-service, só retry por enquanto. O redelivery atrasado precisa do plugin de *delayed exchange* e fica para a Etapa 4.
- [x] Consumers e eventos que geram (via outbox):

  | Consumer | Efeito | Evento publicado |
  | --- | --- | --- |
  | `SolicitarDiagnostico` | Cria a execução em `DiagnosticoPendente` | — |
  | `IniciarExecucao` | Leva para `AguardandoExecucao` | `ExecucaoIniciada` ou `ExecucaoRejeitada` |
  | `CancelarExecucao` | Leva para `Cancelada` | — |

- [x] Eventos publicados a partir da API: `DiagnosticoIniciado`, `DiagnosticoConcluido` e `ExecucaoFinalizada`.
- [x] Logs com `CorrelationId`, `NumeroOS` e tipo da mensagem em todo consumo. Também `OsId` e `MessageId`, por um filtro de consumo com escopo de log.

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

- [x] Todas as rotas exigem JWT. Os prefixos são exclusivos deste serviço, o que permite o roteamento por caminho no API Gateway.
  - **Decidido na implementação:** a política exige o papel `Admin` ou `Mecanico`. O token de cliente da Lambda (papel `Cliente`) recebe 403.
  - Erros: 404 sem execução, 409 fora da etapa (com o motivo), 400 para parâmetro ou corpo inválido. Conflito de versão com um consumidor: a API relê e aplica de novo até 3 vezes; depois, 409.

### 2.5 Infraestrutura própria (`/infra`, Terraform)

- [x] `aws_dynamodb_table`:
  - `PAY_PER_REQUEST`, `hash_key = PK`, `range_key = SK`.
  - GSI1 (`GSI1PK`/`GSI1SK`) e GSI2 (`GSI2PK`/`GSI2SK`).
  - TTL em `ExpiraEm` e *point-in-time recovery* ligado. Proteção contra exclusão em `prod`.
- [x] IAM role para a ServiceAccount (IRSA):
  - *Trust* no OIDC provider do EKS, lido via `terraform_remote_state` do `oficina-infra-k8s`.
  - Política de menor privilégio só na tabela e nos índices: `GetItem`, `Query`, `PutItem`, `UpdateItem`, `DeleteItem`, `TransactWriteItems`.
  - **Correção:** não existe ação IAM `TransactWriteItems`. A transação é autorizada item a item, pela ação de cada um. A política tem `ConditionCheckItem` no lugar dela.
  - O *trust* aceita só a ServiceAccount `execution-service` do namespace `oficina-execution`.
- [x] Repositório ECR do serviço (um por ambiente, com scan no push e retenção das 20 últimas imagens).
- [x] Estado remoto no bucket S3 + trava DynamoDB já usados na Fase 3 (`execution-service/<ambiente>/terraform.tfstate`).
- [x] Outputs: nome da tabela, ARN da role e URL do ECR.
- [x] `terraform test` com provider mockado (sem conta AWS): tabela, *trust* do IRSA, proteção em `prod` e ambiente inválido. O CI ganhou o job `terraform` (`fmt`, `validate` e `test`).

### 2.6 Testes

- [x] **Unitários:** transições do agregado, regras de rejeição e *fault injection*.
- [x] **Consumers** com `ITestHarness` e repositório em memória: mensagens geradas, idempotência e rejeição.
- [x] **Integração:**
  - Testcontainers com DynamoDB Local (ou `Testcontainers.LocalStack`) + `Testcontainers.RabbitMq`.
  - Cobrir: transação condicional, conflito de `Version`, dedup de mensagem, publicação pelo outbox e ordenação da fila.
  - Ponta a ponta com RabbitMQ 4.1: um bus de teste no papel do os-service manda os comandos pelas filas do contrato e recebe os eventos.
- [x] **Contrato:** snapshot JSON das mensagens publicadas e consumidas, com o serializador do MassTransit. Mostrou que o `decimal` vai como texto no fio (registrado na seção 1.5 dos contratos).
- [x] Cobertura ≥ 80%: cerca de 97% de linhas nos dois projetos de teste juntos, sem exclusões.

## PRs sugeridos

Os seis entraram na `main` do `oficina-execution-service` pelo PR #2, um commit para cada, mais um de documentação (o PR #1 foi fechado depois de um rebase da branch):

1. **feat: domínio Execucao e regras de etapas**
2. **feat: repositório DynamoDB com transações condicionais e outbox** (já com os testes de integração do repositório, contra o DynamoDB Local)
3. **feat: consumers da saga + publicação de eventos**
4. **feat: API da fila de execução**
5. **infra: tabela DynamoDB, IRSA e ECR**
6. **test: integração com DynamoDB Local e RabbitMQ**

## Pronto quando

- [x] No `docker-compose`, o `execution-service` cria a execução ao receber `SolicitarDiagnostico` e publica os eventos nos endpoints de diagnóstico e finalização.
  - Conferido em 2026-11-16 com o compose do os-service e o `docker-compose.integrado.yml` (os dois no mesmo RabbitMQ): OS aberta → execução na fila → diagnóstico iniciado e concluído → saga em `AguardandoOrcamento`. A finalização foi conferida nos testes de integração.
- [x] Mensagem reentregue não duplica efeito nem evento (teste com RabbitMQ e o mesmo `MessageId` duas vezes).
- [x] A flag de *fault injection* força `ExecucaoRejeitada` (teste com RabbitMQ).
- [x] CI verde com cobertura ≥ 80%.
  - No CI do PR #2: 130 testes (100 unitários e 30 de integração) e os jobs `build-test-quality` e `terraform` verdes. Cobertura de 97,2% de linhas, calculada do relatório publicado pelo próprio CI. O gate de cobertura entra na Etapa 4.
- **Movido para a [Etapa 4](etapa-4-plataforma-cicd.md#pronto-quando) em 2026-11-16, e de lá para a [Etapa 5](etapa-5-observabilidade.md#50-implantação-na-aws) em 2026-11-17:** o Terraform aplica sem erro, e o pod acessa a tabela via IRSA, sem chaves de acesso.
  - Aqui, foi validado só sem conta AWS (`validate` + `terraform test` com provider mockado).
  - Para conferir de verdade, faltam a ServiceAccount anotada e o deploy no EKS (manifestos da 4.3 e CD da 4.5), além do cluster no ar.

**Etapa concluída em 2026-11-16.**
