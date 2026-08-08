# ADR 0004: Polly — retry en Postgres, circuit breaker + timeout en RabbitMQ

- **Fecha**: 2026-08-07
- **Estado**: Aceptada

## Contexto

CLAUDE.md (sección 2, punto 6) pide "Polly para reintentos (con jitter), circuit
breaker y timeout por cada dependencia externa". Hasta ahora ninguna de las dos
dependencias externas del worker (Postgres, RabbitMQ) tenía protección frente a
fallos transitorios propios de la infraestructura (una conexión de red que se
cae, un broker momentáneamente inalcanzable) — solo `ReservationRepository` ya
tenía su propio bucle de reintento, pero es uno específico para la contención de
locks (`SELECT ... FOR UPDATE SKIP LOCKED`, ver ADR 0002), no para fallos de
conexión.

Se usa **Polly v8** (`Polly.Core`, API de `ResiliencePipeline`), no la API
"legacy" de `Policy`/`Policy<T>` de versiones anteriores.

## Decisión

### Postgres: retry, nunca circuit breaker

`PostgresResilience.BuildTransientFaultPipeline<T>` envuelve **todo**
`ReservationRepository.ReserveCoreAsync` (abrir conexión + transacción +
commit) en un `ResiliencePipeline<Reservation>` con:

- `MaxRetryAttempts = 3` (configurable, `ReservationRepository:TransientFaultMaxRetryAttempts`).
- `BackoffType = Exponential`, `UseJitter = true` — backoff exponencial con
  jitter, tal como pide CLAUDE.md.
- `ShouldHandle`: solo `NpgsqlException` con `IsTransient == true`.

`NpgsqlException.IsTransient` (calculado por Npgsql a partir del `SqlState` del
error, o de la excepción interna para errores de conexión) es exactamente la
distinción que pide el encargo: verdadero para errores de clase de conexión
(`08xxx`), timeouts de red, deadlocks (`40P01`) o fallos de serialización
(`40001`); **falso** para violaciones de constraint (`23xxx`, p. ej.
`unique_violation`) y errores de validación de datos (`22xxx`). No hace falta
inspeccionar códigos SQL a mano: Npgsql ya hace esa clasificación, y confiar en
ella evita una lista de códigos mantenida a mano que se desincroniza con el
tiempo. Se verificó empíricamente (ver `PostgresResilienceTests`) que
`PostgresException` con `SqlState` de constraint/validación da `IsTransient =
false`, y con `SqlState` de conexión/serialización da `true`.

No se añade circuit breaker en el lado de Postgres: el encargo (punto 1) solo
pide retry aquí. Un circuit breaker sobre la única base de datos del sistema
tampoco tendría a dónde "hacer fail-fast" — no hay una ruta alternativa, así
que el efecto práctico de abrir el circuito sería simplemente rechazar más
rápido, sin el beneficio real de un circuit breaker (proteger un recurso
saturado dejándolo recuperarse).

### RabbitMQ: circuit breaker + timeout, nunca retry aquí

`ReservationEventPublisher` (implementa `IReservationEventPublisher`) envuelve
`IPublishEndpoint.Publish` en un `ResiliencePipeline` (no genérico) con, en
este orden:

1. **Circuit breaker** (más externo): `FailureRatio = 1.0` +
   `MinimumThroughput = 5` (`RabbitMqPublishResilience:ConsecutiveFailuresBeforeBreaking`)
   sobre una `SamplingDuration` de 30s. Polly v8 no tiene un modo "N fallos
   consecutivos" literal (su circuit breaker es por ratio sobre una ventana de
   tiempo) — esta combinación lo emula: con ratio 1.0, **una sola publicación
   con éxito** dentro de la ventana baja el ratio por debajo de 1.0 y el
   circuito no se abre, así que en la práctica se comporta como "N fallos
   consecutivos" mientras la ventana sea suficientemente amplia para
   contenerlos (verificado en `ReservationEventPublisherTests`).
2. **Timeout** (más interno, 2s por intento — `RabbitMqPublishResilience:PublishTimeout`):
   así el timeout se aplica solo al intento real de publicar, no al overhead
   del propio circuit breaker.

El circuit breaker va **fuera** del timeout deliberadamente: con el circuito
abierto, una llamada falla al instante con `BrokenCircuitException` sin
siquiera empezar el intento de publicar ni esperar los 2s — es lo que hace que
el fail-fast sea real y no solo "falla igual de lento pero con otro nombre de
excepción".

