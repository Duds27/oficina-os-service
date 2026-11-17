# Etapa 3 — `billing-service` + Mercado Pago

> [← Plano geral](README.md) · **Semanas:** 2–4 · **Frente:** B · **Depende de:** [Etapa 0](etapa-0-fundacao.md) (esqueleto, contratos, spike S3)
> **Repositório:** `oficina-billing-service` · **Banco:** PostgreSQL 16 (RDS)

## Objetivo

Gerar o orçamento e enviá-lo ao cliente, registrar a aprovação ou recusa, cobrar pelo Mercado Pago e estornar quando a saga mandar.

**Ordem interna:** primeiro o fluxo completo com o gateway **Fake** (semanas 2–3, para o marco da semana 3). Depois a integração real com o sandbox do Mercado Pago (semana 4).

## Tarefas

### 3.1 Domínio

- [x] Agregado `Orcamento`:
  - Dados: `Id`, `CorrelationId`, `OsId`, `NumeroOS`, `Cliente {Id, Nome, Email}` (snapshot), `Itens[]` (snapshot com preço), `ValorTotal`, `Status`, `ValidoAte`, datas.
  - Status: `Gerado → Enviado → Aprovado | Recusado | Cancelado | Expirado`.
  - Total recalculado a partir dos itens. Não confiar no valor recebido sem conferir.
- [x] Agregado `Pagamento`:
  - Dados: `Id`, `OrcamentoId`, `Valor`, `Status`, `PreferenceId`, `LinkPagamento`, `MpPaymentId`, `RefundId`, datas.
  - Status: `Pendente → Aprovado | Recusado | Estornado`.
  - **Na implementação:** entrou o status `Cancelado`, para o pagamento pendente de um orçamento cancelado (regra 2 da seção 5 dos contratos).
- [x] Regras:
  - Só aprova ou recusa orçamento `Enviado` e dentro da validade. Vencido vira `Expirado`.
  - Estorno só de pagamento `Aprovado`. Pedido repetido não estorna duas vezes.
  - **Regras 1, 2 e 3 da seção 5:** pagamento aprovado depois do cancelamento do orçamento, ou depois de uma recusa, é estornado sozinho.
  - **Regra 5:** `CancelarOrcamento` antes do `GerarOrcamento` registra o orçamento já cancelado.

### 3.2 Persistência (PostgreSQL)

- [x] EF Core + `Npgsql.EntityFrameworkCore.PostgreSQL`, com migrations versionadas e aplicadas na inicialização (com retry, mesmo padrão do `os-service`).
- [x] Tabelas do outbox/inbox do MassTransit 8.4.1, a mesma versão do `os-service` (`AddEntityFrameworkOutbox<BillingDbContext>(o => o.UsePostgres())`).
- [x] Tabela `NotificacoesOutbox` para o e-mail simulado do orçamento (mesmo padrão da Fase 2).
- [x] Índices: `OsId`, `CorrelationId` (único por orçamento ativo), `MpPaymentId` (único).
  - **Na implementação:** `CorrelationId` é único sem filtro (uma saga, um orçamento). Assim o pedido atrasado encontra o cancelamento antecipado. Concorrência otimista pela coluna `xmin`.

### 3.3 Mensageria

| Consumer | Efeito | Evento publicado |
| --- | --- | --- |
| `GerarOrcamento` | Cria o orçamento, registra a notificação ao cliente e marca como `Enviado` | `OrcamentoGerado` |
| `CancelarOrcamento` | `Cancelado` (idempotente) | — |
| `EstornarPagamento` | Refund no gateway, `Pagamento → Estornado` e orçamento `Cancelado` | `PagamentoEstornado` |
| `ProcessarNotificacaoPagamento` (interno) | Consulta o pagamento no gateway e atualiza o status | `PagamentoConfirmado` ou `PagamentoRecusado` |

- [x] Falha definitiva no estorno (retries esgotados) → mensagem em `_error` + **alerta crítico**, que exige intervenção manual. Documentar no README.
  - Um consumidor de `Fault<EstornarPagamento>` loga em nível crítico, com a OS, o pagamento, o CorrelationId e o que fazer. Vira alerta no New Relic na Etapa 5.
  - Estorno repetido não estorna de novo, mas responde `PagamentoEstornado` de novo, porque a saga espera essa resposta em `AguardandoEstorno`.

### 3.4 API

