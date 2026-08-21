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
| `API_BASE_URL`    | `http://localhost:5257`                    | Recibe los `POST` de reservas |
| `STATUS_BASE_URL` | `http://localhost:5280`                    | Sirve `GET /events/{id}/status` |
| `EVENT_ID`        | `7c9e6679-7425-40de-944b-e07fc1f90ae7`     | Evento a reservar (el que siembra `seed-event.sql`) |
| `TOTAL_STOCK`     | `500`                                      | Solo informativo, para el resumen |

Desde [ADR 0013](../docs/adr/0013-api-y-workers-no-comparten-el-channel-de-ingesta.md)
ambas apuntan al mismo proceso (`workers` en `docker-compose.yml`), expuesto
en dos puertos por compatibilidad con la topología anterior — no hace falta
levantar dos servicios distintos.

```bash
k6 run -e API_BASE_URL=http://mi-host:5257 -e STATUS_BASE_URL=http://mi-host:5280 flashqueue-spike.js
```

## Qué mide este test

Hasta [ADR 0013](../docs/adr/0013-api-y-workers-no-comparten-el-channel-de-ingesta.md),
`FlashQueue.Api` y `FlashQueue.Workers` corrían como procesos independientes,
cada uno con su propio canal de ingesta en memoria — una reserva aceptada
(202) por Api nunca llegaba a persistirse. Con esa limitación ya corregida
(ambos comparten ahora un único proceso y un único channel), este test mide
el pipeline completo: ingesta HTTP, backpressure del channel, persistencia
en Postgres y el estado real de `reserved_stock` bajo el pico.

## Resultados vigentes

Las cifras del README raíz ("Números del test de carga") ya se volvieron a
medir contra la topología corregida por ADR 0013 — 3 ejecuciones
consecutivas de `./run.sh`, mismo workload (evento de 500 plazas, pico de
20.000 peticiones, 2 minutos de tráfico sostenido), sin tocar
`flashqueue-spike.js`. El pico completo se drena en ~6 s (antes: nunca) y
el stock nunca se supera en ninguna de las 3 ejecuciones.

La única métrica con variabilidad real entre ejecuciones es la tasa de
error (4,35 %–12,10 %), y no es un fallo de la aplicación: es el número de
conexiones TCP rechazadas por el sistema operativo en la primera fracción
de segundo del pico (k6 abriendo ~2.000 conexiones simultáneas contra
`localhost` a través de la red de Docker Desktop en Windows), verificado
cruzando el JSON crudo (`status=0`, cero `5xx`) con los logs de `workers`
en esa misma ventana (el proceso está saturado de trabajo real, no caído).
Detalle completo, tabla comparativa con la topología anterior y entorno de
medición en el [README raíz](../README.md#números-del-test-de-carga).

La garantía de cero overselling sigue estando probada, además, de forma
rigurosa y directa contra el repositorio (sin pasar por HTTP ni por el
channel) en
[`../tests/FlashQueue.Tests.Integration/Persistence/ReservationRepositoryOversellingTests.cs`](../tests/FlashQueue.Tests.Integration/Persistence/ReservationRepositoryOversellingTests.cs)
(20.000 reservas concurrentes reales) — este test de carga la observa desde
fuera del proceso vía `GET /events/{id}/status`, pero no la sustituye.
