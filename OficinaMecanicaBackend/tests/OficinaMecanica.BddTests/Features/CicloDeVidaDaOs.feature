# language: pt
Funcionalidade: Ciclo de vida da Ordem de Serviço coordenado por Saga
  Para atender o cliente do diagnóstico à entrega
  Como oficina
  Quero que a saga coordene execução, orçamento e pagamento,
  e desfaça o que já foi feito quando um passo falha

  Contexto:
    Dado a peça "Filtro de óleo" com 10 unidades em estoque
    E uma OS aberta para um cliente com veículo cadastrado

  Cenário: OS percorre todo o fluxo com sucesso
    Quando o diagnóstico é concluído com 1 serviço e 1 peça
    E o cliente aprova o orçamento
    E o pagamento é confirmado pelo Mercado Pago
    E a execução é finalizada
    Então a OS deve estar "Finalizada"
    E o histórico deve conter as transições "EmDiagnostico, AguardandoAprovacao, AguardandoPagamento, EmExecucao, Finalizada"
    E a peça "Filtro de óleo" deve ter 9 unidades em estoque

  Cenário: Cliente recusa o orçamento
    Quando o diagnóstico é concluído com 1 serviço e 1 peça
    E o cliente recusa o orçamento
    Então a OS deve estar "Cancelada"
    E a peça "Filtro de óleo" deve ter 10 unidades em estoque
    E o execution-service deve receber o cancelamento da execução

  Cenário: Pagamento recusado
    Quando o diagnóstico é concluído com 1 serviço e 1 peça
    E o cliente aprova o orçamento
    E o pagamento é recusado pelo Mercado Pago
    Então o billing-service deve receber o cancelamento do orçamento
    E a OS deve estar "Cancelada"
    E a peça "Filtro de óleo" deve ter 10 unidades em estoque

  Cenário: Execução rejeitada após pagamento
    Dado que o execution-service vai rejeitar a execução
    Quando o diagnóstico é concluído com 1 serviço e 1 peça
    E o cliente aprova o orçamento
    E o pagamento é confirmado pelo Mercado Pago
    Então o billing-service deve receber o estorno do pagamento
    E a saga deve terminar em "Cancelada"
    E a OS deve estar "Cancelada"
    E a peça "Filtro de óleo" deve ter 10 unidades em estoque

  Cenário: Cliente não responde ao orçamento dentro do prazo
    Quando o diagnóstico é concluído com 1 serviço e 1 peça
    E o prazo de aprovação do orçamento expira
    Então o billing-service deve receber o cancelamento do orçamento
    E o execution-service deve receber o cancelamento da execução
    E a OS deve estar "Cancelada"
    E a peça "Filtro de óleo" deve ter 10 unidades em estoque
