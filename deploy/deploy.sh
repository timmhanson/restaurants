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
az webapp deploy -n "$APP" -g "$RG" --src-path "$OUT.zip" --type zip --clean true --restart true -o none
echo "Deployed: https://$APP.azurewebsites.net"
