# FlashQueue

FlashQueue es un motor de reservas para eventos con stock limitado
(entradas, mesas) que garantiza **cero overselling** bajo picos de miles
de peticiones concurrentes — el problema clásico de un "drop" de
entradas — resuelto con backpressure, locking a nivel de fila en Postgres
y desacoplamiento vía eventos, no lanzando más servidores al problema.

## Arquitectura

```
Cliente
   │  POST /events/{eventId}/reservations
   ▼
[ FlashQueue.Workers ]    proceso único (ADR 0013): Minimal API (rate limiting por evento,
   │                      responde 202 al instante) + BackgroundService, compartiendo un
   │                      Channel<T> bounded (backpressure en memoria, sin cola externa — ADR 0009)
   │  round-robin por evento + SemaphoreSlim (fairness — ADR 0001)
   ▼
[ PostgreSQL: events / reservations ]
   │  SELECT ... FOR UPDATE SKIP LOCKED, decremento + insert en la misma transacción
   │  → cero overselling garantizado (ADR 0002, 0010)
   ▼
[ RabbitMQ vía MassTransit ]   ReservationConfirmed / ReservationRejected (ADR 0003, 0011)
   │
   ├──▶ [ Payments ]
   ├──▶ [ Notifications ]
   └──▶ [ Analytics ]      ← consumidores independientes y desacoplados

Instrumentado de extremo a extremo: OpenTelemetry → Collector → Tempo (traces) / Prometheus (métricas) → Grafana (ADR 0006)
Resiliencia: Polly — retry en Postgres, circuit breaker en RabbitMQ (ADR 0004) · modo caos activable por variable de entorno (ADR 0005)
```

## Números del test de carga

Evento con 500 plazas, pico de ~20.000 peticiones concurrentes + 2 minutos
de tráfico sostenido ([`load-tests/`](load-tests/), k6):

| Métrica (topología anterior a ADR 0013) | Valor |
|---|---|
| Peticiones totales | 23.685 en 190 s (~124,7 req/s) |
| Latencia p50 / p95 / p99 | ~5,00 s / ~5,03 s / ~5,13 s |
| **Overselling** | **0 casos** — stock nunca superado |

**Estas cifras son históricas, no vigentes.** Se obtuvieron cuando `Api` y
`Workers` corrían como procesos separados, cada uno con su propio channel
de ingesta en memoria (ADR 0009); sin un lector en el proceso de `Api`, el
channel bounded (capacidad 500) se saturaba de verdad tras las primeras
peticiones y dejaba de drenar, así que el resto de la carga esperaba hasta
que el cliente agotaba su timeout (~5 s) — el ~2% de peticiones que sí
completaban encajaba casi exactamente con esa capacidad de 500. Fue un
hallazgo real de un load test real, con causa raíz diagnosticada, no un
número inventado ni escondido — detalle completo en
[ADR 0008](docs/adr/0008-test-de-carga-k6-y-endpoint-de-estado.md).

[ADR 0013](docs/adr/0013-api-y-workers-no-comparten-el-channel-de-ingesta.md)
corrige esa limitación: `Api` y `Workers` comparten ahora un único proceso y
un único channel. La demo end-to-end (`docker compose up -d` + `load-tests/run.sh`)
vuelve a ejercitar el pipeline completo, pero **faltan números nuevos** —
hay que volver a correr el test de carga contra la topología corregida
para reemplazar la tabla de arriba.

La garantía de cero overselling **sí** está probada de forma rigurosa,
directamente contra Postgres y al margen de esta corrección: 20.000
reservas concurrentes reales contra un evento de 500 plazas, exactamente
500 `Confirmed` / 19.500 `Rejected`, verificado en 3+ ejecuciones
consecutivas —
[`ReservationRepositoryOversellingTests`](tests/FlashQueue.Tests.Integration/Persistence/ReservationRepositoryOversellingTests.cs).

## Levantarlo

```bash
docker compose up -d       # 1. Postgres, RabbitMQ, workers, los 3 consumidores
docker compose ps          # 2. confirmar que los 6 servicios están "healthy"
./load-tests/run.sh        # 3. sembrar el evento y correr el test de carga (genera resultados/gráfica)
```

Detalles (modo caos, stack de observabilidad, desarrollo local sin
Docker) en [`README-DOCKER.md`](README-DOCKER.md); el brief completo del
dominio y las convenciones de código, en [`CLAUDE.md`](CLAUDE.md).

## Decisiones de diseño

Cada decisión de arquitectura no trivial está documentada como ADR corto
(contexto, decisión, alternativas descartadas, consecuencias) en
[`docs/adr/`](docs/adr/) — 13 hasta ahora, entre ellas por qué un
`Channel<T>` en memoria y no una cola externa, por qué locking pesimista
en Postgres y no optimista, por qué RabbitMQ y no Kafka, por qué no
Entity Framework para las tablas de stock, y por qué Api y Workers
terminaron unificados en un solo proceso (ADR 0013).

## Licencia

[MIT](LICENSE)
