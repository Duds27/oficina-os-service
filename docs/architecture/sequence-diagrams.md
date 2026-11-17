# Diagramas de sequência

Fase 4: a saga da OS nos 3 serviços. Os nomes das mensagens são os do
[catálogo de contratos](../contratos-mensagens.md), e a máquina de estados completa (com os eventos
fora de ordem) está na seção 4 dele. Os comandos vão para a fila do serviço de destino; os eventos são
publicados e a saga do os-service os consome. Todas as mensagens saem por outbox, na mesma transação
da mudança de estado.

## 1. Caminho feliz

```mermaid
sequenceDiagram
    autonumber
    actor At as Atendente / cliente
    participant OS as os-service (saga)
    participant MQ as RabbitMQ
    participant EX as execution-service
    participant BI as billing-service
    participant MP as Mercado Pago

    At->>OS: POST /api/ordens-servico (+ itens)
    OS->>OS: OrdemServicoAberta → saga AguardandoDiagnostico
    OS->>MQ: SolicitarDiagnostico
    MQ->>EX: SolicitarDiagnostico (execução na fila)
    At->>EX: POST /diagnostico/iniciar
    EX->>MQ: DiagnosticoIniciado
    MQ->>OS: saga EmDiagnostico
    At->>EX: POST /diagnostico/concluir
    EX->>MQ: DiagnosticoConcluido
    MQ->>OS: saga AguardandoOrcamento
    OS->>MQ: GerarOrcamento (itens e cliente da OS)
    MQ->>BI: GerarOrcamento
    BI->>BI: orçamento Enviado (e-mail ao cliente)
    BI->>MQ: OrcamentoGerado
    MQ->>OS: saga AguardandoAprovacao (prazo)
    At->>BI: POST /api/orcamentos/{id}/aprovacao { aprovado: true }
    BI->>MP: cria a preferência (link de pagamento)
    BI->>MQ: OrcamentoAprovado (link)
    MQ->>OS: saga AguardandoPagamento (prazo)
    MP-->>BI: webhook POST /webhooks/mercadopago (assinatura validada)
    BI->>MP: consulta o pagamento
    BI->>MQ: PagamentoConfirmado
    MQ->>OS: saga AguardandoExecucao
    OS->>MQ: IniciarExecucao (serviços aprovados)
    MQ->>EX: IniciarExecucao
    EX->>MQ: ExecucaoIniciada
    MQ->>OS: saga EmExecucao
    At->>EX: POST /reparo/iniciar e /finalizar
    EX->>MQ: ExecucaoFinalizada (duração)
    MQ->>OS: saga Concluida, OS Finalizada
```

## 2. C1: orçamento recusado

```mermaid
sequenceDiagram
    autonumber
    actor Cl as Cliente
    participant BI as billing-service
    participant MQ as RabbitMQ
    participant OS as os-service (saga)
    participant EX as execution-service

    Note over OS: saga AguardandoAprovacao
    Cl->>BI: POST /api/orcamentos/{id}/aprovacao { aprovado: false }
    BI->>BI: orçamento Recusado
    BI->>MQ: OrcamentoRecusado
    MQ->>OS: OrcamentoRecusado
    OS->>OS: devolve as peças ao estoque, OS Cancelada (mesma transação)
    OS->>MQ: CancelarExecucao
    MQ->>EX: execução Cancelada
    Note over OS: saga Cancelada
```

## 3. C2: pagamento recusado ou prazo vencido

```mermaid
sequenceDiagram
    autonumber
    participant MP as Mercado Pago
    participant BI as billing-service
    participant MQ as RabbitMQ
    participant OS as os-service (saga)
    participant EX as execution-service

    Note over OS: saga AguardandoPagamento (ou AguardandoAprovacao)
    alt pagamento recusado
        MP-->>BI: webhook (pagamento recusado)
        BI->>MQ: PagamentoRecusado
        MQ->>OS: PagamentoRecusado
    else prazo de aprovação ou de pagamento vencido
        OS->>OS: VerificadorPrazosSaga publica PrazoExpirado
    end
    OS->>MQ: CancelarOrcamento
    MQ->>BI: orçamento e pagamento pendente Cancelados
    OS->>OS: devolve as peças, OS Cancelada
    OS->>MQ: CancelarExecucao
    MQ->>EX: execução Cancelada
    Note over OS: saga Cancelada
```

