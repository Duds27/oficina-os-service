# Etapa 4 — Plataforma: infra, Kubernetes e CI/CD

> [← Plano geral](README.md) · **Semanas:** 2–5 (contínua) · **Frente:** D · **Depende de:** [Etapa 0](etapa-0-fundacao.md) (repos + spike S4)
> **Repositórios:** `oficina-infra-k8s`, `oficina-lambda-auth` e os 3 repos de serviço (manifestos e pipelines)

## Objetivo

Cada microsserviço com pipeline independente: build, testes, verificação de qualidade e deploy automático no EKS. Além disso, a infraestrutura compartilhada pronta: RabbitMQ, roteamento no API Gateway e rede.

## Tarefas

### 4.1 `oficina-infra-k8s`: recursos compartilhados

- [ ] **EBS CSI driver**, se ainda não existir: add-on `aws-ebs-csi-driver` com role IRSA + StorageClass `gp3` padrão. O RabbitMQ precisa dele para o volume persistente.
- [ ] **RabbitMQ** no namespace `messaging`:
  - Instalar o **RabbitMQ Cluster Operator** (oficial) e um `RabbitmqCluster` com plugins `rabbitmq_management` e `rabbitmq_prometheus`, PVC gp3 e requests/limits.
  - Réplicas: 1 em `homolog` (custo). Em `prod`, 3 réplicas + *quorum queues* (documentar, mesmo que não fique ligado por custo).
  - Vhost `oficina` e **um usuário por serviço** via arquivo de *definitions* importado no boot. Assim não precisa do Topology Operator nem do cert-manager.
  - Senhas geradas com `random_password` e gravadas no Secrets Manager (`rabbitmq/os`, `rabbitmq/billing`, `rabbitmq/execution`).
  - Fixar a versão de imagem validada nos spikes S1/S4.
- [ ] **Namespaces** `oficina-os`, `oficina-billing` e `oficina-execution` (opcional: NetworkPolicy liberando só o tráfego necessário).
- [ ] **NLB**: um listener + target group por serviço (ex.: 8081 os, 8082 billing, 8083 execution).
  - O listener do `os-service` também precisa aceitar a Lambda (security group), por causa do endpoint interno.
- [ ] Outputs para `terraform_remote_state`:
  - ARNs dos target groups.
  - ARN/URL do OIDC provider (IRSA).
  - Subnets privadas e security group dos nós (para os RDS dos serviços).
  - Nome do cluster.

### 4.2 `oficina-lambda-auth`: API Gateway

- [ ] Rotas **por recurso** (os caminhos já são exclusivos de cada serviço, então não precisa reescrever o path):

  | Rotas (`ANY /x` e `ANY /x/{proxy+}`) | Destino (VPC Link → NLB) | Authorizer |
  | --- | --- | --- |
  | `/api/clientes`, `/api/veiculos`, `/api/pecas`, `/api/servicos`, `/api/ordens-servico` | listener `os-service` | JWT |
  | `/api/orcamentos`, `/api/pagamentos` | listener `billing-service` | JWT |
  | `/api/execucoes` | listener `execution-service` | JWT |
  | `POST /webhooks/mercadopago` | listener `billing-service` | **nenhum** (assinatura validada no serviço) + throttling |
  | `POST /auth` | Lambda | — |

