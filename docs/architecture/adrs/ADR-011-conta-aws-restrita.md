# ADR-011 — Conta AWS restrita: `us-east-2`, EKS Pod Identity e chave de acesso no CI

- **Status:** Proposto: aguarda a revisão do grupo para passar a *Aceito*
- **Data:** 2026-11-17
- **Relaciona-se com:** [ADR-008](ADR-008-persistencia-poliglota.md) (acesso ao DynamoDB) e [RFC-001](../rfcs/RFC-001-escolha-nuvem.md) (OIDC do GitHub Actions)

## Contexto

A conta AWS usada na Fase 4 é do **Free plan** e fica numa organização gerenciada pela própria AWS.
As SCPs dessa organização valem acima do IAM, inclusive da `AdministratorAccess`, e não podem ser
vistas nem alteradas de dentro da conta. Em 2026-11-17, com leituras e com o simulador do IAM, que
considera as SCPs:

- **Região:** só a `us-east-2` é liberada. Nas outras regiões, EC2, EKS, RDS, ECR, Lambda, API Gateway, ELB, Secrets Manager, DynamoDB e KMS são negados.
- **OIDC no IAM:** `iam:CreateOpenIDConnectProvider` é negado em qualquer região.
- **O que passa na `us-east-2`:** todas as ações de criação que o projeto usa, inclusive `eks:CreatePodIdentityAssociation`.

O projeto dependia de OIDC em dois pontos:

- o **IRSA**, para as roles dos pods: o LB controller, o EBS CSI e o acesso do execution-service ao DynamoDB;
- o **login do GitHub Actions** na AWS (`role-to-assume`).

E estava todo na `us-east-1`.

## Decisão

1. **Região `us-east-2`** como padrão de `aws_region` nos Terraform e em `AWS_REGION` no GitHub.
2. **EKS Pod Identity no lugar do IRSA.**
   - **No cluster:** `enable_irsa = false` e o add-on `eks-pod-identity-agent`.
   - **Roles do LB controller e do EBS CSI:** módulo `terraform-aws-modules/eks-pod-identity`.
   - **Role do execution-service:** trust em `pods.eks.amazonaws.com` e uma `aws_eks_pod_identity_association` só para a ServiceAccount `oficina-execution/execution-service`.
   - **Sem anotação** `eks.amazonaws.com/role-arn` nas ServiceAccounts, e sem a variable `IRSA_ROLE_ARN` no CD.
3. **CI com chave de acesso ou OIDC.**
   - **Nesta conta:** o passo `configure-aws-credentials` dos 6 repositórios usa a chave de um usuário IAM só do CI (secrets `AWS_ACCESS_KEY_ID` e `AWS_SECRET_ACCESS_KEY`).
   - **Numa conta sem a restrição:** o mesmo passo aceita o `AWS_DEPLOY_ROLE_ARN`.
   - Cadastra-se só uma das duas formas.
4. **Admins do cluster:** além de quem aplica o infra-k8s, os ARNs de `cluster_admin_arns` (variable `EKS_ADMIN_ARNS` no CD) viram *access entries* com a `AmazonEKSClusterAdminPolicy`. Assim, o usuário do CI e o do `kubectl` local podem ser diferentes.

## Consequências

- **Positivas:**
  - o projeto sobe numa conta gratuita;
  - o Pod Identity é a forma atual recomendada pela AWS para o EKS e também funciona em contas sem restrição;
  - o código dos serviços não muda, porque o AWS SDK v4 usa a cadeia padrão de credenciais, que inclui o agente do Pod Identity.
- **Negativas:**
  - uma chave de acesso de longa duração fica guardada nos secrets do GitHub. É preciso girá-la e apagá-la quando a avaliação terminar;
  - o usuário do CI tem `AdministratorAccess`, porque os Terraform criam rede, EKS e IAM;
  - os créditos (US$ 100) acabam em cerca de 2 semanas com tudo ligado. O ambiente só fica de pé para testar e gravar.

## Alternativas consideradas

- **Pedir a liberação da SCP:** não é possível, porque a organização é da AWS.
- **Outra conta sem organização:** resolveria o OIDC, mas o grupo decidiu adaptar o projeto a esta conta.
- **Chave de acesso no pod do execution-service:** descartada. É pior que o Pod Identity em segurança e em rotação.

O passo a passo está em [`aws-setup-fase4.md`](../../aws-setup-fase4.md).