| Método | Rota | Auth | Descrição |
| --- | --- | --- | --- |
| GET | `/api/orcamentos/{id}` | JWT | Orçamento com itens e status |
| GET | `/api/orcamentos?osId=` | JWT | Orçamentos de uma OS |
| POST | `/api/orcamentos/{id}/aprovacao` | JWT do cliente | `{aprovado, motivo?}`. Aprovando, cria o pagamento e devolve o link |
| GET | `/api/pagamentos/{id}` | JWT | Status do pagamento |
| POST | `/webhooks/mercadopago` | assinatura `x-signature` | Notificação do Mercado Pago |
| POST | `/dev/pagamentos/{id}/simular?resultado=aprovado\|recusado` | só em `Development` | Simula o webhook com o gateway Fake |

- [x] Na aprovação, **conferir que o `clienteId` do token (login por CPF) é o dono do orçamento**. Senão, 403.
  - **Decidido na implementação:** o atendente (`Admin`) também registra a decisão do cliente, o que permite a demonstração local sem a Lambda. O mecânico só consulta. As consultas seguem a mesma regra: o cliente só vê os orçamentos e pagamentos dele.
- [x] Fluxo da aprovação:
  1. Orçamento → `Aprovado` e criação de `Pagamento` `Pendente`.
  2. Chamada ao gateway para criar a preferência, com `X-Idempotency-Key = pagamentoId`.
  3. Salvar `PreferenceId`/`LinkPagamento` e publicar `OrcamentoAprovado` pelo outbox.
  - **Na implementação:** os três passos são uma gravação só. Se o gateway falhar, nada é gravado e a API responde 503.
- [x] Na recusa: orçamento → `Recusado` e publicar `OrcamentoRecusado`.

### 3.5 Gateway de pagamento

- [x] Port `IGatewayPagamento`: `CriarCobranca`, `ConsultarPagamento`, `Estornar`.
- [x] `FakeGatewayPagamento`: link fictício e resultado controlado pelo endpoint `/dev/...`. Usado em testes, CI, compose e BDD.
- [x] `MercadoPagoGateway` (sandbox):
  - `HttpClient` tipado com resiliência (`Microsoft.Extensions.Http.Resilience`: timeout, retry e circuit breaker).
  - `POST /checkout/preferences` com itens, `external_reference = pagamentoId` e `notification_url` (rota pública do API Gateway).
    - **Na implementação:** um item só, com o total, porque o Checkout Pro exige quantidade inteira e o orçamento tem quantidades decimais.
    - **Bug encontrado nos testes:** quando o resilience handler desiste (tempo total esgotado ou circuit breaker aberto), o Polly lança `ExecutionRejectedException`. O adapter trata essa exceção como gateway indisponível; sem isso, a aprovação respondia 500.
  - `GET /v1/payments/{id}`: fonte da verdade do status (`approved` → confirmado; `rejected`/`cancelled` → recusado; `pending`/`in_process` → aguarda).
  - `POST /v1/payments/{id}/refunds` com `X-Idempotency-Key`.
- [x] Webhook:
  1. Validar `x-signature`: HMAC-SHA256 com o segredo do webhook, sobre `id:{data.id};request-id:{x-request-id};ts:{ts};`.
  2. Rejeitar assinatura inválida com 401.
  3. Responder 200 rápido e publicar `ProcessarNotificacaoPagamento` (outbox) para processamento assíncrono.
- [x] Credenciais: access token de **teste** e segredo do webhook no Secrets Manager. Nunca no repositório nem no state do Terraform (3.7).
- [x] Seleção do adapter por configuração: `Pagamentos__Gateway = Fake | MercadoPago`.

### 3.6 Notificação do orçamento

- [x] Ao gerar o orçamento, gravar em `NotificacoesOutbox` um e-mail com o resumo e o link de aprovação, e logar o envio (simulado, como na Fase 2).

### 3.7 Infraestrutura própria (`/infra`, Terraform)

- [x] `aws_db_instance` PostgreSQL 16 (`db.t4g.micro`, storage gp3), sem acesso público.
  - Subnet group nas subnets privadas e security group que só aceita os nós do EKS.
  - Esses dados vêm do `terraform_remote_state` do `oficina-infra-k8s`.
  - `free_tier` (padrão `true`), como no `infra-database`: gp2 e sem retenção de backup. Com `false`, gp3 e 7 dias.
