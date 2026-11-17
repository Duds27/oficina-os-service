# ADR-005 — Divisão em três microsserviços

- **Status:** Proposto: implementado e validado na Fase 4 (resultado no fim); aguarda a revisão do grupo para passar a *Aceito*
- **Data:** 2026-11-15
- **Relaciona-se com:** [ADR-003](ADR-003-quatro-repositorios.md) (substitui em parte: `oficina-app` vira `oficina-os-service` e surgem mais dois repositórios)

## Contexto

A Fase 4 pede que o monólito vire microsserviços:

- Cada serviço tem banco próprio.
- Nenhum serviço acessa o banco de outro.
- Os serviços conversam por mensagens ou APIs.

O monólito atual (Clean Architecture, .NET 8) concentra cadastro, estoque, ciclo de vida da OS, orçamento e notificação.

## Decisão

Dividir por área de negócio, seguindo o caminho da OS na oficina (atendimento → diagnóstico/execução → cobrança):

| Serviço | Responsabilidade | Banco |
| --- | --- | --- |
| `os-service` | Clientes, veículos, catálogo de serviços e peças (com estoque), abertura e consulta de OS, histórico e **orquestração da saga** | MySQL 8.4 (RDS atual) |
| `billing-service` | Orçamento (geração, envio, aprovação/recusa), pagamento no Mercado Pago e estorno | PostgreSQL 16 (RDS novo) |
| `execution-service` | Fila de trabalho, diagnóstico, reparo e linha do tempo da execução | DynamoDB |

- Cada serviço tem repositório, pipeline e deploy próprios.
- **Catálogo e estoque ficam no `os-service`:** a reserva de peças já acontece ali (débito ao adicionar item, devolução ao remover). Assim, a compensação "devolver peças" é uma transação local, sem mensageria.
- **Dados de outro contexto chegam dentro das mensagens** (ex.: `GerarOrcamento` leva nome e e-mail do cliente e os itens com preço). Cada serviço guarda a cópia de que precisa. Ver [contratos de mensagens](../../contratos-mensagens.md).
- **A Lambda de autenticação deixa de ler o banco do `os-service`.** Ela passa a chamar `GET /internal/clientes/por-cpf/{cpf}`.

## Consequências

- **Positivas:**
  - Deploy e escala independentes por serviço.
  - Falha no pagamento não derruba o cadastro nem a fila da oficina.
  - O `os-service` aproveita quase todo o código e os testes do monólito.
  - Cada frente do grupo trabalha num repositório.
- **Negativas / mitigação:**
  - **Consistência eventual entre serviços:** status intermediários explícitos na OS ("Aguardando Pagamento") e histórico de status consultável.
  - **Dados duplicados** (snapshot do cliente no orçamento): aceitável, porque o orçamento precisa registrar os dados do momento em que foi emitido.
  - **Mais repositórios e pipelines para manter:** mesmo esqueleto e mesmo CI nos três serviços.
  - **Custo de mais um RDS:** fallback de database lógica no RDS existente, ver [ADR-008](ADR-008-persistencia-poliglota.md).

## Alternativas descartadas

- **Quatro serviços, com catálogo/estoque separado:** a reserva de peças viraria mais uma etapa da saga, com mais uma compensação distribuída, sem ganho de negócio para o tamanho do projeto.
- **Divisão por camada técnica** (ex.: "serviço de notificação", "serviço de dados"): gera dependência síncrona entre serviços em quase toda requisição.
- **Monólito modular:** não atende ao requisito de bancos e deploys independentes.

## Resultado da implementação (Fase 4)

- Os três serviços existem como repositórios próprios, cada um com o seu banco, Dockerfile, manifestos Kubernetes, CI (testes, cobertura ≥ 80%, SonarCloud) e CD:
  - [`oficina-os-service`](https://github.com/Duds27/oficina-os-service);
  - [`oficina-billing-service`](https://github.com/Duds27/oficina-billing-service);
  - [`oficina-execution-service`](https://github.com/Duds27/oficina-execution-service).
- A conversa entre eles é só por mensagens (ADR-007), mais um endpoint interno do os-service para a Lambda de autenticação. Nenhum lê o banco do outro.
- A saga completa rodou nos três: no `docker-compose.full.yml`, num kind com um namespace por serviço e num runner limpo do GitHub (`e2e.yml`).
