#!/usr/bin/env bash
# Orquesta el test de carga completo: siembra el evento, corre k6 guardando la serie temporal,
# y genera el PNG. Requiere el sistema ya levantado (docker compose up -d — ver README-DOCKER.md),
# k6 en el PATH, y python con las dependencias de requirements.txt instaladas.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

mkdir -p "$SCRIPT_DIR/results"
RAW_OUTPUT="$SCRIPT_DIR/results/raw-metrics.json"
rm -f "$RAW_OUTPUT"

echo "==> Sembrando el evento de la prueba de carga (500 plazas)"
"$SCRIPT_DIR/seed-event.sh"

echo "==> Ejecutando k6 (pico + 2 minutos de tráfico sostenido — puede tardar unos 3 minutos)"
# k6 devuelve código de salida distinto de cero si algún threshold no se cumple (ver
# flashqueue-spike.js): con la topología actual eso es exactamente lo esperado (ver el AVISO al
# principio del script), no un fallo de esta orquestación — así que se captura el código sin
# abortar, y se genera la gráfica igualmente con los datos obtenidos.
set +e
k6 run --out "json=$RAW_OUTPUT" "$SCRIPT_DIR/flashqueue-spike.js"
k6_exit_code=$?
set -e
if [ "$k6_exit_code" -ne 0 ]; then
  echo "==> k6 terminó con thresholds incumplidos (código $k6_exit_code) — esperado con la topología actual, ver el AVISO en flashqueue-spike.js. Generando la gráfica igualmente."
fi

echo "==> Generando la gráfica"
PYTHON_BIN="${PYTHON_BIN:-python3}"
command -v "$PYTHON_BIN" >/dev/null 2>&1 || PYTHON_BIN=python
"$PYTHON_BIN" "$SCRIPT_DIR/plot_results.py" \
  --raw "$RAW_OUTPUT" \
  --summary "$SCRIPT_DIR/results/summary.json" \
  --output "$SCRIPT_DIR/results/flashqueue-spike.png"

echo "==> Listo. Resultados en $SCRIPT_DIR/results/"
