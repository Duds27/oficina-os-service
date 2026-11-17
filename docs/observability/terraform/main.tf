# Observabilidade da Fase 4 no New Relic: dashboard "Saga de OS" e alertas (Etapa 5, 5.5 e 5.6).
# Os dados chegam por OTLP (traces, métricas e logs dos 3 serviços) e pelo Prometheus agent do
# nri-bundle (métricas do RabbitMQ).
#
#   terraform init
#   terraform apply -var="newrelic_account_id=<id>" -var="newrelic_api_key=<user key>"
#
# A User key (NRAK-...) é a da API NerdGraph, não a license key de ingestão. Passe-a por
# TF_VAR_newrelic_api_key, nunca num arquivo versionado.
terraform {
  required_version = ">= 1.5.0"

  required_providers {
    newrelic = {
      source  = "newrelic/newrelic"
      version = "~> 3.101.8"
    }
  }
}

provider "newrelic" {
  account_id = var.newrelic_account_id
  api_key    = var.newrelic_api_key
  region     = var.newrelic_region
}

variable "newrelic_account_id" {
  type        = number
  description = "Id da conta no New Relic."
}

variable "newrelic_api_key" {
  type        = string
  sensitive   = true
  description = "User key da API NerdGraph (NRAK-...)."
}

variable "newrelic_region" {
  type    = string
  default = "US"

  validation {
    condition     = contains(["US", "EU"], var.newrelic_region)
    error_message = "Região US ou EU."
  }
}

variable "ambiente" {
  type        = string
  default     = "homolog"
  description = "deployment.environment dos serviços que os alertas olham (homolog | prod)."
}

variable "email_alertas" {
  type        = string
  default     = ""
  description = "E-mail que recebe os incidentes. Vazio: os alertas só aparecem no New Relic."
}
