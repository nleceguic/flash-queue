# FlashQueue en Docker

Levanta todo el sistema con un solo comando. Para el resto del proyecto
(arquitectura, decisiones de diseño, cómo correr los servicios en local sin
Docker para desarrollo activo) ver [`README.md`](README.md) y
[`CLAUDE.md`](CLAUDE.md).

## Levantarlo

```bash
docker compose up -d
```

Esto construye y arranca, en orden (Postgres y RabbitMQ tienen que estar
`healthy` antes de que arranquen los servicios de la aplicación):

| Servicio                  | Puerto host | Qué es |
|----------------------------|:-----------:|--------|
| `postgres`                 | `5433`      | Base de datos (puerto host no estándar a propósito, para no chocar con otro Postgres local) |
| `rabbitmq`                 | `5672` / `15672` | Broker AMQP / **panel de administración** (`http://localhost:15672`, usuario y contraseña `flashqueue`) |
| `workers`                  | `5257` / `5280` | Proceso único (ver [ADR 0013](docs/adr/0013-api-y-workers-no-comparten-el-channel-de-ingesta.md)): `POST /events/{eventId}/reservations` (`5257`) + motor de reserva + `GET /health/dependencies` (`5280`, estado del circuit breaker de RabbitMQ) |
| `consumers-payments`       | `5281`      | Stub de Pagos |
| `consumers-notifications`  | `5282`      | Stub de Notificaciones |
| `consumers-analytics`      | `5283`      | Stub de Analítica |

Comprobar que todo está sano:

```bash
docker compose ps
```

Cada servicio de la aplicación expone `/health` (o `/health/dependencies` en
`workers`), así que `docker compose ps` debería mostrar `healthy` para los
seis servicios una vez arrancan del todo (el primer arranque tarda un poco
más: `workers` aplica el esquema SQL antes de aceptar tráfico).

Probar el flujo (el evento tiene que existir en la tabla `events` — insértalo
a mano contra Postgres, no hay todavía un endpoint de catálogo):

```bash
curl -X POST http://localhost:5257/events/<eventId-guid>/reservations \
  -H "Content-Type: application/json" \
  -d '{"userId":"<userId-guid>","quantity":1}'
```

Para parar todo (los datos de Postgres/RabbitMQ quedan en volúmenes con
nombre, así que un `up -d` posterior los recupera):

```bash
docker compose down
```

Para borrar también los datos:

```bash
docker compose down -v
```

## Activar el modo caos

`workers` es el único servicio que lee `CHAOS_MODE` (ver
[`docs/adr/0005-modo-caos.md`](docs/adr/0005-modo-caos.md)): con él activo,
inyecta latencia aleatoria y fallos artificiales antes de cada llamada a
Postgres/RabbitMQ, logueados con el prefijo `[CHAOS]` — así se puede
provocar en caliente el retry de Postgres y el circuit breaker de RabbitMQ
(`docs/adr/0004-polly-retry-postgres-circuit-breaker-rabbitmq.md`) para el
vídeo demo.

```bash
CHAOS_MODE=true docker compose up -d workers
```

(o `export CHAOS_MODE=true` antes de un `docker compose up -d` normal, si
quieres que arranque así desde el principio). Para ver el efecto:

```bash
docker compose logs -f workers | grep CHAOS
```

Y para comprobar el estado del circuit breaker en cualquier momento:

```bash
curl http://localhost:5280/health/dependencies
```

Para volver a desactivarlo, recrea el contenedor sin la variable (o con
`CHAOS_MODE=false`, se comporta exactamente igual — ver
`ChaosServiceCollectionExtensionsTests`):

```bash
docker compose up -d workers
```

## Activar el profile de observabilidad

El stack de Grafana + Tempo + Prometheus + el Collector
(`docs/adr/0006-opentelemetry-collector-como-fan-out.md`) es opcional y no
arranca con el comando de arriba — vive detrás del profile `observability`:

```bash
docker compose --profile observability up -d
```

Esto arranca el sistema completo (si no estaba ya arrancado) **más**:

| Servicio         | Puerto host | Qué es |
|-------------------|:-----------:|--------|
| `otel-collector`  | `4317` / `4318` | Receptor OTLP (gRPC/HTTP) — a esto exporta `workers` |
| `tempo`           | `3200`      | Backend de trazas |
| `prometheus`      | `9090`      | Backend de métricas |
| `grafana`         | `3000`      | `http://localhost:3000`, sin login (modo anónimo), dashboard **FlashQueue - Overview** ya provisionado |

Genera tráfico (ver arriba) y abre Grafana. Para parar solo el stack de
observabilidad sin tocar el resto del sistema:

```bash
docker compose --profile observability stop otel-collector tempo prometheus grafana
```

o, para pararlo todo (sistema + observabilidad) a la vez:

```bash
docker compose --profile observability down
```

## Notas

- Hasta [ADR 0013](docs/adr/0013-api-y-workers-no-comparten-el-channel-de-ingesta.md),
  `api` y `workers` corrían como procesos **separados**, cada uno con su
  propio canal de ingesta en memoria sin relación entre sí — una reserva
  aceptada por `api` nunca llegaba a persistirse. Ese ADR documenta el
  hallazgo y la corrección: ahora es un único servicio (`workers`) que
  comparte de verdad el mismo channel entre el endpoint y el worker.
- Reconstruir imágenes tras cambiar código (`docker compose up` no reconstruye
  solo porque el código cambió):

  ```bash
  docker compose up -d --build
  ```