- [ ] `/internal/*` e `/dev/*` **não** são roteados.
- [ ] Lambda: variável `OS_SERVICE_URL` + segredo do header interno (detalhes na [Etapa 1](etapa-1-os-service.md#15-lambda-de-autenticação-oficina-lambda-auth)). O código e o Terraform já estão prontos desde a Etapa 1; falta o lado da AWS:
  - Criar o secret da chave interna (`{"key":"..."}`) e injetar o mesmo valor no os-service como `InternalApi__Key`.
  - Cadastrar no repo da Lambda as variables `OS_SERVICE_URL` (DNS do NLB interno + porta do listener do os-service) e `INTERNAL_KEY_SECRET_ID`. Sem elas, o CD falha na validação do Terraform.
  - Passar `os_service_port` com a porta do listener do os-service (8081, se o NLB ficar com um listener por serviço). O SG da Lambda só libera essa porta.
  - Exportar o DNS do NLB interno nos outputs do `infra-k8s`, para montar o `OS_SERVICE_URL`.

### 4.3 Manifestos Kubernetes (em cada repo de serviço)

- [ ] `k8s/base`:
  - `Deployment` com 2 réplicas, readiness `/health/ready`, liveness `/health/live`, requests/limits, `envFrom` (ConfigMap + Secret), `serviceAccountName` e `terminationGracePeriodSeconds` suficiente para os consumers terminarem a mensagem em andamento.
  - `Service` ClusterIP.
  - `HPA` (CPU 70% / memória 80%, mesmo padrão da Fase 2).
  - `ConfigMap` com `RabbitMq__Host`, `RabbitMq__VirtualHost`, ambiente e prazos da saga.
  - `ServiceAccount`.
  - `kustomization.yaml`.
- [ ] `k8s/overlays/local` (kind): imagem local e Secret de desenvolvimento. O banco local fica no compose ou no próprio overlay.
- [ ] `k8s/overlays/aws`:
  - Imagem do ECR e `TargetGroupBinding` (placeholder do ARN substituído no CD).
  - Anotação IRSA na ServiceAccount (`eks.amazonaws.com/role-arn`): obrigatória no `execution-service`.

### 4.4 CI (`.github/workflows/ci.yml`, igual nos 3 repos)

```yaml
# Resumo — dispara em PR e push na main, e é reutilizável pelo CD
on:
  pull_request: { branches: [main] }
  push: { branches: [main] }
  workflow_call: {}
jobs:
  build-test-quality:
    runs-on: ubuntu-latest           # Docker disponível para os Testcontainers
    steps:
      # checkout com fetch-depth: 0 (o Sonar usa o histórico)
      # setup-dotnet 8 + dotnet tool install dotnet-sonarscanner
      # dotnet sonarscanner begin /k:<projeto> /o:<org>
      #     /d:sonar.cs.opencover.reportsPaths=**/coverage.opencover.xml /d:sonar.qualitygate.wait=true
      # dotnet build -c Release
      # dotnet test -c Release --no-build --collect:"XPlat Code Coverage"
      #     -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover,cobertura
      # reportgenerator: consolida a cobertura de todos os projetos de teste
      #     e o passo falha se a cobertura de linhas for < 80%
      # dotnet sonarscanner end          → falha se o quality gate reprovar
      # docker build (sem push) — garante que a imagem compila
      # upload do relatório de cobertura como artifact (evidência)
```

- [ ] Exclusões de cobertura iguais no ReportGenerator e no Sonar (`sonar.coverage.exclusions`): `**/Migrations/**`, `**/*.Designer.cs`, `**/Program.cs`, DTOs.
- [ ] Badge do SonarCloud (quality gate + coverage) no README.

### 4.5 CD (`.github/workflows/cd.yml`)

```yaml
# Resumo — deploy só acontece se o CI (testes + cobertura + Sonar) passar
on:
  push: { branches: [homolog, prod] }
  workflow_dispatch: {}
permissions: { id-token: write, contents: read }
jobs:
  ci:
    uses: ./.github/workflows/ci.yml
    secrets: inherit
  deploy:
    needs: ci
    environment: ${{ github.ref_name }}
    steps:
      # credenciais AWS via OIDC → login no ECR → build/push :<sha>
      # aws eks update-kubeconfig
      # Secrets Manager → kubectl create secret (dry-run | apply) no namespace do serviço
      # kustomize edit set image + substituição do ARN do target group
      # kubectl apply -k k8s/overlays/aws && kubectl rollout status (timeout 300s)
      # smoke test: /health/ready responde 200 (kubectl run curl ou port-forward)
```

- [ ] Variables e secrets por repo (Environments `homolog` e `prod`):

  | Repo | Variables | Secrets |
  | --- | --- | --- |
  | os-service | `AWS_REGION`, `ECR_REPOSITORY`, `EKS_CLUSTER_NAME`, `TARGET_GROUP_ARN`, `DB_SECRET_ID`, `JWT_SECRET_ID`, `INTERNAL_KEY_SECRET_ID`, `RABBITMQ_SECRET_ID` | `AWS_DEPLOY_ROLE_ARN`, `SONAR_TOKEN`, `NEW_RELIC_LICENSE_KEY` |
  | billing-service | idem + `MP_SECRET_ID` (`DB_SECRET_ID` = `billing/db`) | idem |
  | execution-service | idem, sem banco SQL + `DYNAMODB_TABLE`, `IRSA_ROLE_ARN` | idem |

### 4.6 Pipeline de infraestrutura dos serviços (`infra.yml`)

- [ ] Em cada repo de serviço, para mudanças em `infra/**`:
  - **PR:** `terraform fmt -check`, `validate` e `plan`, com o plano comentado no PR.
  - **Push na `main`:** `apply` com aprovação manual (Environment com *required reviewers*).

### 4.7 Proteção dos repositórios

- [ ] `main`:
  - Exige PR, com o job `build-test-quality` e o check do SonarCloud obrigatórios.
  - Exige branch atualizada e bloqueia push direto e force-push.
  - Opcional: 1 aprovação.
- [ ] `homolog`/`prod`: só recebem merge da `main` por PR. O Environment `prod` exige aprovação.
- [ ] Aplicar nos 3 repos de serviço e manter nos 3 repos de infra/Lambda.

### 4.8 Ambiente local completo

- [ ] `docker-compose.full.yml` no `os-service`. Pressupõe os 3 repos clonados lado a lado:

  ```
  oficina/
  ├── oficina-os-service/        (este compose)
  ├── oficina-billing-service/
  └── oficina-execution-service/
  ```

  Sobe RabbitMQ (management), MySQL, PostgreSQL, DynamoDB Local e os 3 serviços (`build: ../oficina-<servico>`), com `Pagamentos__Gateway=Fake`.
- [ ] Script de seed: cliente, veículo, serviços e peças.
- [ ] (Opcional) Workflow manual `e2e.yml` no `os-service`: sobe o compose no runner e roda a collection Postman com Newman.

## PRs sugeridos

1. `oficina-infra-k8s`: **infra: EBS CSI + RabbitMQ (operator) + namespaces + listeners NLB por serviço**
2. `oficina-lambda-auth`: **infra: rotas do API Gateway por serviço + webhook público + Lambda via HTTP**
3. Cada serviço: **ci: build, testes, cobertura ≥ 80% e SonarCloud**
4. Cada serviço: **cd: deploy automático no EKS condicionado ao CI**
5. Cada serviço: **k8s: manifestos base + overlays local/aws**
6. `oficina-os-service`: **chore: docker-compose completo + seed (+ e2e opcional)**

## Pronto quando

- [ ] Um PR que reduz a cobertura abaixo de 80% ou reprova o quality gate **não pode ser mergeado**.
- [ ] Um merge em `homolog` de qualquer serviço builda, testa, publica a imagem e faz o rollout no EKS sem passo manual.
- [ ] Os 3 serviços + RabbitMQ rodam no EKS, acessíveis pelo API Gateway com JWT. O webhook é acessível sem JWT.
- [ ] O `docker-compose.full.yml` sobe o sistema inteiro numa máquina limpa.
