# Etapa 3 — `billing-service` + Mercado Pago

> [← Plano geral](README.md) · **Semanas:** 2–4 · **Frente:** B · **Depende de:** [Etapa 0](etapa-0-fundacao.md) (esqueleto, contratos, spike S3)
> **Repositório:** `oficina-billing-service` · **Banco:** PostgreSQL 16 (RDS)

## Objetivo

Gerar o orçamento e enviá-lo ao cliente, registrar a aprovação ou recusa, cobrar pelo Mercado Pago e estornar quando a saga mandar.

**Ordem interna:** primeiro o fluxo completo com o gateway **Fake** (semanas 2–3, para o marco da semana 3). Depois a integração real com o sandbox do Mercado Pago (semana 4).

## Tarefas

### 3.1 Domínio

- [ ] Agregado `Orcamento`:
  - Dados: `Id`, `CorrelationId`, `OsId`, `NumeroOS`, `Cliente {Id, Nome, Email}` (snapshot), `Itens[]` (snapshot com preço), `ValorTotal`, `Status`, `ValidoAte`, datas.
  - Status: `Gerado → Enviado → Aprovado | Recusado | Cancelado | Expirado`.
  - Total recalculado a partir dos itens. Não confiar no valor recebido sem conferir.
- [ ] Agregado `Pagamento`:
  - Dados: `Id`, `OrcamentoId`, `Valor`, `Status`, `PreferenceId`, `LinkPagamento`, `MpPaymentId`, `RefundId`, datas.
  - Status: `Pendente → Aprovado | Recusado | Estornado`.
- [ ] Regras:
  - Só aprova ou recusa orçamento `Enviado` e dentro da validade.
  - Estorno só de pagamento `Aprovado`. Pedido repetido não estorna duas vezes.

### 3.2 Persistência (PostgreSQL)

- [ ] EF Core + `Npgsql.EntityFrameworkCore.PostgreSQL`, com migrations versionadas e aplicadas na inicialização (com retry, mesmo padrão do `os-service`).
- [ ] Tabelas do outbox/inbox do MassTransit 8.4.1, a mesma versão do `os-service` (`AddEntityFrameworkOutbox<BillingDbContext>(o => o.UsePostgres())`).
- [ ] Tabela `NotificacoesOutbox` para o e-mail simulado do orçamento (mesmo padrão da Fase 2).
- [ ] Índices: `OsId`, `CorrelationId` (único por orçamento ativo), `MpPaymentId` (único).

### 3.3 Mensageria

| Consumer | Efeito | Evento publicado |
| --- | --- | --- |
| `GerarOrcamento` | Cria o orçamento, registra a notificação ao cliente e marca como `Enviado` | `OrcamentoGerado` |
| `CancelarOrcamento` | `Cancelado` (idempotente) | — |
| `EstornarPagamento` | Refund no gateway, `Pagamento → Estornado` e orçamento `Cancelado` | `PagamentoEstornado` |
| `ProcessarNotificacaoPagamento` (interno) | Consulta o pagamento no gateway e atualiza o status | `PagamentoConfirmado` ou `PagamentoRecusado` |

- [ ] Falha definitiva no estorno (retries esgotados) → mensagem em `_error` + **alerta crítico**, que exige intervenção manual. Documentar no README.

### 3.4 API

| Método | Rota | Auth | Descrição |
| --- | --- | --- | --- |
| GET | `/api/orcamentos/{id}` | JWT | Orçamento com itens e status |
| GET | `/api/orcamentos?osId=` | JWT | Orçamentos de uma OS |
| POST | `/api/orcamentos/{id}/aprovacao` | JWT do cliente | `{aprovado, motivo?}`. Aprovando, cria o pagamento e devolve o link |
| GET | `/api/pagamentos/{id}` | JWT | Status do pagamento |
| POST | `/webhooks/mercadopago` | assinatura `x-signature` | Notificação do Mercado Pago |
| POST | `/dev/pagamentos/{id}/simular?resultado=aprovado\|recusado` | só em `Development` | Simula o webhook com o gateway Fake |

- [ ] Na aprovação, **conferir que o `clienteId` do token (login por CPF) é o dono do orçamento**. Senão, 403.
- [ ] Fluxo da aprovação:
  1. Orçamento → `Aprovado` e criação de `Pagamento` `Pendente`.
  2. Chamada ao gateway para criar a preferência, com `X-Idempotency-Key = pagamentoId`.
  3. Salvar `PreferenceId`/`LinkPagamento` e publicar `OrcamentoAprovado` pelo outbox.