No hay retry en la publicación: el encargo (punto 2) solo pide circuit breaker
+ timeout aquí. Además, MassTransit ya tiene su propio mecanismo interno de
reintento de conexión al broker a más bajo nivel; añadir un retry de Polly por
encima solo añadiría reintentos redundantes antes de que el circuit breaker
tenga ocasión de contar el fallo.

`PostgresReservationProcessor` captura `BrokenCircuitException` y
`TimeoutRejectedException` alrededor de la publicación: la reserva ya está
persistida en Postgres en ese punto (el commit ya ocurrió), así que un fallo de
RabbitMQ nunca deshace ni reintenta la reserva — solo se registra que el evento
no se pudo publicar en esta ejecución (ver Consecuencias).

### `CircuitBreakerStateProvider` para `/health/dependencies`

`RabbitMqPublishResiliencePipelineProvider` crea el `CircuitBreakerStateProvider`
y lo registra en `CircuitBreakerStrategyOptions.StateProvider` — la forma
soportada por Polly v8 de leer el estado del circuito desde fuera del pipeline
sin acoplarse a sus eventos `OnOpened`/`OnClosed`. `FlashQueue.Workers` expone
`GET /health/dependencies` devolviendo ese estado (`Closed`/`Open`/`HalfOpen`).
Antes de la primera publicación, `CircuitState` ya reporta `Closed` por
defecto, así que el endpoint funciona desde el arranque sin casos especiales.

Esto obligó a convertir `FlashQueue.Workers` de `Host.CreateApplicationBuilder`
a `WebApplication.CreateBuilder` (con `FrameworkReference` a
`Microsoft.AspNetCore.App`): es el proceso donde vive
`PostgresReservationProcessor` y, con él, el circuit breaker real — el estado
no existe en ningún otro proceso. El resto del comportamiento del proceso (los
`BackgroundService`) no cambia; solo se añade una superficie HTTP mínima.

## Alternativas descartadas

- **Circuit breaker también en Postgres**: descartado, ver arriba.
- **Retry también en la publicación a RabbitMQ**: descartado — el encargo no
  lo pide y MassTransit ya reintenta la conexión a más bajo nivel; combinarlo
  con Polly haría más lento y confuso el camino hacia el circuit breaker.
- **Exponer el estado del circuito desde `FlashQueue.Api`**: `FlashQueue.Api`
  nunca publica a RabbitMQ (solo escribe en el `Channel` de ingesta) y es un
  proceso distinto de `FlashQueue.Workers`, así que no tiene forma de conocer
  el estado real del circuito sin inventarse un mecanismo de sincronización
  entre procesos — el endpoint tiene que vivir donde vive el publicador.
- **`Policy.Handle<T>().CircuitBreakerAsync(5, breakDuration)` (Polly v7,
  "legacy")**: modela literalmente "N fallos consecutivos" sin necesidad de
  emularlo con ratio/ventana. Se descarta por consistencia: el resto del
  proyecto usa exclusivamente la API v8 (`ResiliencePipeline`), y mezclar las
  dos superficies de Polly en el mismo proyecto añadiría una segunda forma de
  hacer lo mismo sin necesidad real.

## Consecuencias

- Ventana de "evento perdido": si `IPublishEndpoint.Publish` falla (circuito
  abierto o timeout) para una reserva ya confirmada/rechazada en Postgres, esa
  reserva nunca vuelve a intentar publicarse — el mismo compromiso ya aceptado
  en ADR 0003 (ahí por la ausencia de outbox transaccional; aquí, además, por
  el fail-fast intencional del circuit breaker). Mitigarlo requeriría un
  outbox transaccional con un publicador de reintento en segundo plano, fuera
  del alcance actual.
- `TransientFaultMaxRetryAttempts`/`TransientFaultBaseDelay` y
  `RabbitMqPublishResilience:*` son configurables por `appsettings`/variables
  de entorno (secciones `ReservationRepository` y `RabbitMqPublishResilience`
  respectivamente), igual que el resto de parámetros de resiliencia del
  proyecto.
- `PostgresResilienceTests` y `ReservationEventPublisherTests` (unitarios, sin
  Postgres ni RabbitMQ reales) verifican el comportamiento con fallos
  inyectados: reintento con backoff en fallos transitorios, cero reintentos en
  violaciones de constraint, apertura del circuito tras N fallos consecutivos,
  y que con el circuito abierto no se vuelve a invocar `IPublishEndpoint`
  (fail-fast real, no solo un fallo más).
