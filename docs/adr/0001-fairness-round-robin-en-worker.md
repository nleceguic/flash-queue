# ADR 0001: Fairness por EventId en ReservationProcessingWorker

- **Fecha**: 2026-08-07
- **Estado**: Aceptada

## Contexto

`ReservationProcessingWorker` consume `ReservationIngestChannel` (un único
`Channel<ReservationRequest>` compartido por todos los eventos) con concurrencia
acotada por `SemaphoreSlim`. Si se procesara estrictamente en el orden de llegada
del canal, un evento con mucho tráfico (p. ej. 5.000 peticiones de un "drop" muy
popular) podría acaparar todos los permisos de concurrencia y dejar sin servicio
a otros eventos activos en paralelo hasta vaciarse por completo. Necesitamos
fairness por `EventId` sin introducir un canal físico por evento (que sería
difícil de acotar en memoria: el número de eventos activos no está limitado de
antemano).

## Decisión

Un único dispatcher central mantiene:

- Una `ConcurrentQueue<ReservationRequest>` interna por `EventId`
  (`ConcurrentDictionary<Guid, ConcurrentQueue<ReservationRequest>>`), donde se
  acumulan las peticiones de ese evento en orden de llegada.
- Un canal `Channel<Guid>` no acotado (`_turns`) que representa una ronda
  round-robin: cada `EventId` aparece en él **como máximo una vez** mientras
  tenga trabajo pendiente ("activo"), controlado con
  `ConcurrentDictionary<Guid, byte> _activeEvents`.

El bucle de ingesta solo añade un `EventId` a la ronda la primera vez que pasa de
inactivo a activo. El bucle de despacho adquiere un permiso del semáforo de
concurrencia, toma el siguiente `EventId` de la ronda, retira **un único** item
de la cola de ese evento y, si a ese evento le queda trabajo, lo reinserta al
final de la ronda (si no, lo marca inactivo). El resultado es un round-robin real:
con 5.000 peticiones del evento A y 10 del B, cada uno recibe un turno por
vuelta, así que B nunca espera a que A se vacíe.

El permiso de concurrencia se adquiere **antes** de leer el siguiente turno de la
ronda, no después: así el dispatcher nunca "reserva" un turno mientras espera un
hueco libre, y el orden de la ronda refleja fielmente el orden de llegada de los
eventos.

## Alternativas descartadas

- **Un `Channel<T>` acotado por evento + un dispatcher que hace round-robin entre
  canales**: funcionalmente equivalente, pero requiere gestionar la creación y
  el cierre de un canal por cada `EventId` que aparece (y su limpieza cuando el
  evento deja de tener tráfico), además de decidir una capacidad por canal. La
  cola interna sin acotar por evento es más simple porque no hay backpressure
  por evento que gestionar (el backpressure real ya lo aplica el `Channel`
  bounded de ingesta) y no hay que destruir infraestructura cuando un evento se
  agota.
- **Prioridad estricta / ponderada por evento**: más compleja de razonar y de
  probar, y no la pide el requisito (que es fairness, no priorización).
- **Un `SemaphoreSlim` por evento en vez de uno global**: no limitaría la
  presión total sobre la base de datos, que es el objetivo del límite de
  concurrencia (evitar saturar Postgres), solo la presión por evento.

## Consecuencias

- La estructura interna de colas por evento crece con el número de `EventId`
  distintos vistos desde el arranque del worker y nunca se libera (las colas
  vacías permanecen en el diccionario). Aceptable para el alcance actual del
  proyecto; si se convirtiera en un problema real se podría añadir una limpieza
  periódica de colas vacías con marca de "inactivo" antigua.
- La sección de doble comprobación en `CompleteTurn` (comprobar vacío → retirar
  marca de activo → volver a comprobar vacío) es necesaria para no perder el
  turno de un evento si un productor encola justo en ese instante; es el único
  punto de sincronización no trivial del worker y está documentado en el código.
- El drenado en shutdown (`DrainAsync`) reutiliza el mismo semáforo de
  concurrencia: esperar a adquirir todos sus permisos, con timeout, equivale a
  esperar a que termine todo el trabajo en vuelo sin necesidad de llevar una
  lista aparte de tareas activas.