## 4. C3: execução rejeitada, com estorno

```mermaid
sequenceDiagram
    autonumber
    participant EX as execution-service
    participant MQ as RabbitMQ
    participant OS as os-service (saga)
    participant BI as billing-service
    participant MP as Mercado Pago

    Note over OS: saga AguardandoExecucao (pagamento já confirmado)
    OS->>MQ: IniciarExecucao
    MQ->>EX: IniciarExecucao
    EX->>MQ: ExecucaoRejeitada (ex.: fault injection)
    MQ->>OS: ExecucaoRejeitada
    OS->>MQ: EstornarPagamento
    OS->>OS: devolve as peças, OS Cancelada
    Note over OS: saga AguardandoEstorno
    MQ->>BI: EstornarPagamento
    BI->>MP: estorno (chave de idempotência = id do pagamento)
    BI->>BI: pagamento Estornado, orçamento Cancelado
    BI->>MQ: PagamentoEstornado
    MQ->>OS: PagamentoEstornado
    Note over OS: saga Cancelada
    Note over BI: estorno que falha de vez vai para a fila _error<br/>e dispara o alerta crítico (intervenção manual)
```

O prazo da execução (`AguardandoExecucao` sem `ExecucaoIniciada`) segue o mesmo caminho do C3, e a saga
também manda `CancelarExecucao`.

---

## Fase 3 (histórico): diagramas de sequência

A Fase 3 tinha um serviço só (a API .NET num Deployment do EKS) e um RDS MySQL.

### 1. Autenticação por CPF

```mermaid
sequenceDiagram
    autonumber
    participant C as Cliente
    participant GW as API Gateway
    participant L as Lambda auth
    participant SM as Secrets Manager
    participant DB as RDS MySQL

    C->>GW: POST /auth { cpf }
    GW->>L: invoca (AWS_PROXY)
    L->>L: valida CPF (formato + dígitos verificadores)
    alt CPF inválido
        L-->>C: 400 { error: "CPF inválido." }
    else CPF válido
        L->>SM: GetSecretValue (chave JWT + creds DB) [cache]
        L->>DB: SELECT Id, Nome, CpfCnpj, Ativo WHERE CpfCnpj = ?
        alt cliente não encontrado
            L-->>C: 401 { error: "Cliente não encontrado." }
        else cliente inativo
            L-->>C: 403 { error: "Cliente inativo." }
        else cliente ativo
            L->>L: emite JWT HS256 (sub, cpf, role=Cliente, exp)
            L-->>C: 200 { token, expiresAt }
        end
    end
```

### 2. Abertura de Ordem de Serviço (rota protegida)

```mermaid
sequenceDiagram
    autonumber
    participant C as Cliente
    participant GW as API Gateway
    participant AZ as Lambda Authorizer
    participant API as API .NET (EKS)
    participant DB as RDS MySQL
    participant NR as New Relic

    C->>GW: POST /api/ordens-servico (Bearer JWT)
    GW->>AZ: autoriza (valida JWT HS256)
    alt token inválido/expirado
        AZ-->>GW: isAuthorized=false
        GW-->>C: 401 Unauthorized
    else token válido
        AZ-->>GW: isAuthorized=true (context: clienteId, cpf)
        GW->>API: encaminha via VPC Link → NLB interno
        API->>API: valida payload + regras de domínio (status inicial = Recebida)
        API->>DB: INSERT OrdemServico
        API->>NR: métrica "os_criada" + log JSON (CorrelationId)
        API-->>C: 201 Created { id, numeroOS, status }
    end
```

> O app .NET mantém validação JWT Bearer própria (defesa em profundidade), usando a
> mesma chave/issuer/audience via Secrets Manager.
