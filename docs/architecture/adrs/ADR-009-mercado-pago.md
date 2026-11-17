# ADR-009 — Pagamento com Mercado Pago (Checkout Pro)

- **Status:** Proposto: implementado e testado contra o Mercado Pago simulado (WireMock). O spike S3, no sandbox, depende da rota pública do webhook e está na [Etapa 6](../../fase4/etapa-6-documentacao-entrega.md#60-implantação-na-aws-e-validação-no-new-relic). Passa a *Aceito* depois dele e da revisão do grupo
- **Data:** 2026-11-15

## Contexto

O `billing-service` precisa cobrar o orçamento aprovado e estornar quando a saga mandar ([ADR-006](ADR-006-saga-orquestrada.md)).

O pagamento real (em sandbox) também é o que dá sentido à compensação mais importante da saga: execução rejeitada → estorno.

Requisitos:

- Ambiente de teste gratuito.
- Notificação assíncrona do resultado.
- API de estorno.
- Nenhum dado de cartão passando pelos nossos serviços.

## Decisão

- **Mercado Pago, Checkout Pro**, no ambiente de teste, com usuários vendedor e comprador de teste e cartões de teste.
- **Momento do pagamento:** depois da aprovação do orçamento e antes da execução.
- **Fluxo:**
  1. O cliente aprova o orçamento.
  2. O billing cria o `Pagamento` e uma preferência (`POST /checkout/preferences`), com:
     - `external_reference = pagamentoId`;
     - `notification_url` = rota pública do webhook;
     - `expiration_date_to` = prazo de pagamento da saga.
  3. O link de pagamento vai no evento `OrcamentoAprovado`.
  4. O cliente paga na página do Mercado Pago.
  5. O webhook `payment` chega em `POST /webhooks/mercadopago`. O billing **consulta** `GET /v1/payments/{id}`, que é a fonte da verdade, e publica `PagamentoConfirmado` ou `PagamentoRecusado`.
  6. Na compensação: `POST /v1/payments/{id}/refunds` → `PagamentoEstornado`.
- **Segurança:**
  - Valida a assinatura `x-signature` do webhook (HMAC-SHA256 sobre `id:{data.id};request-id:{x-request-id};ts:{ts};`) e rejeita com 401 se não bater.
  - `X-Idempotency-Key` nas chamadas que criam ou estornam.
  - Access token de teste e segredo do webhook no Secrets Manager. Nunca no repositório nem no state do Terraform.
  - A rota do webhook não tem authorizer JWT, mas tem throttling no API Gateway.
- **Arquitetura (port & adapter):** `IGatewayPagamento` com dois adapters:
  - `MercadoPagoGateway`: `HttpClient` com timeout, retry e circuit breaker.
  - `FakeGatewayPagamento`: usado em testes, CI, docker-compose e BDD. O resultado é controlado por `/dev/pagamentos/{id}/simular`.
- **Regras de corrida** (pagamento depois do cancelamento, nova tentativa no Checkout Pro): ver [contratos de mensagens §5](../../contratos-mensagens.md#5-corridas-conhecidas-e-regras-de-cada-lado).

## Consequências

- **Positivas:**
  - Nenhum dado de cartão nos nossos serviços (o checkout é hospedado pelo Mercado Pago).
  - Webhook e estorno reais, demonstráveis no vídeo.
  - O gateway Fake deixa o fluxo completo testável sem internet e sem credenciais.
- **Negativas / mitigação:**
  - **O webhook precisa de URL pública:** rota pública no API Gateway. Localmente, um túnel (ex.: cloudflared) ou o endpoint de simulação.
  - **Dependência de um serviço externo na demonstração:** spike S3 logo na semana 1, com os payloads salvos para os testes com WireMock.
  - **Nova tentativa no Checkout Pro depois de uma recusa:** a regra v1 encerra na primeira recusa e estorna automaticamente um pagamento tardio. A alternativa está registrada para o grupo decidir.

## Alternativas descartadas

- **Checkout Transparente / API de pagamentos direta:** exigiria tokenizar o cartão num front-end nosso, sem ganho para o objetivo da fase.
- **Pagamento só por Pix:** o estorno de Pix tem fluxo e prazos próprios e é mais difícil de simular em sandbox.
- **Pagar na entrega:** simplifica a saga, mas tira a transação financeira do meio do fluxo e, com ela, a compensação por estorno.

## Resultado da implementação (Fase 4)

- **Gateway.** `MercadoPagoGateway` no billing-service: preferência com um item no total do orçamento, consulta do pagamento no webhook e estorno com chave de idempotência (o id do pagamento). Retry, timeout e circuit breaker vêm do standard resilience handler.
- **Webhook.** `POST /webhooks/mercadopago`, público no API Gateway com throttling. A assinatura `x-signature` (HMAC-SHA256 do manifesto `id;request-id;ts`) é validada no serviço, e a notificação vai para o outbox antes de ser processada.
- **Gateway Fake.** É o padrão. Usado em testes, compose e kind, com a rota `/dev` para simular o pagamento.
- **Regras de corrida da seção 5 dos contratos.** Pagamento aprovado depois do cancelamento ou da recusa é estornado sozinho (`pagamento.resultado{status=tardio}`).
- **Pendente (Etapa 6, 6.0):** os 4 passos no sandbox e os payloads reais nos testes com WireMock.
