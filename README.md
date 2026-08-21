# FlashQueue

[![CI](https://github.com/nleceguic/flash-queue/actions/workflows/ci.yml/badge.svg)](https://github.com/nleceguic/flash-queue/actions/workflows/ci.yml)

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
de tráfico sostenido ([`load-tests/`](load-tests/), k6). Medidas contra la
**topología corregida por [ADR 0013](docs/adr/0013-api-y-workers-no-comparten-el-channel-de-ingesta.md)**
(`Api` y `Workers` en un único proceso `workers`, un único channel de
ingesta) — 3 ejecuciones consecutivas, `docker compose up -d` sin el
profile `observability`, commit `a4d8133`:

| Métrica | Ejecución 1 | Ejecución 2 | Ejecución 3 | Mediana |
|---|---:|---:|---:|---:|
| Peticiones totales | 23.777 | 23.778 | 23.778 | 23.778 |
| Throughput medio | 128,5 req/s | 128,5 req/s | 128,5 req/s | 128,5 req/s |
| Pico (20.000 peticiones) drenado en | 5,7 s | 5,7 s | 6,4 s | 5,7 s |
| Latencia p50 | 163,8 ms | 176,9 ms | 232,5 ms | 176,9 ms |
| Latencia p95 | 1.076,7 ms | 1.066,5 ms | 1.137,3 ms | 1.076,7 ms |
| Latencia p99 | 1.281,7 ms | 1.146,9 ms | 1.356,8 ms | 1.281,7 ms |
| Tasa de error medida* | 10,53 % | 12,10 % | 4,35 % | 10,53 % |
| **Overselling** | **0** | **0** | **0** | **0** |

\* *No son fallos de FlashQueue: el 100 % son conexiones TCP rechazadas por
el sistema operativo (`dial tcp ... connectex`, `status=0` en
`http_req_duration` — verificado contra el JSON crudo de las 3
ejecuciones), no `5xx` ni `429` del rate limiter, concentradas en la
primera fracción de segundo del pico, cuando k6 abre ~2.000 conexiones
simultáneas contra `localhost`. Los logs de `workers` en esa misma ventana
muestran a `ReservationProcessingWorker` consultando y escribiendo en
Postgres sin parar — el proceso está saturado de trabajo real, no caído ni
colgado. Es una limitación del entorno de medición (k6 y Docker Desktop
compitiendo por la cola de conexión TCP del mismo host Windows), no de la
topología que corrige ADR 0013; con generador de carga y sistema en
máquinas separadas esta cifra debería reducirse o desaparecer.*

**Comparación con la topología anterior a ADR 0013** (histórica —
[ADR 0008](docs/adr/0008-test-de-carga-k6-y-endpoint-de-estado.md)):

| Métrica | Topología anterior (ADR 0008) | Topología actual (ADR 0013) |
|---|---|---|
| Pico de 20.000 peticiones | nunca se drena — `WriteAsync` bloqueado indefinidamente pasadas ~500 | drenado en ~6 s |
| Peticiones totales / throughput | 23.685 en 190 s (~124,7 req/s) — casi todas fueron el cliente agotando su timeout de 5 s, no procesamiento real | 23.778 en 185 s (~128,5 req/s) — completadas de verdad |
| Latencia p50 / p95 / p99 | ~5,00 s / ~5,03 s / ~5,13 s (= el timeout del cliente) | ~177 ms / ~1.077 ms / ~1.282 ms (mediana; procesamiento real) |
| Causa raíz | dos procesos, dos channels de ingesta sin relación — el 100 % de las reservas se perdía | un proceso, un channel compartido — el pico completo llega a Postgres |

> Estas cifras se midieron sobre la topología corregida por ADR 0013; las de
> la topología anterior quedan en [ADR 0008](docs/adr/0008-test-de-carga-k6-y-endpoint-de-estado.md)
> como referencia histórica y **no son comparables directamente** — su
> "p95 ~5,03 s" mide un timeout de cliente, no trabajo real, porque casi
> ninguna petición llegaba a completarse. Entorno de medición: host local
> Windows 11 (10.0.26200), Docker Desktop 29.6.1 / Compose v5.3.0, AMD Ryzen
> 3 3100 (4 núcleos / 8 hilos), 16 GB RAM, k6 v2.1.0 corriendo en el mismo
> host que los contenedores (ver nota* de la tabla). Detalle de las 3
> ejecuciones y metodología en [`load-tests/README.md`](load-tests/README.md).

La garantía de cero overselling **sigue probada**, además, de forma
rigurosa y directa contra Postgres, al margen de este test: 20.000
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
