# Test de carga — FlashQueue

Simula el escenario que da nombre al proyecto: un evento con 500 plazas
sometido a un pico de ~20.000 peticiones concurrentes en los primeros
segundos, seguido de 2 minutos de tráfico sostenido más bajo. Ver
[`flashqueue-spike.js`](flashqueue-spike.js) — los comentarios de cabecera
del script son la referencia completa; esto es solo el resumen operativo.

## Requisitos

- [k6](https://k6.io/docs/get-started/installation/) en el `PATH`.
- Python 3.9+ con `pip install -r requirements.txt` (solo `matplotlib`) para
  la gráfica.
- El sistema levantado — ver [`../README-DOCKER.md`](../README-DOCKER.md):
  `docker compose up -d`.

## Correrlo

Todo en un comando:

```bash
./run.sh
```

O paso a paso:

```bash
./seed-event.sh   # crea/reinicia el evento fijo de 500 plazas en Postgres

k6 run --out json=results/raw-metrics.json flashqueue-spike.js

python plot_results.py   # lee results/raw-metrics.json + results/summary.json
```

Resultados en `results/` (ignorado por git salvo `.gitkeep` — usa
`git add -f results/flashqueue-spike.png` si quieres versionar una gráfica
concreta para el README):

- `summary.json` — throughput, p50/p95/p99, tasa de error, nº de respuestas
  429 y si hubo alguna violación de overselling. Lo escribe `handleSummary()`
  dentro del propio script de k6.
- `raw-metrics.json` — serie temporal completa (una línea JSON por muestra),
  la produce `k6 run --out json=...`. La usa `plot_results.py`.
- `flashqueue-spike.png` — throughput y latencia p50/p95/p99 a lo largo del
  test, con la fase de pico resaltada.

## Variables de entorno del script de k6

Todas opcionales — los valores por defecto casan con `docker-compose.yml` y
`seed-event.sql`:

| Variable          | Por defecto                              | Qué es |
|-------------------|-------------------------------------------|--------|
| `API_BASE_URL`    | `http://localhost:5257`                    | `FlashQueue.Api` — recibe los `POST` de reservas |
| `STATUS_BASE_URL` | `http://localhost:5280`                    | `FlashQueue.Workers` — sirve `GET /events/{id}/status` |
| `EVENT_ID`        | `7c9e6679-7425-40de-944b-e07fc1f90ae7`     | Evento a reservar (el que siembra `seed-event.sql`) |
| `TOTAL_STOCK`     | `500`                                      | Solo informativo, para el resumen |

```bash
k6 run -e API_BASE_URL=http://mi-host:5257 -e STATUS_BASE_URL=http://mi-host:5280 flashqueue-spike.js
```

## ⚠️ Qué mide realmente este test ahora mismo

`FlashQueue.Api` y `FlashQueue.Workers` son procesos independientes, cada uno
con su propio canal de ingesta en memoria (ver
[`../docs/adr/0006-opentelemetry-collector-como-fan-out.md`](../docs/adr/0006-opentelemetry-collector-como-fan-out.md)
y [`../README-DOCKER.md`](../README-DOCKER.md), sección "Notas"). Una reserva
aceptada (202) por Api **no llega** a Workers cuando corren en contenedores
separados, como en `docker-compose.yml`. Con la topología actual:

- El test **sí mide de verdad** el comportamiento de `FlashQueue.Api` bajo
  carga: throughput, latencia y cómo se comporta el rate limiter (3.000
  peticiones/s + cola de 2.000 por evento) ante un pico de 20.000 — la
  degradación controlada que pide CLAUDE.md sección 1.
- El check "nunca se supera el stock" pasará siempre, porque `reserved_stock`
  no se mueve de 0 (Workers nunca ve estas peticiones). No es un check vacío
  por error: la garantía real de cero overselling bajo concurrencia ya está
  probada de forma rigurosa y directa contra el repositorio en
  [`../tests/FlashQueue.Tests.Integration/Persistence/ReservationRepositoryOversellingTests.cs`](../tests/FlashQueue.Tests.Integration/Persistence/ReservationRepositoryOversellingTests.cs)
  (20.000 reservas concurrentes reales). Este test de carga la observa desde
  fuera del proceso vía `GET /events/{id}/status`, pero no la sustituye.
