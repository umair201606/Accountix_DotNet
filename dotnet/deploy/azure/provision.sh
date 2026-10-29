#!/usr/bin/env bash
# The same resources as main.bicep, with the az CLI. NOT RUN: written for the
# go-ahead. Review every value; docs/DEPLOY.md explains each choice.
#
#   RG=accountix-prod LOCATION=eastus2 APP=accountix-net PUBLIC_HOST=erp.example.com \
#     dotnet/deploy/azure/provision.sh
set -euo pipefail
: "${RG:?resource group}" "${LOCATION:?region nearest Neon, e.g. eastus2}" "${APP:?app name}" "${PUBLIC_HOST:?public domain}"
SKU="${SKU:-P0v3}"; INSTANCES="${INSTANCES:-2}"; MAX="${MAX_INSTANCES:-4}"; KV="${KV:-$APP-kv}"; FWD="${FORWARDED_LIMIT:-2}"

az group create -n "$RG" -l "$LOCATION" -o none
az appservice plan create -g "$RG" -n "$APP-plan" --is-linux --sku "$SKU" --number-of-workers "$INSTANCES" -o none
az webapp create -g "$RG" -p "$APP-plan" -n "$APP" --runtime "DOTNETCORE:10.0" --assign-identity '[system]' -o none
az webapp update -g "$RG" -n "$APP" --https-only true --client-affinity-enabled false -o none
az webapp config set -g "$RG" -n "$APP" --always-on true --http20-enabled true --min-tls-version 1.2 \
   --ftps-state Disabled --startup-file "dotnet Accountix.Web.dll" --generic-configurations '{"healthCheckPath": "/net/health"}' -o none

az keyvault create -g "$RG" -n "$KV" -l "$LOCATION" --enable-rbac-authorization true --enable-purge-protection true -o none
KV_ID=$(az keyvault show -n "$KV" --query id -o tsv)
APP_PID=$(az webapp identity show -g "$RG" -n "$APP" --query principalId -o tsv)
az role assignment create --assignee-object-id "$APP_PID" --assignee-principal-type ServicePrincipal \
   --role "Key Vault Secrets User" --scope "$KV_ID" -o none
# Secrets (set by a person, never in a file):
#   az keyvault secret set --vault-name "$KV" -n SECRET-KEY   --value '<the Python app SECRET_KEY on Vercel>'
#   az keyvault secret set --vault-name "$KV" -n DATABASE-URL --value 'postgresql://<user>:<pw>@ep-...-pooler.<region>.neon.tech/<db>?sslmode=require'
#   az keyvault secret set --vault-name "$KV" -n SENTRY-DSN   --value '<dsn or empty>'

REF="@Microsoft.KeyVault(VaultName=$KV;SecretName="
SETTINGS=(
  ASPNETCORE_ENVIRONMENT=Production
  "SECRET_KEY=${REF}SECRET-KEY)" "DATABASE_URL=${REF}DATABASE-URL)" "SENTRY_DSN=${REF}SENTRY-DSN)"
  SENTRY_ENVIRONMENT=production DATA_PROTECTION_STORE=postgres DB_MAX_POOL_SIZE=20 DB_LOCK_TIMEOUT_MS=15000
  FORWARDED_LIMIT="$FWD" FORWARDED_ALLOWED_HOSTS="$PUBLIC_HOST,$APP.azurewebsites.net" HSTS_MAX_AGE_DAYS=365
  LOG_FORMAT=json WEBSITE_TIME_ZONE=UTC WEBSITE_RUN_FROM_PACKAGE=1
  WEBSITE_SWAP_WARMUP_PING_PATH=/net/health WEBSITE_SWAP_WARMUP_PING_STATUSES=200
)
az webapp config appsettings set -g "$RG" -n "$APP" --settings "${SETTINGS[@]}" -o none

# Staging slot: deploy there, warm up, swap (the swap back is the rollback).
az webapp deployment slot create -g "$RG" -n "$APP" --slot staging --configuration-source "$APP" -o none
az webapp identity assign -g "$RG" -n "$APP" --slot staging -o none
SLOT_PID=$(az webapp identity show -g "$RG" -n "$APP" --slot staging --query principalId -o tsv)
az role assignment create --assignee-object-id "$SLOT_PID" --assignee-principal-type ServicePrincipal \
   --role "Key Vault Secrets User" --scope "$KV_ID" -o none
az webapp config appsettings set -g "$RG" -n "$APP" --slot staging --slot-settings SENTRY_ENVIRONMENT=staging -o none
az webapp config set -g "$RG" -n "$APP" --slot staging --always-on false -o none

# Autoscale on CPU (each instance holds at most DB_MAX_POOL_SIZE connections).
az monitor autoscale create -g "$RG" --resource "$APP-plan" --resource-type Microsoft.Web/serverfarms \
   -n "$APP-autoscale" --min-count "$INSTANCES" --max-count "$MAX" --count "$INSTANCES" -o none
az monitor autoscale rule create -g "$RG" --autoscale-name "$APP-autoscale" --condition "CpuPercentage > 70 avg 5m" --scale out 1 -o none
az monitor autoscale rule create -g "$RG" --autoscale-name "$APP-autoscale" --condition "CpuPercentage < 30 avg 15m" --scale in 1 -o none

# Logs to Log Analytics (stdout JSON + HTTP log).
az monitor log-analytics workspace create -g "$RG" -n "$APP-logs" -l "$LOCATION" -o none
WS=$(az monitor log-analytics workspace show -g "$RG" -n "$APP-logs" --query id -o tsv)
SITE=$(az webapp show -g "$RG" -n "$APP" --query id -o tsv)
az monitor diagnostic-settings create -n to-log-analytics --resource "$SITE" --workspace "$WS" \
   --logs '[{"category":"AppServiceConsoleLogs","enabled":true},{"category":"AppServiceHTTPLogs","enabled":true},{"category":"AppServicePlatformLogs","enabled":true}]' -o none

echo "Outbound IPs (for Neon's IP allow list):"
az webapp show -g "$RG" -n "$APP" --query outboundIpAddresses -o tsv
