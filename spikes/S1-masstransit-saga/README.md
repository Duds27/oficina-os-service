# Spike S1 — MassTransit + saga persistida no MySQL

> [Etapa 0, item 0.5](../../docs/fase4/etapa-0-fundacao.md) · Executado em 2026-11-15 · **Resultado: aprovado, com uma restrição de versão**

## Pergunta

1. A state machine do MassTransit, persistida no MySQL (EF Core, concorrência otimista), funciona com o `AppDbContext` atual, junto com o outbox?
2. O `[EntityName]` faz produtor e consumidor com namespaces diferentes usarem o mesmo exchange?
3. A imagem `rabbitmq:4.1-management` dos composes funciona com o MassTransit 8.x?

## Como rodar

```bash
dotnet test spikes/S1-masstransit-saga
```

- Precisa do Docker ligado. Os Testcontainers sobem `mysql:8.4` e `rabbitmq:4.1-management`.
- Leva cerca de 40 s depois de as imagens estarem baixadas.
- O projeto fica fora da solução principal e não roda no CI.

## Montagem

- **Lado "os":**
  - Usa o `AppDbContext` real do `OficinaMecanica.Infrastructure`. As tabelas do MassTransit e da saga entram no modelo por um `IModelCustomizer`, sem alterar a classe.
  - Repositório EF da saga com `ConcurrencyMode.Optimistic` e outbox com `UseMySql()` + `UseBusOutbox()`.
  - O saga endpoint se chama `os-ordem-servico-saga`.
- **Lado "execution":** outro host e outro bus. Os contratos ficam em **outro namespace** (`Spike.Execution.Contracts`) e o consumer escuta a fila `execution-solicitar-diagnostico`.
- **Fluxo testado:**
  1. O caso de uso grava Cliente + Veículo + OS e publica `OrdemServicoAberta` pelo outbox, na mesma transação.
  2. A saga nasce no MySQL, carrega a OS no mesmo `DbContext` e envia `SolicitarDiagnostico`.
  3. O execution responde com `DiagnosticoIniciado`.
  4. A saga vai para `EmDiagnostico` e muda o status da OS na mesma transação.

## Resultados (6 testes, todos verdes)

| Teste | Resultado |
| --- | --- |
| Abertura da OS cria a saga no MySQL e avança com o evento do outro serviço | ✅ Saga em `EmDiagnostico`, OS em `EmDiagnostico`, comando desserializado no tipo do outro namespace |
| Sem `SaveChanges`, nenhuma mensagem sai do outbox | ✅ Nada entregue em 5 s |
| `RowVersion` no MySQL detecta atualização concorrente | ✅ `DbUpdateConcurrencyException` na segunda gravação |
| Sem `[EntityName]`, mensagem entre namespaces diferentes não chega | ✅ Exchanges diferentes (`Spike.Os.Contracts.SemEntityName:Ping` e `Spike.Execution.Contracts.SemEntityName:Ping`) e a mensagem se perde **sem erro** |
| Exchanges usam o `EntityName` e o envelope não vira exchange | ✅ `oficina.ordem-servico-aberta.v1` etc. O `[ExcludeFromTopology]` evita exchanges de `MensagemOS`/`IMensagemOS` |
| MassTransit não usa recurso depreciado do RabbitMQ 4.1 | ✅ Nenhum aviso de `transient_nonexcl_queues`. O único aviso é `management_metrics_collection`, do plugin de management |

## Conclusões e decisões

1. **Versão fixada em `8.4.1`, não "a última 8.x".**
   - A partir da 8.5.0, o `MassTransit.EntityFrameworkCore` exige `Microsoft.EntityFrameworkCore.Relational` **9.0.1**, mesmo para net8.0.
   - O `os-service` usa EF Core 8.0.12 com o Pomelo 8.0.2, que não aceita EF Core 9.
   - A 8.4.1 tem licença Apache-2.0, depende de `Microsoft.Extensions.*` 8.0 e de `RabbitMQ.Client` 7.1.2, e compila sem conflito.
   - **Vale para os três serviços**, porque o billing usa Npgsql com EF Core 8.
   - Para ir para a 8.5.x, seria preciso migrar para EF Core 9 (Pomelo 9 e Npgsql 9). Fica fora do escopo da fase.
2. **`[EntityName]` é obrigatório:** o `[MessageUrn]` sozinho não define o nome do exchange. Confirma a convenção de [`docs/contratos-mensagens.md`](../../docs/contratos-mensagens.md#12-nomes-urn-e-exchange).
3. **Concorrência otimista no MySQL:** `DateTime RowVersion` com `.IsRowVersion()`. O Pomelo gera `timestamp(6) ... ON UPDATE CURRENT_TIMESTAMP(6)`.
4. **Activities usam o mesmo `DbContext` da saga** (`context.GetServiceOrCreateInstance<AppDbContext>()`). As mudanças na OS são gravadas na mesma transação do estado da saga e do outbox.
5. **Envio de comandos:** endereço explícito (`GetSendEndpoint(new Uri("queue:execution-solicitar-diagnostico"))`) em vez de `EndpointConvention`, que é estático e global ao processo.
6. **RabbitMQ 4.1 aprovado para os composes.** As métricas do management estão depreciadas no 4.x, o que reforça o uso do plugin Prometheus na Etapa 5.

## O que muda na Etapa 1

- Pacotes `MassTransit`, `MassTransit.RabbitMQ` e `MassTransit.EntityFrameworkCore` em **8.4.1**.
- No `AppDbContext.OnModelCreating`: `AddInboxStateEntity()`, `AddOutboxMessageEntity()`, `AddOutboxStateEntity()` e o `SagaClassMap` da saga (como em `SagaModelCustomizer`). Em seguida, uma migration nova.
- A transação de `CriarAsync`:
  1. Abrir a transação.
  2. `SaveChanges`, para gerar o `Id` da OS.
  3. `Publish(OrdemServicoAberta)`.
  4. `SaveChanges` de novo.
  5. Commit.
