# ADR-008 — Persistência poliglota (MySQL, PostgreSQL e DynamoDB)

- **Status:** Proposto (rascunho da Etapa 0; RDS separado para o billing ainda em decisão)
- **Data:** 2026-11-15
- **Relaciona-se com:** [RFC-002](../rfcs/RFC-002-escolha-banco.md) (escolha do MySQL na Fase 3)

## Contexto

Cada microsserviço ([ADR-005](ADR-005-microsservicos.md)) precisa de um banco próprio, sem acesso cruzado. Os padrões de acesso são diferentes:

- **Cadastro e OS:** relacional, com integridade referencial e unicidade.
- **Cobrança:** valores monetários e auditoria.
- **Execução:** fila e linha do tempo, acessadas por chave.

## Decisão

| Serviço | Banco | Por quê |
| --- | --- | --- |
| `os-service` | **MySQL 8.4** (RDS já provisionado) | Dados fortemente relacionais (cliente → veículo → OS → itens) e unicidade de CPF, placa e `NumeroOS`. Já está migrado. Também guarda o estado da saga e o outbox/inbox. |
| `billing-service` | **PostgreSQL 16** (RDS novo, `db.t4g.micro`) | Dinheiro exige ACID, constraints e auditoria. Mostra independência tecnológica entre serviços (EF Core + Npgsql). |
| `execution-service` | **DynamoDB** (*on-demand*) | A fila e a linha do tempo de apontamentos são acessadas por chave e têm esquema flexível. É gerenciado e sem servidor, com custo próximo de zero no volume do projeto. A escrita condicional dá idempotência. |

**Modelo do DynamoDB (*single-table*):**

- `PK = EXEC#{osId}`, `SK = META | EVT#{timestamp} | MSG#{messageId} | OUTBOX#...`.
- `GSI1` para a fila: `GSI1PK = FILA#{etapa}`, `GSI1SK = {dataEntrada}#{osId}`.
- TTL nos itens de deduplicação.

**Credenciais:**

- RDS: usuários próprios por serviço, guardados no Secrets Manager.
- DynamoDB: acesso via IRSA, com uma role restrita à tabela do serviço.

## Consequências

- **Positivas:**
  - Cada serviço usa o modelo que combina com o seu acesso.
  - O isolamento é físico: não há como um serviço ler o banco do outro.
  - Mostra o uso de bancos relacionais e NoSQL no mesmo sistema.
- **Negativas / mitigação:**
  - **Três tecnologias para operar:** todas gerenciadas pela AWS, com Terraform em cada repositório.
  - **Custo de dois RDS:** se apertar, criar uma *database* lógica no RDS existente, com usuário e *grants* restritos ao billing, e registrar como dívida técnica.
  - **Sem o outbox do MassTransit no DynamoDB:** outbox próprio com `TransactWriteItems` (deduplicação + agregado + mensagem de saída numa transação).
  - **Testes:** Testcontainers para MySQL e PostgreSQL. DynamoDB Local (ou LocalStack) para o execution.

## Alternativas descartadas

- **MySQL para todos (instâncias separadas):** menos variedade para operar, mas perde a justificativa técnica por serviço e a demonstração de independência tecnológica.
- **MongoDB / DocumentDB para o execution:** o DocumentDB tem custo mínimo alto. O MongoDB Atlas sairia da AWS e da rede privada.
- **Um banco compartilhado com schemas separados:** viola o requisito de nenhum acesso a banco de outro serviço.
