# ADR-006 — Saga orquestrada no `os-service`

- **Status:** Proposto (rascunho da Etapa 0; a persistência no MySQL é validada no spike S1)
- **Data:** 2026-11-15

## Contexto

Uma OS atravessa os três serviços: diagnóstico (execution) → orçamento e aprovação (billing) → pagamento (billing + Mercado Pago) → execução (execution).

Cada passo pode falhar ou nunca terminar:

- O cliente recusa o orçamento.
- O pagamento é recusado.
- A execução é rejeitada.
- Um prazo vence.

Quando isso acontece, é preciso desfazer o que já foi feito: devolver peças ao estoque, cancelar o orçamento e estornar o pagamento. Não há transação distribuída entre os bancos, então a consistência vem de uma **saga** com compensações.

## Decisão

**Saga orquestrada**, com o orquestrador dentro do `os-service`:

- **Implementação:** state machine do MassTransit 8 (`OrdemServicoStateMachine`), com estado persistido no MySQL via EF Core e concorrência otimista.
- **Transação única:** estado da saga, status da OS e mensagens de saída (outbox) são gravados na mesma transação do `AppDbContext`.
- **Comunicação com os outros serviços:** a saga envia **comandos** (`GerarOrcamento`, `IniciarExecucao`, `EstornarPagamento`...) e reage a **eventos**. Os outros serviços não conhecem a saga, só os seus comandos.
- **Compensações:** cada falha tem uma compensação definida (C1, C2 e C3 em [contratos de mensagens §4](../../contratos-mensagens.md#4-máquina-de-estados-da-saga-no-os-service)). Todas são idempotentes.
- **Timeouts:**
  - Ao entrar num estado de espera, a saga grava `PrazoExpiraEm`.
  - Um `BackgroundService` publica `PrazoExpirado` para as sagas vencidas.
  - Não depende de plugin de *delayed message* no RabbitMQ.
- **Consulta:** a instância da saga não é apagada ao final. Fica disponível em `GET /api/ordens-servico/{id}/saga`.

**Por que orquestração:**

- O fluxo tem cinco etapas, ramificações (recusa, expiração) e compensações encadeadas.
  - Com um orquestrador, a sequência fica num único lugar.
  - Ela pode ser testada com o *test harness* do MassTransit e com BDD.
- O estado de cada OS fica explícito e consultável, o que ajuda no suporte, na observabilidade e na demonstração.
- O `os-service` já é dono do ciclo de vida da OS. Colocar o orquestrador ali evita um quarto serviço só para orquestrar.

## Consequências

- **Positivas:**
  - Fluxo legível e testável num lugar só.
  - Compensações rastreáveis pelo estado da saga e pelo histórico da OS.
  - Os outros serviços ficam simples: recebem um comando e respondem com um evento.
- **Negativas / mitigação:**
  - **O `os-service` conhece a sequência do processo:** contratos versionados e comandos explícitos limitam o acoplamento ao formato das mensagens.
  - **O orquestrador é ponto central:** se o `os-service` cair, as mensagens ficam na fila e são processadas quando ele voltar (outbox + filas duráveis). Nada se perde.
  - **Eventos fora de ordem ou duplicados:** `Ignore` explícito por estado + inbox do MassTransit.

## Alternativas descartadas

- **Coreografia** (cada serviço reage aos eventos dos outros): a lógica de compensação ficaria espalhada em três repositórios. Seria difícil responder "em que passo esta OS parou?" e testar o fluxo ponta a ponta.
- **Orquestrador num serviço separado:** mais um deploy e mais um banco sem responsabilidade de negócio própria.
- **AWS Step Functions:** prende o fluxo à AWS, não roda no docker-compose e complica os testes BDD locais.