- [x] Senha mestre gerenciada (`manage_master_user_password = true`) ou `random_password` → secret `billing/db` no Secrets Manager (`host`, `port`, `username`, `password`, `dbname`), no mesmo formato que o CD já lê.
  - Feito com `random_password`. O nome tem o ambiente: `<projeto>-<ambiente>/billing/db`.
- [x] Secret `billing/mercadopago` criado vazio pelo Terraform. O valor (token de teste + segredo do webhook) é preenchido manualmente pelo console ou CLI.
- [x] Repositório ECR do serviço.
- [x] Estado remoto no S3 + trava DynamoDB.
- [x] `terraform test` com provider mockado (5 casos) e job `terraform` no CI.

### 3.8 Testes

- [x] **Unitários:** regras de `Orcamento` e `Pagamento`, cálculo do total, validação de assinatura (válida, inválida, `ts` adulterado).
- [x] **Consumers** com `ITestHarness`: geração, cancelamento e estorno (inclusive estorno repetido).
- [x] **Integração:** Testcontainers PostgreSQL + RabbitMQ + **WireMock.Net** simulando a API do Mercado Pago, com os payloads capturados no spike S3. Cenários:
  - Pagamento aprovado.
  - Pagamento recusado.
  - Webhook duplicado.
  - Falha temporária da API do MP (retry).
  - Estorno.
  - Os payloads seguem a documentação do Mercado Pago. Os reais do sandbox entram com o spike S3, que foi para a [Etapa 5](etapa-5-observabilidade.md#50-implantação-na-aws).
  - Também ponta a ponta com RabbitMQ 4.1 e um bus no papel do os-service, do pedido de orçamento ao estorno.
- [x] **Contrato:** snapshot das mensagens (as 9 do catálogo e o evento interno), com o serializador do MassTransit.
- [x] Cobertura ≥ 80%: cerca de 96% de linhas. O `coverlet.runsettings` exclui só as migrations, que são código gerado.

## PRs sugeridos

Os seis entraram na `main` do `oficina-billing-service` pelo PR #2, um commit para cada, mais um de documentação (o PR #1 foi fechado e substituído pelo #2):

1. **feat: domínio Orcamento/Pagamento + persistência PostgreSQL**
2. **feat: consumers da saga + outbox (gateway Fake)**: libera o marco da semana 3
3. **feat: API de orçamento e aprovação pelo cliente**
4. **feat: integração Mercado Pago (preferência, webhook, consulta, estorno)**
5. **infra: RDS PostgreSQL, secrets e ECR**
6. **test: integração com WireMock do Mercado Pago**

## Pronto quando

- [x] No compose, com o gateway Fake: `GerarOrcamento` → aprovação → simulação de pagamento → `PagamentoConfirmado` chega ao `os-service`.
  - Conferido em 2026-11-16 com os três serviços no mesmo RabbitMQ (`docker-compose.integrado.yml` no execution e no billing): OS aberta → diagnóstico → orçamento → aprovação → pagamento simulado → execução → saga `Concluida`.
  - Na mesma rodada, uma OS com o prazo de aprovação vencido levou a saga e o orçamento a `Cancelado` (C2).
- [x] Um cliente não aprova orçamento de outro (testes de unidade e da API: 403).
- [x] CI verde com cobertura ≥ 80%.
  - No CI do PR #2 e da `main`: 182 testes (149 unitários e 33 de integração) e os jobs `build-test-quality` e `terraform` verdes. Cobertura de 95,7% de linhas, calculada do relatório publicado pelo próprio CI. O gate de cobertura entra na Etapa 4.
- **Movido para a [Etapa 4](etapa-4-plataforma-cicd.md#49-mercado-pago-no-sandbox) em 2026-11-16, e de lá para a [Etapa 5](etapa-5-observabilidade.md#50-implantação-na-aws) em 2026-11-17:** no sandbox, preferência criada, pagamento com cartão de teste, webhook recebido e validado, `PagamentoConfirmado` publicado e estorno realizado.
  - Depende do spike S3: conta de teste do Mercado Pago (vendedor e comprador), token de teste e segredo do webhook, e uma URL pública para o webhook.
  - A Etapa 4 cria a rota pública `POST /webhooks/mercadopago` no API Gateway, então o teste roda no ambiente de verdade, sem túnel.
  - O mesmo fluxo já passa contra o Mercado Pago simulado no WireMock.

**Etapa concluída em 2026-11-16.**
