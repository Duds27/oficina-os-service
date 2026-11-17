# Etapa 4 — Plataforma: infra, Kubernetes e CI/CD

> [← Plano geral](README.md) · **Semanas:** 2–5 (contínua) · **Frente:** D · **Depende de:** [Etapa 0](etapa-0-fundacao.md) (repos + spike S4)
> **Repositórios:** `oficina-infra-k8s`, `oficina-lambda-auth` e os 3 repos de serviço (manifestos e pipelines)

## Objetivo

Cada microsserviço com pipeline independente: build, testes, verificação de qualidade e deploy automático no EKS. Além disso, a infraestrutura compartilhada pronta: RabbitMQ, roteamento no API Gateway e rede.

## Tarefas

### 4.1 `oficina-infra-k8s`: recursos compartilhados

- [x] **EBS CSI driver**, se ainda não existir: add-on `aws-ebs-csi-driver` com role IRSA + StorageClass `gp3` padrão. O RabbitMQ precisa dele para o volume persistente.
- [x] **RabbitMQ** no namespace `messaging`:
  - Instalar o **RabbitMQ Cluster Operator** (oficial) e um `RabbitmqCluster` com plugins `rabbitmq_management` e `rabbitmq_prometheus`, PVC gp3 e requests/limits.
  - Réplicas: 1 em `homolog` (custo). Em `prod`, 3 réplicas + *quorum queues* (documentar, mesmo que não fique ligado por custo).
  - Vhost `oficina` e **um usuário por serviço** via arquivo de *definitions* importado no boot. Assim não precisa do Topology Operator nem do cert-manager.
  - Senhas geradas com `random_password` e gravadas no Secrets Manager (`rabbitmq/os`, `rabbitmq/billing`, `rabbitmq/execution`).
  - Fixar a versão de imagem validada nos spikes S1/S4.
  - **Na implementação:**
    - **Versão do operator.** Ficou na v2.21.1, baixada da release e conferida por SHA-256 no plan. A v2.22+ exige o cert-manager (webhooks) e o RabbitMQ 4.2.4+, por causa do novo startup probe. A imagem é `rabbitmq:4.1-management`, a mesma do S1.
    - **Usuário admin.** Com definitions no boot, o RabbitMQ não cria o usuário padrão do operator. Por isso entrou um usuário `admin` (tag administrator) para a UI por port-forward, com a senha em `rabbitmq/admin`.
    - **Recursos.** Os requests do operator caem de 500Mi para 128Mi, e a memória do broker fica com request igual ao limit (512Mi), como o operator recomenda.
    - **Validado num kind (spike S4, fora o EBS):** o operator e o `RabbitmqCluster` ficaram com todas as condições `True`, e as definitions criaram vhost, usuários e permissões. A mensagem publicada de um pod foi conferida com os 3 serviços no kind (4.3).
- [x] **Namespaces** `oficina-os`, `oficina-billing` e `oficina-execution` (opcional: NetworkPolicy liberando só o tráfego necessário). Ficam no Terraform, e não nos manifestos, para o CD não disputar a posse do namespace. A NetworkPolicy não entrou: na VPC CNI ela exige ligar o agente de network policy.
- [x] **NLB**: um listener + target group por serviço (ex.: 8081 os, 8082 billing, 8083 execution).
  - O listener do `os-service` também precisa aceitar a Lambda (security group), por causa do endpoint interno.
  - **Na implementação:** o NLB não tem security group. Os alvos são IP e chegam aos pods pela regra do SG dos nós (CIDR da VPC), o que já cobre a Lambda. As saídas antigas (`app_listener_arn`, `app_target_group_arn`) ficam como alias do os-service durante a migração.
- [x] Outputs para `terraform_remote_state` (mais `nlb_dns_name`, `listener_ports`, `rabbitmq_secret_ids` e `service_namespaces`):
  - ARNs dos target groups.
  - ARN/URL do OIDC provider (IRSA).
  - Subnets privadas e security group dos nós (para os RDS dos serviços).
  - Nome do cluster.
