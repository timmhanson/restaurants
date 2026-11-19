#!/usr/bin/env bash
# Builds the React app, bundles it into the API's wwwroot, publishes the API and zip-deploys it.
set -euo pipefail
cd "$(dirname "$0")/.."

RG=${RG:-rg-restaurant-feedback}
APP=${APP:-restaurant-feedback-45200}
OUT=publish

echo "== front end"
(cd web && npm ci --no-audit --no-fund && npm run build)

echo "== API"
rm -rf src/RestaurantFeedback.Api/wwwroot "$OUT" "$OUT.zip"
cp -r web/dist src/RestaurantFeedback.Api/wwwroot
dotnet publish src/RestaurantFeedback.Api -c Release -o "$OUT" --nologo -v q

echo "== deploy"
(cd "$OUT" && zip -qr "../$OUT.zip" .)
# The site is stopped while its files are replaced, so no half-replaced copy runs and nothing else writes the database.
az webapp stop -n "$APP" -g "$RG" -o none
sleep 20
trap 'az webapp start -n "$APP" -g "$RG" -o none' EXIT
az webapp deploy -n "$APP" -g "$RG" --src-path "$OUT.zip" --type zip --clean true --restart false -o none
az webapp start -n "$APP" -g "$RG" -o none
trap - EXIT
for i in $(seq 1 36); do
  [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 "https://$APP.azurewebsites.net/api/health" || true)" = "200" ] && break
  sleep 10
done
echo "Deployed: https://$APP.azurewebsites.net"
