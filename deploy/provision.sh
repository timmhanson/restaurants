#!/usr/bin/env bash
# Creates (or re-asserts) every Azure resource the app uses. Idempotent.
# Everything lives in one dedicated resource group on free / lowest-cost tiers:
#   - App Service plan F1 (Linux, free)      -> hosts the .NET 10 API + React app
#   - Storage account Standard_LRS           -> one JSON blob per investment group (settings)
#   - System-assigned managed identity       -> the web app reads/writes blobs without secrets
# The SQLite database lives on the App Service persistent share at /home/data.
set -euo pipefail

RG=${RG:-rg-restaurant-feedback}
LOC=${LOC:-centralus}
PLAN=${PLAN:-asp-restaurant-feedback}
APP=${APP:-restaurant-feedback-45200}
SA=${SA:-strestfeedback45200}

az group create -n "$RG" -l "$LOC" --tags project=restaurant-feedback -o none
az appservice plan create -n "$PLAN" -g "$RG" -l "$LOC" --is-linux --sku F1 -o none
az webapp create -n "$APP" -g "$RG" -p "$PLAN" --runtime "DOTNETCORE:10.0" -o none 2>/dev/null || true
az webapp config set -n "$APP" -g "$RG" --always-on false --http20-enabled true --ftps-state Disabled -o none
az webapp update -n "$APP" -g "$RG" --https-only true -o none

az storage account create -n "$SA" -g "$RG" -l "$LOC" --sku Standard_LRS --kind StorageV2 --access-tier Hot \
  --allow-blob-public-access false --min-tls-version TLS1_2 -o none
az storage container create -n group-settings --account-name "$SA" --auth-mode login -o none || true

PRINCIPAL=$(az webapp identity assign -n "$APP" -g "$RG" --query principalId -o tsv)
SA_ID=$(az storage account show -n "$SA" -g "$RG" --query id -o tsv)
az role assignment create --assignee-object-id "$PRINCIPAL" --assignee-principal-type ServicePrincipal \
  --role "Storage Blob Data Contributor" --scope "$SA_ID" -o none 2>/dev/null || true

cat <<MSG
Resources ready.
  Web app:  https://$APP.azurewebsites.net
  Storage:  https://$SA.blob.core.windows.net/group-settings

Now set the app settings (once). Replace the placeholders:
  az webapp config appsettings set -n $APP -g $RG --settings \\
    ASPNETCORE_ENVIRONMENT=Production SCM_DO_BUILD_DURING_DEPLOYMENT=false WEBSITES_ENABLE_APP_SERVICE_STORAGE=true \\
    "ConnectionStrings__Default=Data Source=/home/data/restaurant-feedback.db" \\
    "Jwt__Key=<random 48+ chars>" \\
    "Storage__AccountUrl=https://$SA.blob.core.windows.net" \\
    "Seed__SuperAdminUsername=<email>" "Seed__SuperAdminPassword=<password>" \\
    "Seed__OwnerUsername=owner" "Seed__OwnerPassword=<password>"
Then deploy with deploy/deploy.sh or push to main (GitHub Actions).
MSG
