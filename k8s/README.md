# Manifestos Kubernetes — os-service

Kustomize com uma base e dois overlays. O serviço roda no namespace `oficina-os` e fala com o
RabbitMQ em `rabbitmq.messaging.svc.cluster.local`, o mesmo DNS no kind e no EKS.

| Pasta | O que tem |
| --- | --- |
| `base/` | `Deployment` (2 réplicas, probes de startup, readiness e liveness, requests/limits, `envFrom` do ConfigMap e do Secret, ServiceAccount, `preStop` e `terminationGracePeriodSeconds: 45`), `Service` ClusterIP, `HPA` (CPU 70% / memória 80%, de 2 a 5), `ConfigMap` e `ServiceAccount` |
| `overlays/local/` | kind: imagem `oficina-os-service:local`, namespace, Secret de desenvolvimento, MySQL (StatefulSet) e o RabbitMQ de desenvolvimento no namespace `messaging`, usado também pelos overlays locais do billing e do execution. 1 réplica, HPA de 1 a 5 |
| `overlays/aws/` | EKS: imagem do ECR, `TargetGroupBinding` no target group do serviço no NLB interno, e o ConfigMap com o ambiente e os ajustes do OpenTelemetry para o New Relic. 1 réplica, HPA de 1 a 3 (custo em homolog) |

## Decisões

- **Probes:** o `startupProbe` (`/health/live`, até 3 min) segura o liveness enquanto o
  serviço sobe. O readiness (`/health/ready`) confere banco e RabbitMQ, e o liveness
  (`/health/live`) só o processo.
- **Parada:** o `preStop` espera 10 s para o pod sair do Service e do target group do NLB.
  Depois vêm até 30 s de parada do host .NET, que deixa os consumers do MassTransit
  terminarem a mensagem em andamento. São 45 s no total.
- **Memória:** `DOTNET_gcServer=0` (GC de workstation). No kind, o pod ficou entre 80 e
  165 Mi, de 30% a 65% do request de 256 Mi, então o HPA não escala parado.
- **Segurança:** usuário 1654 (o `app` das imagens .NET), sem escalada de privilégio,
  sem capabilities, seccomp `RuntimeDefault`.
- **Namespace:** fica fora da base. Na AWS, quem cria é o `oficina-infra-k8s`. No kind, é o
  overlay local.

## Rodar no kind

```bash
docker build -t oficina-os-service:local OficinaMecanicaBackend
kind load docker-image oficina-os-service:local --name <cluster>
kubectl apply -k k8s/overlays/local
kubectl -n oficina-os rollout status deploy/os-service
kubectl -n oficina-os port-forward svc/os-service 8080:80   # http://localhost:8080/swagger
```

O HPA precisa do metrics-server. No kind, use
`kubectl apply -f https://github.com/kubernetes-sigs/metrics-server/releases/download/v0.7.2/components.yaml`
com o argumento `--kubelet-insecure-tls`.

## Na AWS

O CD (`.github/workflows/cd.yml`) faz o deploy:

1. cria o Secret `os-service-secrets` a partir do Secrets Manager;
2. troca a imagem e os placeholders (`__TARGET_GROUP_ARN__` e os demais);
3. roda `kubectl apply -k k8s/overlays/aws` e espera o rollout.

Validado em 2026-11-16 num cluster kind com os três serviços, cada um no seu namespace,
e o broker em `messaging`. A saga completa passou: OS aberta, diagnóstico, orçamento,
aprovação, pagamento simulado, execução e `Concluida`.

## Teste de carga

[`loadtest/`](loadtest/) tem um gerador de carga dentro do cluster e um script k6 para mostrar o
HPA escalando.

## Mudança da Fase 3 para a 4

O namespace `oficina` e os objetos `api`, `api-config`, `api-secrets` e `api-tgb` viraram
`oficina-os` e `os-service`, `os-service-config`, `os-service-secrets` e o TargetGroupBinding
`os-service`. É o mesmo padrão do billing e do execution. O target group também é outro (um
por serviço no NLB), então o primeiro deploy da Fase 4 cria tudo do zero. Depois dele, apague
o namespace `oficina` antigo.
