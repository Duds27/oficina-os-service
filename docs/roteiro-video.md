# Roteiro do Vídeo de Demonstração — Fase 3 (≤ 15 min)

Objetivo: demonstrar, ao vivo, os itens exigidos no enunciado. Orçamento de tempo total
**~14:30** (folga para o limite de 15:00). Grave em 1080p; use zoom no terminal/telas.

## Antes de gravar (pré-condições)

- [ ] Infra provisionada e app no ar (`infra-k8s` → `infra-database` → `lambda-auth` → `oficina-app`).
- [ ] Um **cliente ativo** cadastrado no banco com um CPF válido conhecido (para o login).
- [ ] Endpoint do **API Gateway** à mão + coleção Postman/Insomnia importada.
- [ ] Abas abertas: GitHub (Actions + Branches), New Relic (dashboard + Logs/APM), terminal com `kubectl`.
- [ ] Uma alteração trivial pronta para abrir um PR (ex.: 1 linha no README) para demonstrar CI/CD ao vivo.
- [ ] `kubectl config` apontando para o EKS; `kubectl get pods -n oficina` funcionando.

---

## Bloco 0 — Abertura (0:00 – 0:45)

- **Tela:** slide/título com nome do grupo e integrantes.
- **Narração:** “Tech Challenge 15SOAT, Fase 3 — oficina mecânica em operação corporativa
  na AWS: autenticação serverless por CPF, EKS escalável, RDS gerenciado, observabilidade e
  CI/CD em 4 repositórios.”

## Bloco 1 — Visão de arquitetura (0:45 – 2:00)

- **Tela:** `docs/architecture/component-diagram.md` (diagrama renderizado).
- **Narração:** percorrer o fluxo: Cliente → API Gateway → (Lambda auth | Lambda Authorizer)
  → EKS (API .NET + HPA) → RDS MySQL; Secrets Manager e New Relic ao lado. Citar os 4 repositórios.

## Bloco 2 — Autenticação com CPF (2:00 – 4:00)

- **Tela:** Postman → `POST {api-gw}/auth` com `{ "cpf": "<cpf válido>" }`.
- **Mostrar:**
  1. CPF **válido de cliente ativo** → `200` com `token` + `expiresAt`.
  2. CPF **inválido** → `400`; CPF **inexistente** → `401`; (se possível) cliente **inativo** → `403`.
- **Narração:** a Function Serverless valida os dígitos do CPF, consulta o cliente no RDS,
  verifica o status e emite um JWT HS256. Mostrar rapidamente o payload do token (jwt.io).

## Bloco 3 — Pipeline CI/CD + deploy automatizado (4:00 – 7:30)

- **Tela:** GitHub do `oficina-app`.
- **Mostrar:**
  1. Regra de proteção da `main` (sem push direto, PR obrigatório) em _Settings → Branches_.
  2. Abrir um **PR** com a alteração trivial → **CI rodando** (build + testes) → check verde.
  3. Merge para `homolog` (ou `prod`) → **workflow de CD dispara**: build → **ECR** → deploy no **EKS**.
  4. `kubectl -n oficina rollout status deployment/api` e `kubectl get pods` mostrando o rollout.
- **Narração:** deploy automático por branch de ambiente; imagem versionada no ECR; kustomize overlay `aws`.

## Bloco 4 — Consumo das APIs protegidas (7:30 – 9:30)

- **Tela:** Postman.
- **Mostrar:**
  1. `GET {api-gw}/api/clientes` **sem token** → `401` (bloqueado pelo Lambda Authorizer).
  2. `GET {api-gw}/api/clientes` **com `Bearer <token>`** → `200`.
  3. **Abrir uma OS**: `POST {api-gw}/api/ordens-servico` (autenticado) → `201`.
  4. Avançar status da OS (ex.: Recebida → Em Diagnóstico) para gerar eventos de negócio.
- **Narração:** as rotas sensíveis exigem o JWT emitido pela Lambda; o app mantém validação própria.

## Bloco 5 — Dashboard de monitoramento ao vivo (9:30 – 12:00)

- **Tela:** New Relic → dashboard “Oficina Mecânica — Fase 3”.
- **Mostrar (idealmente refletindo as ações dos blocos anteriores):**
  1. **Volume diário de OS** subindo após a OS criada.
  2. **Tempo médio por status** / transições por status.
  3. **Latência das APIs** (p95) e **CPU/memória por pod**.
  4. (Se der) gerar carga leve (`k8s/loadtest`) e mostrar o **HPA escalando** (`kubectl get hpa -w`).
- **Narração:** métricas de negócio derivadas de logs estruturados + APM; healthchecks/uptime.

## Bloco 6 — Logs e traces em execução (12:00 – 14:00)

- **Tela:** New Relic → Logs e APM (Distributed Tracing).
- **Mostrar:**
  1. Logs em **JSON** com o campo **`CorrelationId`**; filtrar por um `X-Correlation-ID` da requisição feita.
  2. Eventos `Evento=os_criada` / `os_status_alterado`.
  3. Um **trace distribuído** de uma requisição `/api/*` (latência por span).
  4. (Opcional) disparar/forçar `os_falha_processamento` e mostrar o **alerta** correspondente.
- **Narração:** correlação ponta a ponta entre requisição, logs e traces.

## Bloco 7 — Encerramento (14:00 – 14:30)

- **Tela:** README/entrega.
- **Narração:** recapitular os requisitos atendidos; citar os 4 repositórios, o usuário
  `soat-architecture` adicionado e onde está a documentação. Agradecer.

---

## Dicas

- Ensaie uma vez cronometrando; corte o que passar de 15:00.
- Deixe requisições e comandos **pré-digitados** (histórico do terminal / Postman salvo).
- Se o deploy ao vivo for arriscado, grave-o separadamente e **acelere** (2x) esse trecho.
- Fale o **porquê** das decisões (RFCs/ADRs), não só o “o quê”.
