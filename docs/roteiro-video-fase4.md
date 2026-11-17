# Roteiro do vídeo de demonstração — Fase 4 (≤ 15 min)

Mostra ao vivo os 4 pontos que o enunciado exige:

1. o fluxo completo de uma OS pelos microsserviços;
2. a saga com falha e compensação;
3. o deploy automático de um serviço com validação de testes;
4. o monitoramento e o rastreamento distribuído.

Os blocos seguem a [seção 10 do plano](fase4/README.md#10-roteiro-do-vídeo--15-min). O tempo total é
**~14:30**, com folga para o limite de 15:00. Grave em 1080p, com zoom no terminal e no Postman. O
roteiro da Fase 3 continua em [`roteiro-video.md`](roteiro-video.md).

## Antes de gravar

Depende da [Etapa 6.0](fase4/etapa-6-documentacao-entrega.md#60-implantação-na-aws-e-validação-no-new-relic):
ambiente AWS de pé, deploy feito e New Relic recebendo dados.

- [ ] **Ambiente AWS:** EKS, RDS MySQL e PostgreSQL, DynamoDB, RabbitMQ e API Gateway. Os 3 serviços em `homolog`, com `kubectl get pods -A | grep oficina` tudo `Running`.
- [ ] **Seed:** `python scripts/seed.py --url <api-gateway>`, com `ADMIN_USER`/`ADMIN_PASSWORD` no ambiente. Ele cria a cliente Maria Souza (CPF `529.982.247-25`), usada no login pela Lambda.
- [ ] **Postman:**
  - collection [`postman/oficina-fase4.postman_collection.json`](../postman/oficina-fase4.postman_collection.json) importada;
  - environment `aws` com as URLs do API Gateway;
  - a senha do admin digitada por quem grava, no campo *Current value* (não fica no arquivo).
- [ ] **Mercado Pago (sandbox):**
  - usuários de teste vendedor e comprador;
  - cartões de teste à mão: o nome do titular decide o resultado (`APRO` aprova e `OTHE` recusa, conforme a página de cartões de teste do Mercado Pago);
  - billing com `Pagamentos__Gateway=MercadoPago`.
- [ ] **Prazos curtos:** 2 min no ConfigMap do os-service, só para a gravação (o CD seguinte volta ao padrão). Comandos:

  ```bash
  kubectl -n oficina-os patch configmap os-service-config --type merge -p '{"data":{"Saga__Prazos__Aprovacao":"00:02:00","Saga__Prazos__Pagamento":"00:02:00","Saga__Prazos__Execucao":"00:02:00"}}'
  ```

  ```bash
  kubectl -n oficina-os rollout restart deployment/os-service
  ```

- [ ] **Fault injection:** o comando do bloco 3 pronto no histórico do terminal.
- [ ] **RabbitMQ:** UI por port-forward, logada com o admin do Secrets Manager (`<projeto>-<ambiente>/rabbitmq/admin`) e aberta em *Queues*. Comando:

  ```bash
  kubectl -n messaging port-forward svc/rabbitmq 15672
  ```

- [ ] **New Relic:** abas abertas no dashboard "Oficina Mecânica — Saga de OS (Fase 4)", em Distributed Tracing e em Logs.
- [ ] **Deploy:** um PR com uma alteração trivial pronto num serviço, por exemplo o execution-service, e a aba do GitHub Actions aberta.
- [ ] **Ensaio:** uma vez, cronometrando cada bloco.

---

## Bloco 1 — Arquitetura e o porquê da saga orquestrada (0:00 – 2:00)

- **Tela:**
  - slide com o grupo e os integrantes, por 10 s;
  - [diagrama de componentes](architecture/component-diagram.md) renderizado no GitHub.
- **Narração:**
  - três microsserviços, cada um com o seu banco:
    - os-service com MySQL: cadastros, catálogo, OS e a saga;
    - billing-service com PostgreSQL: orçamento e pagamento no Mercado Pago;
    - execution-service com DynamoDB: fila, diagnóstico e reparo;
  - conversam só por RabbitMQ, e o API Gateway e a Lambda de CPF ficam na frente;
  - **por que orquestração** ([ADR-006](architecture/adrs/ADR-006-saga-orquestrada.md)): o fluxo e as compensações ficam num lugar só, e dá para consultar em que passo cada OS está;
  - outbox e inbox nos 3 serviços garantem que o estado e a mensagem andam juntos.

## Bloco 2 — Fluxo completo de uma OS (2:00 – 6:00)

- **Tela:** Postman com o environment `aws` e, ao lado, a UI do RabbitMQ.
- **Mostrar:**
  1. **Autenticação:**
     - `1. Autenticação → Login da equipe (admin)`;
     - `Login do cliente por CPF (Lambda)`, com 200 e o JWT.
  2. **Abrir OS, Adicionar serviço e Adicionar peça:** na UI do RabbitMQ, a mensagem passa por `execution-solicitar-diagnostico`.
  3. **Diagnóstico:** `Iniciar diagnóstico` e `Concluir diagnóstico` no execution-service. O billing recebe `GerarOrcamento` e gera o orçamento (`Aguardar o orçamento`).
  4. **Aprovação:** `Aprovar o orçamento`, e a resposta traz o pagamento com o `linkPagamento`.
  5. **Pagamento no sandbox:**
     - abrir o link numa janela anônima, logada com o comprador de teste;
     - pagar com o cartão `APRO`;
     - o webhook confirma, e a saga sai de `AguardandoPagamento`.
     - **Não rode** `Simular pagamento aprovado (gateway Fake)`: ele só existe em desenvolvimento.
  6. **Reparo:** `Aguardar a liberação do reparo`, `Iniciar reparo` e `Finalizar`.
  7. **Fim:**
     - `Aguardar a saga Concluida`;
     - `Pagamento aprovado no billing`;
     - `Histórico da OS`, com os status de ponta a ponta.
- **Narração:**
  - cada passo é um comando da saga ou um evento de volta;
  - o status muda nos 3 serviços sem que um leia o banco do outro.

## Bloco 3 — Saga com falha e compensação (6:00 – 9:00)

- **Tela:** Postman, terminal e UI do RabbitMQ.
- **C2, pagamento recusado (1:15):**
  - pasta `4. Cenários de falha → C2`, até `Aguardar a saga em AguardandoPagamento`;
  - pagar o link com o cartão `OTHE`;
  - `C2 · Aguardar a saga Cancelada`: orçamento cancelado, peças devolvidas ao estoque e OS cancelada.
- **C3, execução rejeitada com estorno (1:45):**
  1. Ligar a flag no execution-service (mais o `rollout restart` do mesmo deployment). Comando:

     ```bash
     kubectl -n oficina-execution patch configmap execution-service-config --type merge -p '{"data":{"FaultInjection__RejeitarExecucao":"true"}}'
     ```

  2. Rodar o fluxo do bloco 2 até o pagamento com `APRO`.
  3. O execution responde `ExecucaoRejeitada`, e a saga manda `EstornarPagamento`.
  4. Mostrar o resultado:
     - pagamento `Estornado` no billing (`GET /api/pagamentos/{id}`) e no painel do vendedor de teste;
     - peças devolvidas (`GET /api/pecas`);
     - saga `Cancelada`.
  5. Desligar a flag (`"false"` e `rollout restart`).
- **Se sobrar tempo:** o C1 (orçamento recusado), também na pasta 4.
- **Narração:** cada compensação desfaz só o que já foi feito, na ordem inversa, e a OS termina num estado consistente.

## Bloco 4 — Deploy automático com validação de testes (9:00 – 12:00)

- **Tela:** GitHub do serviço escolhido.
- **Mostrar:**
  1. O PR aberto e o CI rodando, com o job `build-test-quality`:
     - testes unitários e de integração (Testcontainers);
     - gate de cobertura de 80%;
     - quality gate do SonarCloud;
     - OpenAPI.
  2. *Settings → Branches*: a `main` só aceita PR com os checks obrigatórios e 1 aprovação.
  3. **Merge na `main`, depois PR `main → homolog`:**
     - o CD dispara;
     - imagem no ECR;
     - Secret a partir do Secrets Manager;
     - `kubectl apply` do overlay `aws`;
     - rollout e smoke test.
  4. No terminal: `kubectl -n oficina-execution rollout status deployment/execution-service` e `kubectl -n oficina-execution get pods`, com o pod novo.
- **Dica:** o CI leva alguns minutos. Grave esse trecho à parte e acelere (2x), ou corte a espera.

## Bloco 5 — Monitoramento e rastreamento distribuído (12:00 – 14:00)

- **Tela:** New Relic.
- **Mostrar:**
  1. **Distributed Tracing:** o trace da OS do bloco 2, atravessando `os-service → RabbitMQ → execution-service → billing-service`, com o span HTTP do Mercado Pago dentro do consumer.
  2. **Service map:** os 3 serviços e o broker.
  3. **Dashboard "Saga de OS":**
     - sagas iniciadas, concluídas e compensadas;
     - compensações por motivo, com o C2 e o C3 do bloco 3;
     - o funil OS → orçamento → pagamento → finalização.
  4. **Logs:** filtrar por `NumeroOS` e abrir o trace a partir de um log, pelo `trace.id`.
  5. **Alertas, se der tempo:** o de compensações da saga, disparado pelo bloco 3.
- **Narração:** OpenTelemetry nos 3 serviços ([ADR-010](architecture/adrs/ADR-010-opentelemetry.md)), com o contexto do trace nos headers da mensagem. Assim, um trace só cobre a OS inteira.

## Bloco 6 — Fechamento (14:00 – 14:30)

- **Tela:** [`entrega-fase4.md`](entrega-fase4.md) ou o README do os-service.
- **Narração:**
  - recapitular os 4 pontos;
  - citar os 6 repositórios, o usuário `soat-architecture` adicionado e onde está a documentação (arquitetura, ADRs, Postman e evidências de cobertura).

---

## Depois de gravar

- [ ] Voltar os prazos e a flag ao padrão. O próximo CD faz isso, ou rode o `patch` com os valores do `k8s/base/configmap.yaml`.
- [ ] Subir no YouTube ou no Vimeo, como público ou não listado, e testar o link numa janela anônima.
- [ ] Colocar o link em [`entrega-fase4.md`](entrega-fase4.md#3-vídeo-de-demonstração--15-min).
- [ ] Se não houver mais demonstração, rodar `terraform destroy` dos ambientes para não gerar custo.

## Dicas

- Deixe requisições e comandos prontos: o Postman salvo e o histórico do terminal.
- A espera das compensações por prazo (2 min) não cabe no vídeo. Use a recusa (C2) e a flag (C3), que são imediatas.
- Fale o **porquê** das decisões (ADRs 005 a 010), não só o quê.
