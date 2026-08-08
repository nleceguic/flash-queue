# ADR 0006: Instrumentación con OpenTelemetry — propagación de traza por el channel y Collector como fan-out

- **Fecha**: 2026-08-07
- **Estado**: Aceptada. Ambas decisiones de este ADR (propagación de traza por el item del
  channel, Collector como fan-out) siguen vigentes sin cambios — un `Channel<T>` sigue sin
  propagar `Activity.Current` esté o no en el mismo proceso que su lector. Lo que sí queda
  superado es el dato de contexto mencionado más abajo ("cada proceso tiene su propia instancia de
  `ReservationIngestChannel`"): desde [ADR 0013](0013-api-y-workers-no-comparten-el-channel-de-ingesta.md)
  vuelve a haber una única instancia por sistema, no una por proceso.

## Contexto

FlashQueue necesita una traza distribuida real desde que la petición HTTP
entra en `FlashQueue.Api` hasta que el evento de dominio se publica en
RabbitMQ, pasando por el channel de ingesta, `ReservationProcessingWorker` y
Postgres — no trazas sueltas por componente que solo coinciden por
casualidad en el tiempo, sino una única traza conectada que se pueda seguir
de principio a fin en Tempo. Esto obliga a resolver dos problemas
distintos: cómo sobrevive el contexto de traza al cruzar un
`Channel<T>` en memoria (un boundary asíncrono que no tiene ninguna
relación causal automática con el `Activity.Current` de quien escribió en
el channel), y a qué apunta el exportador OTLP de la aplicación dado que
las trazas y las métricas no pueden ir al mismo sitio.

## Decisión 1: `ReservationIngestItem` — la traza viaja con el item por el channel

Un `Channel<T>` no propaga `Activity.Current`: el bucle de lectura de
`ReservationProcessingWorker` corre en un `Task` de fondo completamente
ajeno al flujo async de la petición HTTP que originó el item. Sin hacer
nada al respecto, cualquier span creado al procesar la reserva sería una
traza nueva sin relación con la petición HTTP que la originó — justo el
punto que esta instrumentación existe para demostrar.

La solución: `FlashQueue.Application.Ingestion.ReservationIngestItem` es
un `record` que envuelve `ReservationRequest` (el dominio puro, sin
cambios) junto con un `ActivityContext` capturado en
`ReservationsEndpoints.CreateReservationAsync` en el momento exacto de
encolar (dentro de un span propio, `reservation.enqueue`, hijo del span de
ASP.NET Core auto-instrumentado de esa petición). `ReservationIngestChannel`
pasó de `Channel<ReservationRequest>` a `Channel<ReservationIngestItem>`.
Al otro lado, `ReservationProcessingWorker.ProcessAsync` usa ese contexto
como `parentContext` al abrir `reservation.process`
(`FlashQueueDiagnostics.ActivitySource.StartActivity(..., item.TraceContext)`),
reconectando la traza como un hijo real, no como una traza nueva.

`ActivityContext` es un tipo del BCL (`System.Diagnostics`), no del SDK de
OpenTelemetry — usarlo en `FlashQueue.Application` no viola "sin
dependencias externas" en el sentido que le da CLAUDE.md a esa capa (nada
de paquetes NuGet). El propio `ReservationRequest` (dominio) no sabe nada
de esto: la ingesta es responsabilidad de Application, no del dominio.

### Alternativas descartadas

- **Diccionario de correlación aparte** (`ConcurrentDictionary<Guid, ActivityContext>`
  poblado al encolar, consultado al desencolar): evita cambiar el tipo del
  channel, pero añade un segundo mecanismo de sincronización mutable con
  que limpiar (¿qué pasa si un item nunca se desencola?) por ahorrarse un
  cambio de tipo mecánico. El envoltorio es más simple de razonar: el
  contexto viaja con el dato, no en una estructura separada que hay que
  mantener sincronizada a mano.
- **Solo un tag de correlación (`reservation.id`) en spans desconectados**:
  cumple "se puede buscar por id" pero no "traza distribuida" — en Tempo
  aparecerían como trazas independientes, no como una única traza
  navegable de principio a fin. No es lo que se pidió.

