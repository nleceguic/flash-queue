# ADR 0009: `Channel<T>` bounded en memoria vs. cola externa para la ingesta

- **Fecha**: 2026-08-07
- **Estado**: Aceptada. La decisión en sí (`Channel<T>` en memoria frente a cola externa) sigue
  vigente sin cambios. La consecuencia de "cada proceso tiene su propio channel" que se documentaba
  más abajo queda resuelta por [ADR 0013](0013-api-y-workers-no-comparten-el-channel-de-ingesta.md):
  Api y Workers vuelven a compartir una única instancia, como en el diseño original de esta ADR.

## Contexto

La ingesta necesita backpressure real (limitar cuántas peticiones viven en
memoria a la vez, sin perder ninguna) delante de un pico de miles de
peticiones concurrentes, con el mínimo de latencia añadida al camino más
caliente del sistema — el que recibe el pico.

## Decisión

`System.Threading.Channels.Channel<T>` bounded, en memoria, dentro del
propio proceso `FlashQueue.Api`, con `BoundedChannelFullMode.Wait` (el
productor espera cuando está lleno, nunca descarta — ver ADR sobre las
alternativas de `FullMode` en el propio código de `ReservationsEndpoints`).
Cero infraestructura adicional, encolar cuesta microsegundos (memoria, no
red/serialización), y usa directamente las primitivas de concurrencia de
.NET que el proyecto existe para demostrar (CLAUDE.md, sección 1).

## Alternativas descartadas

- **Cola externa (Redis Streams / RabbitMQ / SQS) para la ingesta**: añade
  un salto de red y serialización justo en el camino más caliente, a
  cambio de durabilidad que este caso de uso no necesita — una petición
  perdida por un crash del proceso simplemente se reintenta desde el
  cliente, no es una operación financiera irreversible. También delega a
  un producto externo la pieza de concurrencia que el proyecto está
  pensado para demostrar con código propio.
- **Tabla `outbox`/`inbox` en Postgres con polling**: añade latencia de
  disco al camino de ingesta y compite por escritura con la misma base de
  datos que ya sirve la reserva.

## Consecuencias

- El channel vive en memoria de un único proceso: si ese proceso cae, las
  peticiones ya encoladas (no persistidas todavía) se pierden. Aceptable
  dado el argumento de reintento del cliente.
- **Histórico, ya corregido**: entre la dockerización del sistema (fase 10)
  y [ADR 0013](0013-api-y-workers-no-comparten-el-channel-de-ingesta.md),
  `Api` y `Workers` se desplegaron como procesos separados, cada uno con
  su **propio** channel sin relación entre sí — el test de carga con k6
  confirmó empíricamente el efecto (ver ADR 0006 y 0008). ADR 0013 lo
  corrige: ambos vuelven a compartir la única instancia que esta decisión
  siempre asumió.
