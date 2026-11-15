# Etapa 6 — Documentação, evidências e entrega

> [← Plano geral](README.md) · **Semana:** 6 · **Frente:** todas · **Depende de:** [Etapas 1–5](README.md#7-etapas)
> **Repositórios:** todos

## Objetivo

Fechar os entregáveis do enunciado: repositórios documentados com evidências, Swagger/Postman atualizados, vídeo de até 15 minutos e o PDF do portal.

## Tarefas

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

- [ ] O PDF foi enviado no portal dentro do prazo, com todos os links funcionando.
- [ ] Todos os itens do [checklist final](README.md#11-checklist-final-de-entrega) estão marcados.
