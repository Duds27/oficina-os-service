# Etapa 6 — Documentação, evidências e entrega

> [← Plano geral](README.md) · **Semana:** 6 · **Frente:** todas · **Depende de:** [Etapas 1–5](README.md#7-etapas)
> **Repositórios:** todos

## Objetivo

Fechar os entregáveis do enunciado: repositórios documentados com evidências, Swagger/Postman atualizados, vídeo de até 15 minutos e o PDF do portal.

## Tarefas

### 6.0 Implantação na AWS e validação no New Relic

Veio da [Etapa 5](etapa-5-observabilidade.md#pronto-quando) em 2026-11-17 (e, antes, das Etapas 2, 3 e 4). O código, os testes, os pipelines e a observabilidade estão prontos e validados fora da AWS. Aqui ficam os passos que dependem da conta AWS, das configurações do GitHub, do New Relic e do Mercado Pago. Eles também são pré-requisito do vídeo (6.4).

**Implantação** (o que era a 5.0):

- [ ] **Credenciais.** As da AWS na máquina de desenvolvimento estavam inválidas em 2026-11-17 (`InvalidClientTokenId`). Os applies podem rodar pelos workflows (OIDC) ou com credenciais renovadas.
- [ ] **Capacidade dos nós.** Decidir o tipo de instância antes do apply do `infra-k8s`. Com `t3.micro` (cerca de 400 MiB alocáveis), o pod do RabbitMQ (512 MiB) não cabe. No Free Plan, `t3.small` também é elegível. A conta está no [README do infra-k8s](https://github.com/Duds27/oficina-infra-k8s#capacidade-dos-nós-decidir-antes-do-apply).
- [ ] **Role do deploy** (`AWS_DEPLOY_ROLE_ARN`, cadastrada em cada repo). Os secrets e os Environments da Fase 3 não existem mais nos repositórios. A role precisa de:
  - push nos ECRs novos;
  - acesso ao cluster (o criador vira admin pelo `enable_cluster_creator_admin_permissions`);
  - leitura dos secrets novos (`rabbitmq/*`, `billing/*` e a chave interna).
- [ ] **Applies, nesta ordem:**
  1. `oficina-infra-k8s` (homolog);
  2. o `infra/` do billing e do execution, pelo `infra.yml` (variables `AWS_REGION`, `TF_STATE_BUCKET` e `TF_LOCK_TABLE` no repositório);
  3. `oficina-lambda-auth`, pelo CD no push em `homolog`.
- [ ] **Secret da chave interna** (`{"key": "..."}`). Cadastrar o `INTERNAL_KEY_SECRET_ID` na Lambda e no os-service.
- [ ] **GitHub:**
  - criar as branches `homolog` e `prod`, que só recebem merge da `main` por PR;
  - criar os Environments `homolog`, `prod` (com aprovação) e `infra` (com *required reviewers*; hoje ele existe sem regra, e o apply rodaria sem aprovação);
  - cadastrar as variables de cada repo (README, seção CD).
- [ ] **Deploy.** Push em `homolog` em cada serviço, e o CD faz o deploy. Depois, apagar o namespace `oficina` da Fase 3.
- [ ] **Sandbox do Mercado Pago** (era a 4.9; spike S3):
  - contas de teste (vendedor e comprador);
  - token de teste e segredo do webhook no secret `billing/mercadopago`, preenchidos à mão;
  - `Pagamentos__Gateway=MercadoPago` no overlay `aws` do billing (a `NotificationUrl` já sai da URL do API Gateway);
  - os 4 passos do spike;
  - payloads reais nos testes com WireMock;
  - resultado registrado no [ADR-009](../architecture/adrs/ADR-009-mercado-pago.md).

**Validação no New Relic** (o que era a 5.7):

- [ ] Aplicar o dashboard e os alertas: `docs/observability/terraform`, com a User key em `TF_VAR_newrelic_api_key` (instruções no [README da observabilidade](../observability/README.md#aplicar)). A license key já está nos secrets `NEW_RELIC_LICENSE_KEY` dos 5 repositórios.
- [ ] Rodar o caminho feliz e o cenário de estorno no EKS.
- [ ] Encontrar o trace pelo `NumeroOS` e conferir o service map com os 3 serviços + RabbitMQ.
- [ ] Na chamada ao Mercado Pago (sandbox), conferir que o span HTTP de saída fica dentro do trace do consumer.
- [ ] Capturas para a documentação e o vídeo: trace, service map, dashboard e um alerta disparado.

### 6.1 README de cada microsserviço (mesmo modelo nos 3)

- [ ] Propósito e responsabilidades do serviço, e o que **não** é dele.
- [ ] Diagrama do serviço: camadas, banco, filas que consome e publica.
- [ ] Tabela de **mensagens publicadas e consumidas** (link para `docs/contratos-mensagens.md`).
- [ ] Endpoints + link do Swagger.
- [ ] Como rodar local (compose) e como rodar os testes.
- [ ] **Evidência de cobertura:** badges do SonarCloud + print do relatório (`docs/evidencias/`).
- [ ] Pipeline CI/CD (diagrama do fluxo) e como fazer deploy.
- [ ] Seção **"Saga: estratégia e justificativa"**, resumo do ADR-006 com link. O enunciado exige isso no README.

### 6.2 Documentação de arquitetura (`docs/architecture` no `os-service`)

- [ ] Diagrama de componentes atualizado: 3 serviços, bancos, RabbitMQ, API Gateway, Lambda e Mercado Pago.
- [ ] Diagramas de sequência: caminho feliz + compensações (recusa, pagamento recusado, estorno).
- [ ] Modelo de dados por serviço:
  - ER do MySQL (os) e do PostgreSQL (billing).
  - Padrões de acesso do DynamoDB (execution).
- [ ] ADRs 005–009 finalizados. ADR-003 marcado como "parcialmente substituído pelo ADR-005".
- [ ] Atualizar o `README.md` raiz do `os-service` com a seção "Fase 4" (mesmo formato das fases anteriores) e link para este plano.

### 6.3 Swagger e Postman

- [ ] Swagger em cada serviço. Exportar o `openapi.json` no CI (Swashbuckle CLI) para `docs/openapi.json`, para ficar consultável sem subir a aplicação.
- [ ] Collection `postman/oficina-fase4.postman_collection.json` no `os-service`:
  - Pastas: Autenticação → Cadastros → Fluxo completo da OS → Cenários de falha.
  - Scripts que encadeiam variáveis (`token`, `osId`, `orcamentoId`, `pagamentoId`).
  - Environments `local` e `aws`.
- [ ] Rodar a collection com Newman contra o compose para garantir que está atualizada.

### 6.4 Vídeo (≤ 15 min, YouTube/Vimeo público ou não listado)

- [ ] Ensaiar com o roteiro da [seção 10 do plano](README.md#10-roteiro-do-vídeo--15-min) e cronometrar cada bloco.
- [ ] Checklist antes de gravar:
  - [ ] Ambiente AWS de pé: EKS, RDS, RabbitMQ, API Gateway.
  - [ ] Seed aplicado.
  - [ ] Usuários de teste do Mercado Pago (vendedor e comprador) e cartões de teste à mão.
  - [ ] Prazos da saga curtos (2 min) via ConfigMap.
  - [ ] Flag de *fault injection* pronta para ligar.
  - [ ] Dashboard do New Relic aberto e filtrado.
  - [ ] UI do RabbitMQ por port-forward.
  - [ ] PR preparado para demonstrar o deploy automático.
- [ ] Os 4 pontos obrigatórios aparecem explicitamente:
  1. Fluxo completo da OS pelos microsserviços.
  2. Saga com falha e compensação.
  3. Deploy automático de um serviço com validação de testes.
  4. Monitoramento e rastreamento distribuído.
- [ ] Upload e teste do link numa janela anônima.

### 6.5 PDF de entrega (portal)

- [ ] Montar `docs/entrega-fase4.md` e exportar para PDF, com:
  - Nome e identificação (RM) dos participantes.
  - Links de **todos** os repositórios: 3 serviços + `oficina-lambda-auth`, `oficina-infra-k8s` e `oficina-infra-database`.
  - Link do vídeo.
  - Diagrama geral da arquitetura (serviços, bancos e comunicação).
  - Estratégia da saga (orquestração) e justificativa.
  - Justificativa da divisão dos microsserviços e das tecnologias escolhidas.

### 6.6 Verificação final

- [ ] Usuário `soat-architecture` adicionado como colaborador em **todos** os repositórios.
- [ ] Todos os links do PDF e dos READMEs abrem (repos, vídeo, SonarCloud, docs).
- [ ] CI verde na `main` dos 3 serviços, com cobertura ≥ 80% visível nos badges.
- [ ] Tag `v4.0.0` em cada repositório de serviço, marcando a versão entregue.
- [ ] Revisar o [checklist final do plano](README.md#11-checklist-final-de-entrega) item a item.
- [ ] Depois da gravação e da correção: `terraform destroy` dos ambientes para evitar custo, se não houver mais demonstração.

## Pronto quando

- [ ] Da 6.0 (vieram das Etapas 2 a 5):
  - um merge em `homolog` de qualquer serviço builda, testa, publica a imagem e faz o rollout no EKS sem passo manual;
  - os 3 serviços + RabbitMQ rodam no EKS, acessíveis pelo API Gateway com JWT, e o webhook é acessível sem JWT;
  - o Terraform do `execution-service` aplica sem erro, e o pod acessa a tabela via IRSA, sem chaves de acesso;
  - no sandbox do Mercado Pago: preferência criada, pagamento com cartão de teste, webhook recebido pelo API Gateway e validado, `PagamentoConfirmado` publicado e estorno realizado;
  - um único trace no New Relic cobre a requisição HTTP, o publish, o consumer no outro serviço, a chamada ao Mercado Pago e o evento de volta; filtrar os logs por `NumeroOS` mostra os 3 serviços;
  - o dashboard "Saga de OS" e os alertas aplicados e mostrando dados.
- [ ] O PDF foi enviado no portal dentro do prazo, com todos os links funcionando.
- [ ] Todos os itens do [checklist final](README.md#11-checklist-final-de-entrega) estão marcados.
