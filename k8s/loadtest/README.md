# Teste de carga — demonstração do HPA

Dois geradores de carga para mostrar a **escalabilidade automática** (o HPA
escalando os pods do os-service sob CPU: de 1 a 5 réplicas no kind, de 1 a 3 no EKS).

Em um terminal, acompanhe o autoscaling:

```bash
kubectl -n oficina-os get hpa os-service -w
```

## Opção A — dentro do cluster (não precisa de nada instalado)

```bash
kubectl apply -f k8s/loadtest/load-generator.yaml
# ... observe as réplicas subirem ...
kubectl -n oficina-os delete -f k8s/loadtest/load-generator.yaml   # para a carga
```

Para mais pressão, aplique o manifesto mais de uma vez com nomes diferentes
(edite `metadata.name`) ou aumente o número de loops no `command`.

## Opção B — a partir do host (k6)

Requer [k6](https://k6.io/) instalado e um `port-forward` ativo:

```bash
kubectl -n oficina-os port-forward svc/os-service 8080:80   # em outro terminal
k6 run k8s/loadtest/load-test.js
# alvo alternativo:
k6 run -e BASE_URL=http://localhost:8080 k8s/loadtest/load-test.js
```

## O que observar

```
NAME         REFERENCE               TARGETS         MINPODS  MAXPODS  REPLICAS
os-service   Deployment/os-service   cpu: 180%/70%   1        5        1 -> 2 -> 4 ...
```

- As métricas levam ~15–30 s para refletir (metrics-server + intervalo do HPA).
- `TARGETS = <unknown>/70%` significa que o metrics-server ainda não coletou
  métricas — verifique `kubectl -n kube-system get deploy metrics-server`.
- O **scale-down é lento de propósito** (`stabilizationWindowSeconds: 120` no HPA):
  após parar a carga, as réplicas caem gradualmente ao longo de alguns minutos.