- **Testes:** `terraform test` com providers mockados e os módulos da VPC, do EKS e das roles substituídos, em 9 casos, no CI do repo.
- **Pendente, decisão do grupo antes do apply:** a capacidade dos nós. Com `t3.micro` (1 GiB e cerca de 400 MiB alocáveis), o pod do RabbitMQ (512 MiB) não cabe em nenhum nó. A conta está no [README do infra-k8s](https://github.com/Duds27/oficina-infra-k8s#capacidade-dos-nós-decidir-antes-do-apply). No Free Plan, `t3.small` também é elegível.

### 4.2 `oficina-lambda-auth`: API Gateway

- [x] Rotas **por recurso** (os caminhos já são exclusivos de cada serviço, então não precisa reescrever o path):

  | Rotas (`ANY /x` e `ANY /x/{proxy+}`) | Destino (VPC Link → NLB) | Authorizer |
  | --- | --- | --- |
  | `/api/clientes`, `/api/veiculos`, `/api/pecas`, `/api/servicos`, `/api/ordens-servico` | listener `os-service` | JWT |
  | `/api/orcamentos`, `/api/pagamentos` | listener `billing-service` | JWT |
  | `/api/execucoes` | listener `execution-service` | JWT |
  | `POST /webhooks/mercadopago` | listener `billing-service` | **nenhum** (assinatura validada no serviço) + throttling |
  | `POST /auth` | Lambda | — |

  - **Na implementação:** entrou também `POST /api/auth/login` (os-service, sem JWT e com o throttling do webhook). É o login da equipe, que emite o token de Admin e de Mecânico. Na Fase 3, a rota genérica `ANY /api/{proxy+}` exigia JWT até para ele. Throttling: 100 req/s com rajada de 200 em todas as rotas, e 10 req/s com rajada de 20 nas públicas.
  - `terraform test` com provider mockado (8 casos), no CI do repo.

- [x] `/internal/*` e `/dev/*` **não** são roteados (há um teste para isso).
- [ ] Lambda: variável `OS_SERVICE_URL` + segredo do header interno (detalhes na [Etapa 1](etapa-1-os-service.md#15-lambda-de-autenticação-oficina-lambda-auth)). O código e o Terraform já estão prontos desde a Etapa 1; falta o lado da AWS:
  - Criar o secret da chave interna (`{"key":"..."}`) e injetar o mesmo valor no os-service como `InternalApi__Key`.
  - Cadastrar no repo da Lambda as variables `OS_SERVICE_URL` (DNS do NLB interno + porta do listener do os-service) e `INTERNAL_KEY_SECRET_ID`. Sem elas, o CD falha na validação do Terraform.
  - Passar `os_service_port` com a porta do listener do os-service (8081, se o NLB ficar com um listener por serviço). O SG da Lambda só libera essa porta.
  - Exportar o DNS do NLB interno nos outputs do `infra-k8s`, para montar o `OS_SERVICE_URL`.
  - **Na implementação:**
    - O Terraform da Lambda monta `OS_SERVICE_URL` (`http://<nlb_dns_name>:<porta do os>`) e a porta do SG a partir das saídas do `infra-k8s`. A variable `OS_SERVICE_URL` ficou opcional, e o DNS já é exportado.
    - O CD do os-service injeta `InternalApi__Key` a partir do `INTERNAL_KEY_SECRET_ID`.
    - Falta só a parte da AWS e do GitHub: criar o secret da chave interna e cadastrar `INTERNAL_KEY_SECRET_ID` nos dois repos.

### 4.3 Manifestos Kubernetes (em cada repo de serviço)

- [x] `k8s/base`:
  - `Deployment` com 2 réplicas, readiness `/health/ready`, liveness `/health/live`, requests/limits, `envFrom` (ConfigMap + Secret), `serviceAccountName` e `terminationGracePeriodSeconds` suficiente para os consumers terminarem a mensagem em andamento.
  - `Service` ClusterIP.
  - `HPA` (CPU 70% / memória 80%, mesmo padrão da Fase 2).
  - `ConfigMap` com `RabbitMq__Host`, `RabbitMq__VirtualHost`, ambiente e prazos da saga.
  - `ServiceAccount`.
  - `kustomization.yaml`.
- [x] `k8s/overlays/local` (kind): imagem local e Secret de desenvolvimento. O banco local fica no compose ou no próprio overlay.
- [x] `k8s/overlays/aws`:
  - Imagem do ECR e `TargetGroupBinding` (placeholder do ARN substituído no CD).
  - Anotação IRSA na ServiceAccount (`eks.amazonaws.com/role-arn`): obrigatória no `execution-service`.
- **Na implementação:**
  - **Probes.** `startupProbe` de até 3 min. Sem ele, o liveness derrubava o os-service no kind enquanto o MySQL subia, porque ele aplica as migrations antes de abrir a porta.
  - **Parada.** `preStop` de 10 s e `terminationGracePeriodSeconds: 45`.
  - **Memória.** `DOTNET_gcServer=0`, com requests de 256Mi. No kind, o uso ficou entre 80 e 165 Mi.
  - **Segurança.** Usuário 1654, sem escalada de privilégio e sem capabilities.
  - **Réplicas na AWS.** No overlay aws, 1 réplica e HPA de 1 a 3, pelo custo em homolog.
  - **Broker local.** No kind, o overlay local do os-service sobe um RabbitMQ de desenvolvimento no namespace `messaging`, com o mesmo DNS do EKS. Os overlays dos outros dois usam esse broker.
  - **Nomes do os-service.** Os da Fase 3 (namespace `oficina`, `api`, `api-config`, `api-secrets`) viraram `oficina-os` e `os-service`. O `cd.yml` da Fase 2 (GHCR + kind) virou o `kind.yml`, que confere o overlay local num kind no runner. Os guias da Fase 3 (`aws-setup.md`, `roteiro-video.md`) ainda usam os nomes antigos e entram na Etapa 6.
  - **Validação.** Conferido em 2026-11-16 num kind com os 3 serviços, cada um no seu namespace, e o broker em `messaging`. A saga completa passou: OS aberta, diagnóstico, orçamento, aprovação, pagamento simulado, execução e `Concluida`.

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

- [x] Exclusões de cobertura iguais no ReportGenerator e no Sonar (`sonar.coverage.exclusions`): `**/Migrations/**`, `**/*.Designer.cs`, `**/Program.cs`, DTOs.
  - **Na implementação:** em cada repo, o Sonar usa as mesmas exclusões do `coverlet.runsettings` daquele repo. No os-service são as quatro acima. No billing, só as migrations. No execution, nenhuma (Program.cs e DTOs contam, como na Etapa 3).
- [x] Badge do SonarCloud (quality gate + coverage) no README.
- **Na implementação:**
  - `dotnet-sonarscanner` 11.3.0 (a 12.0 é uma major de um mês) e ReportGenerator 5.5.12.
  - O `coverlet.runsettings` gera cobertura em cobertura e em opencover.
  - O gate lê a taxa de linhas do relatório consolidado. Foi conferido que passa com 80% e falha com 99%.
  - Sem `SONAR_TOKEN` (PR de fork ou do Dependabot), o Sonar é pulado.
  - O job do os-service foi renomeado de `build-test` para `build-test-quality`, o nome do *required check* nos 3 repos.
  - Cobertura local em 2026-11-16: os-service 95,5% (237 testes), billing 95,8% (182) e execution 97,4% (130).
  - Falta a primeira execução no GitHub, que dispara no PR.

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

  - **Na implementação:**
    - **O `cd.yml` está pronto nos 3 repos**, e no os-service substitui o `cd-aws.yml`. O deploy só roda depois do CI do mesmo commit.
    - **O job confere as variables** e para com a lista do que falta.
    - **A imagem vai para o ECR** com a tag do commit.
    - **O Secret do pod** sai do Secrets Manager, com os valores mascarados no log. O secret do Mercado Pago, ainda vazio, é pulado, e o gateway segue Fake.
    - **Placeholders.** Um placeholder sem valor derruba o deploy antes do `apply`.
    - **Rollout.** O pod é anotado com o hash do Secret, então credencial trocada sem mudança de código também reinicia os pods. Depois vem um smoke test em `/health/ready` de dentro do namespace.
    - **Variables novas:** `API_BASE_URL` no billing (URL do API Gateway, para o link de aprovação e o webhook) e o `adminPassword`, opcional, no secret do JWT do os-service.
    - **Falta cadastrar** as variables e os secrets nos Environments. As tabelas com a origem de cada valor estão no README de cada repo, seção CD.
    - **Conferido com `actionlint` 1.7.12 (com shellcheck), sem apontamentos.** Ainda não rodou contra a AWS.

### 4.6 Pipeline de infraestrutura dos serviços (`infra.yml`)

- [x] Em cada repo de serviço, para mudanças em `infra/**`:
  - **PR:** `terraform fmt -check`, `validate` e `plan`, com o plano comentado no PR.
  - **Push na `main`:** `apply` com aprovação manual (Environment com *required reviewers*).
  - **Na implementação:** é o `infra.yml` do billing e do execution. O os-service não tem `infra/` na AWS: o banco dele é o do `oficina-infra-database`.
    - **Ambientes.** O plan é de homolog. O apply vai para homolog no push da `main` e passa pelo Environment `infra`, separado do `homolog` para não travar o CD. O plan ou apply manual vale para homolog ou prod.
    - **Comentário.** O plano fica num comentário do PR, atualizado a cada push.
    - **Variables.** `AWS_REGION`, `TF_STATE_BUCKET`, `TF_LOCK_TABLE` e o secret `AWS_DEPLOY_ROLE_ARN` ficam no repositório.
- [ ] Primeiro `apply` do `infra/` do `execution-service` (tabela, role IRSA e ECR), que já tem `fmt`, `validate` e `terraform test` no CI. Veio da [Etapa 2](etapa-2-execution-service.md#pronto-quando).

### 4.7 Proteção dos repositórios

- [ ] `main`:
  - Exige PR, com o job `build-test-quality` e o check do SonarCloud obrigatórios.
  - Exige branch atualizada e bloqueia push direto e force-push.
  - Opcional: 1 aprovação.
- [ ] `homolog`/`prod`: só recebem merge da `main` por PR. O Environment `prod` exige aprovação.
- [ ] Aplicar nos 3 repos de serviço e manter nos 3 repos de infra/Lambda.

### 4.8 Ambiente local completo

- [x] `docker-compose.full.yml` no `os-service`. Pressupõe os 3 repos clonados lado a lado:

  ```
  oficina/
  ├── oficina-os-service/        (este compose)
  ├── oficina-billing-service/
  └── oficina-execution-service/
  ```

  Sobe RabbitMQ (management), MySQL, PostgreSQL, DynamoDB Local e os 3 serviços (`build: ../oficina-<servico>`), com `Pagamentos__Gateway=Fake`.
- [x] Script de seed: cliente, veículo, serviços e peças.
- [x] (Opcional) Workflow manual `e2e.yml` no `os-service`: sobe o compose no runner e roda a collection Postman com Newman.
- **Na implementação:**
  - **Compose.** As pastas dos outros dois repos podem mudar por `BILLING_DIR` e `EXECUTION_DIR`, e os prazos por `PRAZO`. O nome do projeto é fixo (`oficina-full`), para os volumes não se misturarem com os do compose do `OficinaMecanicaBackend`.
  - **Seed.** O `scripts/seed.py` só usa a biblioteca padrão do Python e pode rodar de novo sem duplicar nada. Também roda como serviço do compose (`--profile seed`).
  - **E2E.** No lugar da collection Postman, que não cobre os 3 serviços, o `e2e.yml` roda o `scripts/e2e_saga.py`: a saga em 13 passos, até `Concluida`.
  - **Validação.** Em 2026-11-16, nesta máquina: build do zero, seed duas vezes e saga completa, 13 de 13.

### 4.9 Mercado Pago no sandbox

Veio da [Etapa 3](etapa-3-billing-service.md#pronto-quando) (spike S3). Depende da rota pública do webhook (4.2) e do `billing-service` no EKS (4.3 e 4.5).

- [ ] Contas de teste do Mercado Pago (vendedor e comprador), criadas pelo grupo.
- [ ] Token de teste e segredo do webhook no secret `billing/mercadopago`, preenchidos à mão pelo console ou CLI. Nunca no repositório nem no state do Terraform.
- [ ] No overlay `aws` do `billing-service`: `Pagamentos__Gateway=MercadoPago` e `Pagamentos__MercadoPago__NotificationUrl` com a rota pública `POST /webhooks/mercadopago` do API Gateway.
- [ ] Os 4 passos do spike: preferência criada, pagamento com cartão de teste, webhook recebido e validado, estorno.
- [ ] Payloads reais salvos para os testes com WireMock, no lugar dos que seguem a documentação.
- [ ] Resultado registrado no [ADR-009](../architecture/adrs/ADR-009-mercado-pago.md).

## PRs sugeridos

Um commit para cada, nas branches `feat/etapa-4-plataforma` (infra-k8s e os 3 serviços) e `feat/etapa-4-api-gateway` (Lambda). Nos serviços, a ordem dos commits é ci → k8s → cd, e o cd do billing e do execution traz também o `infra.yml` (4.6).

1. `oficina-infra-k8s`: **infra: EBS CSI + RabbitMQ (operator) + namespaces + listeners NLB por serviço**
2. `oficina-lambda-auth`: **infra: rotas do API Gateway por serviço + webhook público + Lambda via HTTP**
3. Cada serviço: **ci: build, testes, cobertura ≥ 80% e SonarCloud**
4. Cada serviço: **cd: deploy automático no EKS condicionado ao CI**
5. Cada serviço: **k8s: manifestos base + overlays local/aws**
6. `oficina-os-service`: **chore: docker-compose completo + seed (+ e2e opcional)**

## Pronto quando

- [ ] Um PR que reduz a cobertura abaixo de 80% ou reprova o quality gate **não pode ser mergeado**.
  - O CI já falha nos dois casos. Falta a proteção da `main` (4.7), que torna o job obrigatório, e a primeira execução com o SonarCloud no GitHub.
- [ ] Um merge em `homolog` de qualquer serviço builda, testa, publica a imagem e faz o rollout no EKS sem passo manual.
- [ ] Os 3 serviços + RabbitMQ rodam no EKS, acessíveis pelo API Gateway com JWT. O webhook é acessível sem JWT.
  - Os dois itens acima dependem dos applies e dos Environments (ver "Primeiro deploy na AWS"). No kind, os 3 serviços e o RabbitMQ já rodaram a saga completa.
- [ ] O `docker-compose.full.yml` sobe o sistema inteiro numa máquina limpa.
  - Conferido nesta máquina, com build do zero e volumes novos, mas com as imagens base já em cache. Uma máquina limpa de fato é o runner do `e2e.yml`, que roda depois do merge.
- [ ] O Terraform do `execution-service` aplica sem erro, e o pod acessa a tabela via IRSA, sem chaves de acesso. Veio da [Etapa 2](etapa-2-execution-service.md#pronto-quando).
- [ ] No sandbox do Mercado Pago: preferência criada, pagamento com cartão de teste, webhook recebido pelo API Gateway e validado, `PagamentoConfirmado` publicado e estorno realizado. Veio da [Etapa 3](etapa-3-billing-service.md#pronto-quando).

## Primeiro deploy na AWS

O código está pronto; a partir daqui, os passos dependem da conta AWS e das configurações do GitHub.

1. **Credenciais.** As da AWS nesta máquina estão inválidas (`InvalidClientTokenId`). Os applies podem rodar pelos workflows (OIDC) ou com credenciais renovadas.
2. **Capacidade dos nós.** Decidir o tipo de instância antes do apply do `infra-k8s` (4.1).
3. **Role do deploy.** A `AWS_DEPLOY_ROLE_ARN` da Fase 3 precisa de três coisas:
   - push nos ECRs novos;
   - acesso ao cluster: o criador vira admin pelo `enable_cluster_creator_admin_permissions`;
   - leitura dos secrets novos (`rabbitmq/*`, `billing/*`, chave interna).
4. **Ordem dos applies:**
   - `oficina-infra-k8s` (homolog);
   - o `infra/` do billing e do execution, pelo `infra.yml`;
   - `oficina-lambda-auth`, pelo CD no push em `homolog`.
5. **Secret da chave interna.** Criar o secret `{"key": "..."}` e cadastrar `INTERNAL_KEY_SECRET_ID` na Lambda e no os-service.
6. **GitHub.**
   - Environments `homolog`, `prod` (com aprovação) e `infra` (com *required reviewers*).
   - As variables de cada repo (README, seção CD).
   - A proteção das branches (4.7).
7. **Deploy e limpeza.** Push em `homolog` em cada serviço: o CD faz o deploy. Depois, apagar o namespace `oficina` da Fase 3.
8. **Sandbox do Mercado Pago (4.9).**