## Decisión 2: el endpoint OTLP de la app apunta a un Collector, no a Tempo/Prometheus directamente

`Observability:OtlpEndpoint` es un único endpoint configurable, y tanto
`WithTracing` como `WithMetrics` (`ObservabilityServiceCollectionExtensions.AddObservability`)
exportan ahí. Pero Tempo solo entiende OTLP de trazas — no acepta
métricas — y Prometheus no tiene un receptor OTLP a secas: necesita
remote-write (`--web.enable-remote-write-receiver`). Ningún endpoint único
sirve a la vez de receptor OTLP de trazas y de receptor remote-write de
métricas.

`docker-compose.observability.yml` añade un **OpenTelemetry Collector**
(`otel/opentelemetry-collector-contrib`) como única pieza que recibe OTLP
(trazas + métricas, un solo puerto 4317/4318) y reparte cada señal por su
pipeline: `otlp/tempo` para trazas, `prometheusremotewrite` para métricas.
La app nunca necesita saber que existen dos backends distintos con
protocolos distintos — solo conoce un endpoint OTLP, que es exactamente lo
que se pidió ("expórtalo vía OTLP a un endpoint configurable").

Verificado end-to-end antes de dar esto por cerrado: se levantó el stack
completo, se corrió `FlashQueue.Api` apuntando a `http://localhost:4317`,
y se confirmó en los logs del Collector (exportador `debug`) que las
trazas de una petición HTTP real llegaban y se reenviaban, y que el gauge
`flashqueue.reservation_channel.size` aparecía en Prometheus como
`flashqueue_reservation_channel_size` (los puntos del nombre se convierten
en guiones bajos; una unidad "de anotación" como `{item}` no añade sufijo).
No se pudo verificar de la misma forma el sufijo exacto que
`prometheusremotewrite` da al Counter (`flashqueue.reservations.processed`)
y al Histogram (`flashqueue.reservations.processing_duration`) por una
limitación del entorno de verificación, no por un fallo del pipeline —
las convenciones de Counter (`_total`) e Histogram (`_bucket`/`_sum`/`_count`)
usadas en el dashboard son las estándar y estables del exporter
`prometheusremotewrite`, pero si algún panel no muestra datos al primer
arranque, comprobar el nombre exacto en Prometheus (`/graph`, autocompletado,
o `Status → Target metadata`) es el primer paso, documentado en el README.

### Alternativas descartadas

- **Dos endpoints configurables en la app** (uno para trazas OTLP/gRPC a
  Tempo, otro para métricas OTLP/HTTP a Prometheus): funciona, pero
  contradice literalmente "un endpoint configurable" del encargo, y
  además acopla la aplicación a los detalles de protocolo de cada backend
  (gRPC vs. HTTP, rutas específicas) en vez de dejar esa traducción en la
  capa de infraestructura de observabilidad donde pertenece.
- **Prometheus con `--enable-feature=otlp-write-receiver` en vez de
  remote-write**: es una alternativa más nueva y también válida (evitaría
  necesitar el exporter `prometheusremotewrite` en el Collector, usando
  `otlphttp` hacia Prometheus directamente), pero remote-write es la ruta
  más madura y ampliamente documentada; se prefiere por estabilidad.

## Consecuencias

- La app (`FlashQueue.Api`, `FlashQueue.Workers`) sigue sin saber nada de
  Tempo ni de Prometheus — solo publica OTLP a un endpoint. Cambiar de
  backend de observabilidad (p. ej. a un SaaS) es un cambio de
  configuración del Collector, no de la app.
- `ReservationIngestItem` es ahora lo que realmente circula por
  `ReservationIngestChannel`; cualquier código nuevo que escriba o lea del
  channel (incluidos tests) tiene que conocer el envoltorio. Los tests
  existentes que no necesitan trazabilidad simplemente pasan
  `default(ActivityContext)`.
- Grafana viene con Tempo y Prometheus pre-provisionados como datasources
  y el dashboard `flashqueue-overview.json` autoprovisionado — no hace
  falta importar nada a mano tras `docker compose up`.
