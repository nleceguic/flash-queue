#!/usr/bin/env bash
# Siembra (o reinicia) el evento de 500 plazas usado por flashqueue-spike.js, contra el Postgres
# de docker-compose.yml. Requiere que el sistema ya esté levantado: docker compose up -d
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONTAINER="${POSTGRES_CONTAINER:-flash-queue-postgres-1}"

docker exec -i "$CONTAINER" psql -U flashqueue -d flashqueue < "$SCRIPT_DIR/seed-event.sql"
echo "Evento sembrado: 7c9e6679-7425-40de-944b-e07fc1f90ae7 (500 plazas)"