- [ ] Na recusa: orçamento → `Recusado` e publicar `OrcamentoRecusado`.

### 3.5 Gateway de pagamento

- [ ] Port `IGatewayPagamento`: `CriarCobranca`, `ConsultarPagamento`, `Estornar`.
- [ ] `FakeGatewayPagamento`: link fictício e resultado controlado pelo endpoint `/dev/...`. Usado em testes, CI, compose e BDD.
- [ ] `MercadoPagoGateway` (sandbox):
  - `HttpClient` tipado com resiliência (`Microsoft.Extensions.Http.Resilience`: timeout, retry e circuit breaker).
  - `POST /checkout/preferences` com itens, `external_reference = pagamentoId` e `notification_url` (rota pública do API Gateway).
  - `GET /v1/payments/{id}`: fonte da verdade do status (`approved` → confirmado; `rejected`/`cancelled` → recusado; `pending`/`in_process` → aguarda).
  - `POST /v1/payments/{id}/refunds` com `X-Idempotency-Key`.
- [ ] Webhook:
  1. Validar `x-signature`: HMAC-SHA256 com o segredo do webhook, sobre `id:{data.id};request-id:{x-request-id};ts:{ts};`.
  2. Rejeitar assinatura inválida com 401.
  3. Responder 200 rápido e publicar `ProcessarNotificacaoPagamento` (outbox) para processamento assíncrono.
- [ ] Credenciais: access token de **teste** e segredo do webhook no Secrets Manager. Nunca no repositório nem no state do Terraform (3.7).
- [ ] Seleção do adapter por configuração: `Pagamentos__Gateway = Fake | MercadoPago`.

### 3.6 Notificação do orçamento

- [ ] Ao gerar o orçamento, gravar em `NotificacoesOutbox` um e-mail com o resumo e o link de aprovação, e logar o envio (simulado, como na Fase 2).

### 3.7 Infraestrutura própria (`/infra`, Terraform)

- [ ] `aws_db_instance` PostgreSQL 16 (`db.t4g.micro`, storage gp3), sem acesso público.
  - Subnet group nas subnets privadas e security group que só aceita os nós do EKS.
  - Esses dados vêm do `terraform_remote_state` do `oficina-infra-k8s`.
- [ ] Senha mestre gerenciada (`manage_master_user_password = true`) ou `random_password` → secret `billing/db` no Secrets Manager (`host`, `port`, `username`, `password`, `dbname`), no mesmo formato que o CD já lê.
- [ ] Secret `billing/mercadopago` criado vazio pelo Terraform. O valor (token de teste + segredo do webhook) é preenchido manualmente pelo console ou CLI.
- [ ] Repositório ECR do serviço.
- [ ] Estado remoto no S3 + trava DynamoDB.

### 3.8 Testes

- [ ] **Unitários:** regras de `Orcamento` e `Pagamento`, cálculo do total, validação de assinatura (válida, inválida, `ts` adulterado).
- [ ] **Consumers** com `ITestHarness`: geração, cancelamento e estorno (inclusive estorno repetido).
- [ ] **Integração:** Testcontainers PostgreSQL + RabbitMQ + **WireMock.Net** simulando a API do Mercado Pago, com os payloads capturados no spike S3. Cenários:
  - Pagamento aprovado.
  - Pagamento recusado.
  - Webhook duplicado.
  - Falha temporária da API do MP (retry).
  - Estorno.
- [ ] **Contrato:** snapshot das mensagens.
- [ ] Cobertura ≥ 80%.

## PRs sugeridos

1. **feat: domínio Orcamento/Pagamento + persistência PostgreSQL**
2. **feat: consumers da saga + outbox (gateway Fake)**: libera o marco da semana 3
3. **feat: API de orçamento e aprovação pelo cliente**
4. **feat: integração Mercado Pago (preferência, webhook, consulta, estorno)**
5. **infra: RDS PostgreSQL, secrets e ECR**
6. **test: integração com WireMock do Mercado Pago**

## Pronto quando

- [ ] No compose, com o gateway Fake: `GerarOrcamento` → aprovação → simulação de pagamento → `PagamentoConfirmado` chega ao `os-service`.
- [ ] No sandbox: preferência criada, pagamento com cartão de teste, webhook recebido e validado, `PagamentoConfirmado` publicado e estorno realizado.
- [ ] Um cliente não aprova orçamento de outro.
- [ ] CI verde com cobertura ≥ 80%.
