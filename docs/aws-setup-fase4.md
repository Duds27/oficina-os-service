# Runbook — AWS da Fase 4 (conta Free, `us-east-2`)

Passo a passo para subir os 3 microsserviços na conta AWS do grupo: bootstrap, credenciais do CI,
applies, deploy e teardown. É a parte prática da [seção 6.0 da Etapa 6](fase4/etapa-6-documentacao-entrega.md#60-implantação-na-aws-e-validação-no-new-relic).
O runbook da Fase 3, de uma conta e de um repositório só, continua em [`aws-setup.md`](aws-setup.md).

Os comandos são bash (no Windows, o Git Bash). Os repositórios ficam lado a lado numa mesma pasta (o os-service é a `tech-challenge-fiap`).

## O que a conta permite

A conta é do **Free plan** e fica numa organização gerenciada pela própria AWS. As SCPs dessa
organização valem acima de qualquer permissão do IAM, inclusive da `AdministratorAccess`, e não
podem ser vistas nem alteradas de dentro da conta. Medido em 2026-11-17, com leituras e com o
simulador do IAM, que considera as SCPs:

| | Situação | Consequência no projeto |
| --- | --- | --- |
| Região | Só a **`us-east-2`** (Ohio) é liberada | Padrão de `aws_region` em todos os Terraform, e `AWS_REGION=us-east-2` no GitHub |
| Provider OIDC no IAM | `iam:CreateOpenIDConnectProvider` negado em qualquer região | Sem IRSA: as roles dos pods vêm do **EKS Pod Identity**. Sem OIDC no GitHub Actions: o CI entra com a **chave de acesso de um usuário IAM** |
| EKS, EC2, RDS, DynamoDB, ECR, Lambda, API Gateway, NLB, Secrets Manager, KMS, S3, CloudWatch | Liberados na `us-east-2` | Nada muda |
| Créditos | US$ 100, e o plano termina em 2027-05-17 ou quando o crédito acabar | Ligar o ambiente só para testar e gravar (veja [Custo](#custo)) |

A decisão está no [ADR-011](architecture/adrs/ADR-011-conta-aws-restrita.md).

## 0. Máquina local

- **Região.** A variável de ambiente `AWS_REGION` do Windows tem prioridade sobre o `~/.aws/config`.
  Ela estava em `us-east-1`, então é preciso trocar as duas e abrir um terminal novo.
  - No PowerShell, para a variável do usuário:

    ```powershell
    [Environment]::SetEnvironmentVariable('AWS_REGION', 'us-east-2', 'User')
    ```

  - No Git Bash, para o perfil da CLI:

    ```bash
    aws configure set region us-east-2
    ```

- **Credencial.** O usuário IAM local precisa de `AdministratorAccess`. Confira a conta e a região com:

  ```bash
  aws sts get-caller-identity
  aws configure get region
  ```

- **Variáveis da sessão:**

  ```bash
  export AWS_REGION=us-east-2 ENV=homolog
  export ACCOUNT=$(aws sts get-caller-identity --query Account --output text)
  export TF_STATE_BUCKET=oficina-tfstate-$ACCOUNT TF_LOCK_TABLE=oficina-tf-locks
  ```

## 1. Bootstrap (uma vez)

**Estado do Terraform.** Fora da `us-east-1`, o bucket exige o `LocationConstraint`:

```bash
aws s3api create-bucket --bucket $TF_STATE_BUCKET --region $AWS_REGION \
  --create-bucket-configuration LocationConstraint=$AWS_REGION
aws s3api put-bucket-versioning --bucket $TF_STATE_BUCKET --versioning-configuration Status=Enabled
aws dynamodb create-table --table-name $TF_LOCK_TABLE \
  --attribute-definitions AttributeName=LockID,AttributeType=S --key-schema AttributeName=LockID,KeyType=HASH \
  --billing-mode PAY_PER_REQUEST --region $AWS_REGION
```

**ECR do os-service.** O billing e o execution criam o deles no `infra/`, mas o os-service não tem um:

```bash
aws ecr create-repository --repository-name oficina-$ENV-os-service \
  --image-scanning-configuration scanOnPush=true --region $AWS_REGION
```

**Segredos compartilhados**, no formato `{"key":"..."}`:

- `oficina/jwt`: o JWT da Lambda e dos 3 serviços;
- `oficina/internal-key`: o header `X-Internal-Key` entre a Lambda e o os-service.

A chave é gerada na hora e não aparece na tela:

```bash
for s in oficina/jwt oficina/internal-key; do
  aws secretsmanager create-secret --name $s --region $AWS_REGION \
    --secret-string "{\"key\":\"$(openssl rand -hex 32)\"}" --query Name --output text
done
```

## 2. Usuário IAM do CI e configuração do GitHub

O GitHub Actions não consegue usar OIDC nesta conta. Os workflows aceitam as duas formas:

- **Nesta conta:** os secrets `AWS_ACCESS_KEY_ID` e `AWS_SECRET_ACCESS_KEY`, de um usuário IAM só do CI.
- **Numa conta sem a restrição:** o `AWS_DEPLOY_ROLE_ARN`.

Cadastre só uma das duas formas.

O trecho abaixo cria o usuário e grava a chave direto nos 6 repositórios, sem passar pela tela.
A AWS mostra a chave secreta uma vez só.

```bash
REPOS="oficina-os-service oficina-billing-service oficina-execution-service oficina-lambda-auth oficina-infra-k8s oficina-infra-database"
aws iam create-user --user-name github-actions-oficina
aws iam attach-user-policy --user-name github-actions-oficina \
  --policy-arn arn:aws:iam::aws:policy/AdministratorAccess
read -r ID SEGREDO < <(aws iam create-access-key --user-name github-actions-oficina \
  --query 'AccessKey.[AccessKeyId,SecretAccessKey]' --output text)
for r in $REPOS; do
  printf %s "$ID" | gh secret set AWS_ACCESS_KEY_ID -R Duds27/$r
  printf %s "$SEGREDO" | gh secret set AWS_SECRET_ACCESS_KEY -R Duds27/$r
done
unset ID SEGREDO
```

**Variables.** São da região e do estado. As outras de cada serviço saem dos *outputs* dos applies (passo 4).

```bash
for r in $REPOS; do gh variable set AWS_REGION -R Duds27/$r --body us-east-2; done
for r in oficina-billing-service oficina-execution-service oficina-lambda-auth oficina-infra-k8s oficina-infra-database; do
  gh variable set TF_STATE_BUCKET -R Duds27/$r --body $TF_STATE_BUCKET
  gh variable set TF_LOCK_TABLE -R Duds27/$r --body $TF_LOCK_TABLE
done
# Admins do EKS além de quem aplicar o infra-k8s: o usuário do CI e o usuário local do kubectl.
gh variable set EKS_ADMIN_ARNS -R Duds27/oficina-infra-k8s \
  --body "[\"arn:aws:iam::$ACCOUNT:user/github-actions-oficina\",\"arn:aws:iam::$ACCOUNT:user/$(aws sts get-caller-identity --query Arn --output text | cut -d/ -f2)\"]"
```

## 3. Plataforma: infra-k8s e infra-database

O primeiro apply roda daqui, para ver os erros na hora. Os seguintes podem ir pelo CD (push em `homolog`).
Quem aplica o infra-k8s vira admin do cluster. A lista `cluster_admin_arns` acrescenta os outros
(o criador é tirado dela sozinho), por isso é seguro passar os dois usuários.

```bash
cd oficina-infra-k8s
terraform init -backend-config="bucket=$TF_STATE_BUCKET" -backend-config="key=infra-k8s/$ENV/terraform.tfstate" \
  -backend-config="region=$AWS_REGION" -backend-config="dynamodb_table=$TF_LOCK_TABLE"
K8S_VARS=(-var="environment=$ENV" -var='node_instance_types=["t3.small"]'
  -var="cluster_admin_arns=[\"arn:aws:iam::$ACCOUNT:user/github-actions-oficina\"]")
terraform apply -target=module.eks "${K8S_VARS[@]}"   # 1º: VPC e EKS (o provider kubectl precisa do cluster)
terraform apply "${K8S_VARS[@]}"                      # 2º: add-ons, RabbitMQ, namespaces e NLB
terraform output
cd ../oficina-infra-database
terraform init -backend-config="bucket=$TF_STATE_BUCKET" -backend-config="key=infra-database/$ENV/terraform.tfstate" \
  -backend-config="region=$AWS_REGION" -backend-config="dynamodb_table=$TF_LOCK_TABLE"
terraform apply -var="environment=$ENV" -var="state_bucket=$TF_STATE_BUCKET"
cd ..
```

- O infra-k8s sobe em dois applies. No primeiro, só a VPC e o EKS (`-target=module.eks`), porque
  o provider `kubectl` não aceita o endpoint do cluster ainda desconhecido. O CD faz o mesmo quando o
  cluster não existe.
- O infra-k8s leva de 15 a 20 minutos e o RDS cerca de 10.
- Com `t3.micro`, o pod do RabbitMQ não cabe. A conta está no [README do infra-k8s](https://github.com/Duds27/oficina-infra-k8s#capacidade-dos-nós-decidir-antes-do-apply).

**Acesso ao cluster**, num kubeconfig separado para não mexer no contexto do kind local:

```bash
aws eks update-kubeconfig --name oficina-$ENV --region $AWS_REGION --kubeconfig ~/.kube/oficina-eks
export KUBECONFIG=~/.kube/oficina-eks
kubectl get nodes
aws eks list-pod-identity-associations --cluster-name oficina-$ENV --query 'associations[].[namespace,serviceAccount]' --output text
```

Até aqui, a lista do Pod Identity mostra o `kube-system` do LB controller e do EBS CSI.

## 4. Infra dos serviços, Mercado Pago e variables do CD

**`infra/` do billing e do execution.** Pelo `infra.yml` (push na `main` com aprovação no Environment `infra`) ou daqui:

```bash
for s in billing execution; do
  (cd oficina-$s-service/infra &&
   terraform init -backend-config="bucket=$TF_STATE_BUCKET" -backend-config="key=$s-service/$ENV/terraform.tfstate" \
     -backend-config="region=$AWS_REGION" -backend-config="dynamodb_table=$TF_LOCK_TABLE" &&
   terraform apply -var="environment=$ENV" -var="state_bucket=$TF_STATE_BUCKET")
done
```

O apply do execution cria a associação do Pod Identity em `oficina-execution/execution-service`.

**Mercado Pago (sandbox).** O `infra/` do billing cria o secret vazio. Preencha com as credenciais de
teste da conta vendedora. O `read -s` não mostra nem grava no histórico:

```bash
read -rs -p "Access token de teste: " MP_TOKEN; echo
read -rs -p "Segredo do webhook: " MP_SEGREDO; echo
aws secretsmanager put-secret-value --secret-id oficina-$ENV/billing/mercadopago \
  --secret-string "{\"accessToken\":\"$MP_TOKEN\",\"webhookSecret\":\"$MP_SEGREDO\"}" --query Name --output text
unset MP_TOKEN MP_SEGREDO
```

**Variables do CD.** Cada README, na seção CD, diz de qual *output* sai cada uma:

| Repositório | Variables |
| --- | --- |
| [os-service](../README.md#cd) | `ECR_REPOSITORY` (`oficina-homolog-os-service`), `EKS_CLUSTER_NAME`, `TARGET_GROUP_ARN`, `RABBITMQ_SECRET_ID`, `DB_SECRET_ID`, `JWT_SECRET_ID` (`oficina/jwt`), `INTERNAL_KEY_SECRET_ID` (`oficina/internal-key`) |
| [billing-service](https://github.com/Duds27/oficina-billing-service#cd) | as mesmas, sem a chave interna, mais `MP_SECRET_ID` e `API_BASE_URL` (do passo 5) |
| [execution-service](https://github.com/Duds27/oficina-execution-service#cd) | as mesmas, sem banco SQL, mais `DYNAMODB_TABLE`. A role do pod vem do Pod Identity, então não há `IRSA_ROLE_ARN` |
| [lambda-auth](https://github.com/Duds27/oficina-lambda-auth#deploy) | `INTERNAL_KEY_SECRET_ID` e, opcional, `OS_SERVICE_URL`; o secret `JWT_SECRET_ID` (`oficina/jwt`) |

## 5. Deploy

1. **Branches.** Crie `homolog` e `prod` a partir da `main` em cada repositório. Depois, só PR `main → homolog`.
2. **Lambda.** Push em `homolog` no `oficina-lambda-auth`. A saída `api_endpoint` vira o `API_BASE_URL` do billing e o `authUrl` do Postman.
3. **Serviços.** PR `main → homolog` em cada um. O CD builda, publica no ECR e aplica o overlay `aws`.
4. **Conferir:**

   ```bash
   kubectl get pods -A | grep oficina
   kubectl -n oficina-execution exec deploy/execution-service -- printenv AWS_CONTAINER_CREDENTIALS_FULL_URI
   ```

   O segundo comando mostra a URL do agente do Pod Identity, e o execution-service acessa o DynamoDB sem chave.

5. **Seed:**

   ```bash
   python scripts/seed.py --url <api_endpoint>
   ```

6. **Validação:** a parte do New Relic e o roteiro do vídeo seguem na [6.0](fase4/etapa-6-documentacao-entrega.md#60-implantação-na-aws-e-validação-no-new-relic) e em [`roteiro-video-fase4.md`](roteiro-video-fase4.md).

## Custo

O EKS, o NAT, o NLB e os RDS cobram por hora. A estimativa, com preços de tabela, é de US$ 5 a 6 por dia
com tudo ligado, o que acaba com os US$ 100 de crédito em cerca de 2 semanas.

| Item | Estimativa por dia |
| --- | --- |
| EKS (control plane) | ~ US$ 2,40 |
| 2 × `t3.small` | ~ US$ 1,00 |
| NAT Gateway (mais os dados) | ~ US$ 1,10 |
| NLB interno | ~ US$ 0,55 |
| 2 × RDS micro (MySQL e PostgreSQL) | ~ US$ 0,80 |

Ligue para testar e gravar, e destrua em seguida. Os créditos e o fim do plano aparecem com:

```bash
aws freetier get-account-plan-state --region us-east-1
```

## Teardown

Na ordem inversa. Os recursos dos serviços saem antes do infra-k8s, para o LB controller ainda estar
lá e limpar os `TargetGroupBinding`.

```bash
for d in tech-challenge-fiap oficina-billing-service oficina-execution-service; do
  kubectl delete -k $d/k8s/overlays/aws --ignore-not-found
done
```

Depois, os `terraform destroy`, com os mesmos `-var` do apply:

1. `oficina-lambda-auth/terraform`;
2. o `infra/` do execution e do billing;
3. `oficina-infra-database`;
4. `oficina-infra-k8s`.

O bucket do estado, a tabela de lock, o ECR do os-service e os segredos `oficina/*` ficam. Apague à mão
se não houver mais uso.
