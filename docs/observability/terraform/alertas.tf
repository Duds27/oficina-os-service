# Alertas da Etapa 5 (5.6). Duas políticas: "crítico" (exige ação imediata) e "alerta". O New
# Relic sempre pede um limiar crítico na condição, e a severidade fica na política.
locals {
  servicos = "service.name IN ('os-service', 'billing-service', 'execution-service')"
  ambiente = "deployment.environment = '${var.ambiente}'"

  # Condição → política, consulta NRQL e limiar.
  condicoes = {
    filas_error = {
      politica  = "critico"
      nome      = "Mensagens em fila _error"
      descricao = "Mensagem que esgotou os retries no RabbitMQ: o consumer não conseguiu processar."
      nrql      = "SELECT max(rabbitmq_queue_messages) FROM Metric WHERE queue LIKE '%_error' FACET queue"
      operador  = "above"
      limiar    = 0
      janela    = 60
      duracao   = 300
      ocorre    = "all"
    }
    estorno_falhou = {
      politica  = "critico"
      nome      = "Falha definitiva de estorno"
      descricao = "EstornarPagamento na fila _error: o cliente pagou e não foi estornado. Exige intervenção manual (README do billing-service)."
      nrql      = "SELECT max(rabbitmq_queue_messages) FROM Metric WHERE queue = 'billing-estornar-pagamento_error'"
      operador  = "above_or_equals"
      limiar    = 1
      janela    = 60
      duracao   = 60
      ocorre    = "at_least_once"
    }
    estorno_falhou_log = {
      politica  = "critico"
      nome      = "Falha definitiva de estorno (log do billing)"
      descricao = "O FalhaDeEstornoConsumer registrou o alerta crítico. Dispara antes da métrica da fila."
      nrql      = "SELECT count(*) FROM Log WHERE service.name = 'billing-service' AND message LIKE 'ALERTA: estorno%'"
      operador  = "above"
      limiar    = 0
      janela    = 60
      duracao   = 60
      ocorre    = "at_least_once"
    }
    compensacoes = {
      politica  = "alerta"
      nome      = "Muitas compensações da saga"
      descricao = "Mais de 5 sagas compensadas em uma hora (recusas, prazos, pagamentos recusados, rejeições)."
      nrql      = "SELECT sum(saga.compensada) FROM Metric WHERE service.name = 'os-service'"
      operador  = "above"
      limiar    = 5
      janela    = 3600
      duracao   = 3600
      ocorre    = "at_least_once"
    }
    webhook_mp = {
      politica  = "alerta"
      nome      = "Webhook do Mercado Pago com erro"
      descricao = "Mais de 3 respostas 4xx/5xx no webhook em 10 min: assinatura inválida, segredo trocado ou falha no billing."
      nrql      = "SELECT count(*) FROM Span WHERE service.name = 'billing-service' AND span.kind = 'server' AND http.route = 'webhooks/mercadopago' AND numeric(http.response.status_code) >= 400"
      operador  = "above"
      limiar    = 3
      janela    = 600
      duracao   = 600
      ocorre    = "at_least_once"
    }
    latencia = {
      politica  = "alerta"
      nome      = "Latência p95 alta por serviço"
      descricao = "p95 das requisições HTTP acima de 1 s por 5 min."
      nrql      = "SELECT percentile(duration.ms, 95) FROM Span WHERE span.kind = 'server' AND ${local.servicos} FACET service.name"
      operador  = "above"
      limiar    = 1000
      janela    = 60
      duracao   = 300
      ocorre    = "all"
    }
    saga_parada = {
      politica  = "alerta"
      nome      = "Saga parada além do prazo"
      descricao = "Saga com prazo vencido há mais de duas rodadas do verificador e sem PrazoExpirado (saga.prazos.atrasados)."
      nrql      = "SELECT max(saga.prazos.atrasados) FROM Metric WHERE service.name = 'os-service'"
      operador  = "above"
      limiar    = 0
      janela    = 60
      duracao   = 300
      ocorre    = "all"
    }
    falha_os = {
      politica  = "alerta"
      nome      = "Falha no processamento de OS"
      descricao = "Log Evento=os_falha_processamento do os-service (alerta da Fase 3, agora pelos logs OTLP)."
      nrql      = "SELECT count(*) FROM Log WHERE service.name = 'os-service' AND Evento = 'os_falha_processamento'"
      operador  = "above"
      limiar    = 0
      janela    = 60
      duracao   = 300
      ocorre    = "at_least_once"
    }
  }

  # Métricas dos serviços também filtram o ambiente; as do RabbitMQ (Prometheus) não têm o atributo.
  condicoes_com_ambiente = {
    for chave, c in local.condicoes : chave => merge(c, {
      nrql = strcontains(c.nrql, "service.name") ? replace(c.nrql, " WHERE ", " WHERE ${local.ambiente} AND ") : c.nrql
    })
  }
}

resource "newrelic_alert_policy" "oficina" {
  for_each = {
    critico = "Oficina Mecânica — crítico (${var.ambiente})"
    alerta  = "Oficina Mecânica — alerta (${var.ambiente})"
  }

  name                = each.value
  incident_preference = "PER_CONDITION_AND_TARGET"
}

resource "newrelic_nrql_alert_condition" "oficina" {
  for_each = local.condicoes_com_ambiente

  policy_id          = newrelic_alert_policy.oficina[each.value.politica].id
  name               = each.value.nome
  description        = each.value.descricao
  type               = "static"
  enabled            = true
  aggregation_window = each.value.janela
  aggregation_method = "event_flow"
  aggregation_delay  = 120

  nrql {
    query = each.value.nrql
  }

  critical {
    operator              = each.value.operador
    threshold             = each.value.limiar
    threshold_duration    = each.value.duracao
    threshold_occurrences = each.value.ocorre
  }
}

# ───────────────────── Notificação por e-mail (opcional) ─────────────────────
resource "newrelic_notification_destination" "email" {
  count = var.email_alertas == "" ? 0 : 1

  name = "Oficina Mecânica — e-mail"
  type = "EMAIL"

  property {
    key   = "email"
    value = var.email_alertas
  }
}

resource "newrelic_notification_channel" "email" {
  count = var.email_alertas == "" ? 0 : 1

  name           = "Oficina Mecânica — e-mail"
  type           = "EMAIL"
  destination_id = newrelic_notification_destination.email[0].id
  product        = "IINT"

  property {
    key   = "subject"
    value = "{{ issueTitle }}"
  }
}

resource "newrelic_workflow" "oficina" {
  count = var.email_alertas == "" ? 0 : 1

  name                  = "Oficina Mecânica (${var.ambiente})"
  muting_rules_handling = "NOTIFY_ALL_ISSUES"

  issues_filter {
    name = "Políticas da oficina"
    type = "FILTER"

    predicate {
      attribute = "labels.policyIds"
      operator  = "EXACTLY_MATCHES"
      values    = [for p in newrelic_alert_policy.oficina : p.id]
    }
  }

  destination {
    channel_id = newrelic_notification_channel.email[0].id
  }
}

output "dashboard_url" {
  value = newrelic_one_dashboard_json.saga.permalink
}

output "politicas" {
  value = { for k, p in newrelic_alert_policy.oficina : k => p.name }
}
