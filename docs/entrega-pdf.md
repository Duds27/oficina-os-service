# Tech Challenge FIAP — 15SOAT · Fase 3
## Documento de Entrega

> Esqueleto do **PDF único** para o Portal do Aluno. Preencha os campos `[preencher]`,
> revise e exporte para PDF (VS Code “Markdown PDF”, Pandoc, ou impressão do navegador).
> Os itens abaixo seguem exatamente o enunciado da Fase 3.

---

### 1. Identificação

| Campo | Valor |
| --- | --- |
| Curso / Turma | Software Architecture — 15SOAT |
| Fase | 3 — Operação Corporativa em Nuvem |
| Grupo | `[preencher: nome/número do grupo]` |
| Data de entrega | `[preencher]` |

**Integrantes**

| Nome | RM | GitHub |
| --- | --- | --- |
| `[preencher]` | `[RM]` | `[@usuario]` |
| `[preencher]` | `[RM]` | `[@usuario]` |
| `[preencher]` | `[RM]` | `[@usuario]` |

---

### 2. Links dos 4 repositórios

| # | Repositório | Papel | URL |
| --- | --- | --- | --- |
| 1 | `oficina-lambda-auth` | Function Serverless de autenticação por CPF + API Gateway | `[preencher]` |
| 2 | `oficina-infra-k8s` | Terraform: VPC + EKS + add-ons | `[preencher]` |
| 3 | `oficina-infra-database` | Terraform: RDS MySQL gerenciado | `[preencher]` |
| 4 | `oficina-app` | Aplicação .NET (Clean Architecture) + manifestos K8s | `[preencher]` |

---

### 3. Vídeo de demonstração (≤ 15 min)

| Item | Valor |
| --- | --- |
| Plataforma | YouTube / Vimeo (público ou não listado) |
| URL | `[preencher]` |
| Duração | `[preencher] (máx. 15:00)` |

O vídeo demonstra: autenticação com CPF · execução da pipeline CI/CD · deploy automatizado ·
consumo das APIs protegidas · dashboard de monitoramento com análise ao vivo · logs e traces.
Roteiro: `docs/roteiro-video.md`.

---

### 4. Links das documentações

| Documento | URL |
| --- | --- |
| Documentação arquitetural (índice) | `[preencher]/docs/architecture/README.md` |
| Diagrama de Componentes | `[preencher]/docs/architecture/component-diagram.md` |
| Diagramas de Sequência (auth + abertura de OS) | `[preencher]/docs/architecture/sequence-diagrams.md` |
| Modelo de Dados (ER + justificativa do banco) | `[preencher]/docs/architecture/database-model.md` |
| RFCs (nuvem, banco, autenticação) | `[preencher]/docs/architecture/rfcs/` |
| ADRs (comunicação, HPA, 4 repos, observabilidade) | `[preencher]/docs/architecture/adrs/` |
| Observabilidade (New Relic) | `[preencher]/docs/observability/README.md` |
| Swagger (API principal) | `[preencher]` (ex.: `https://<host>/swagger`) |
| Coleção Postman | `[preencher]/lambda-auth/docs/postman_collection.json` |
| Dashboard New Relic (link público) | `[preencher]` |

---

### 5. Confirmação do usuário `soat-architecture`

Usuário **`soat-architecture`** adicionado como colaborador em **todos os 4 repositórios**
(_Settings → Collaborators and teams_):

| Repositório | Adicionado? | Print/evidência |
| --- | --- | --- |
| oficina-lambda-auth | ☐ Sim | `[anexar print]` |
| oficina-infra-k8s | ☐ Sim | `[anexar print]` |
| oficina-infra-database | ☐ Sim | `[anexar print]` |
| oficina-app | ☐ Sim | `[anexar print]` |

---

### 6. Arquitetura (resumo)

Solução em **AWS**: **API Gateway** como entrada única; **Lambda (Node/TS)** autentica por
CPF e emite **JWT**, com **Lambda Authorizer** protegendo `/api/*`; aplicação **.NET** em
**EKS** (com HPA) acessando **RDS MySQL** gerenciado; **New Relic** para APM, infraestrutura,
logs estruturados e alertas. IaC em **Terraform**; CI/CD por repositório com **GitHub Actions**
e deploy automático nas branches `homolog`/`prod`.

_(Cole aqui o diagrama de componentes — `docs/architecture/component-diagram.md`.)_

---

### 7. Checklist de conformidade (auto-avaliação)

- [ ] API Gateway implementado e roteando as rotas.
- [ ] Rotas sensíveis protegidas por autenticação via CPF.
- [ ] Function Serverless valida CPF, consulta o cliente e devolve JWT.
- [ ] 4 repositórios separados, cada um com CI/CD e deploy automático.
- [ ] `main` protegida (sem commits diretos) + PRs obrigatórios.
- [ ] Banco gerenciado (RDS MySQL), cluster K8s escalável e Terraform.
- [ ] Monitoramento: latência, CPU/memória, healthchecks/uptime, alertas de falha de OS.
- [ ] Logs estruturados (JSON) com correlação entre requisições.
- [ ] Dashboards: volume diário de OS, tempo médio por status, erros de integração.
- [ ] Documentação: componentes, sequência, RFCs, ADRs, ER + justificativa do banco.
- [ ] READMEs completos e links de deploy ativos.
- [ ] Vídeo (≤15 min) e usuário `soat-architecture` nos 4 repos.
