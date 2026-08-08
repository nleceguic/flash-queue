# ADR 0005: Modo caos activable por `CHAOS_MODE`

- **Fecha**: 2026-08-07
- **Estado**: Aceptada

## Contexto

CLAUDE.md (sección 2, punto 8) pide un modo "caos" activable por variable de
entorno que inyecte latencia y fallos aleatorios en Postgres y RabbitMQ, "para
demostrar degradación controlada bajo fallo" — es decir, para poder disparar en
caliente, durante una demo, el retry de `ReservationRepository` y el circuit
breaker de `ReservationEventPublisher` (ambos de
docs/adr/0004-polly-retry-postgres-circuit-breaker-rabbitmq.md) sin depender de
que Postgres o RabbitMQ fallen de verdad.

El requisito no negociable es que, con el modo caos desactivado (que es el caso
por defecto, y el único caso en producción), el coste sea cero — "ni siquiera
un if innecesario en el hot path".

## Decisión

`IChaosInjector` (`FlashQueue.Infrastructure/Chaos/`) expone dos métodos,
`BeforePostgresCallAsync` y `BeforeRabbitMqPublishAsync`, llamados
incondicionalmente al principio de `ReservationRepository.ReserveCoreAsync` y
dentro del delegado que ejecuta `ReservationEventPublisher.PublishAsync`
respectivamente. Tiene dos implementaciones:

- `NullChaosInjector`: no-op puro, cada método es `=> Task.CompletedTask`. Se
  registra siempre que `CHAOS_MODE` no sea exactamente `"true"` — ausente,
  vacío, `"false"` o cualquier otro valor.
- `RandomChaosInjector`: la implementación real. Antes de la llamada, espera
  una latencia aleatoria uniforme en `[MinLatency, MaxLatency]` (100–2000ms por
  defecto) y, con la probabilidad configurada (5% Postgres, 10% RabbitMQ), lanza
  un fallo artificial. Se registra solo cuando `CHAOS_MODE=true`.

**La decisión de qué implementación usar se toma una única vez, en
`ChaosServiceCollectionExtensions.AddChaos`, al arrancar el proceso** — no en
cada llamada. El código que llama a `IChaosInjector` (`ReservationRepository`,
`ReservationEventPublisher`) no tiene ningún `if` sobre `CHAOS_MODE`: siempre
invoca la misma interfaz, y es la implementación resuelta por el contenedor de
DI la que decide si eso significa "no hacer nada" o "inyectar caos". Esto es lo
que permite cumplir "cero overhead, ni siquiera un if innecesario en el hot
path": con el modo desactivado, el "overhead" se reduce a una llamada virtual a
un método de una línea que devuelve una `Task` ya completada — no hay
condicional, asignación de memoria (más allá de la ya existente para la propia
`Task`) ni comprobación de configuración en el camino de cada reserva o
publicación. La alternativa de `#if CHAOS_MODE` (compilación condicional) se
descarta porque `CHAOS_MODE` es una variable de **entorno**, evaluada en
tiempo de ejecución, no de compilación — no hay forma de usar directivas de
preprocesador para algo que se decide al arrancar el proceso, no al compilarlo.

### Dónde se inyecta exactamente

- **Postgres**: al principio de `ReserveCoreAsync`, el método que
  `ReservationRepository.ReserveAsync` envuelve en el pipeline de reintento de
  ADR 0004. Como cada reintento vuelve a ejecutar `ReserveCoreAsync` desde
  cero, un fallo inyectado por el caos en el primer intento se comporta
  exactamente como lo haría un fallo transitorio real: el segundo intento
  vuelve a pasar por el chaos injector (y puede fallar o no, según la
  probabilidad configurada) antes de tocar Postgres de verdad. El fallo
  inyectado es un `NpgsqlException` con una `SocketException` como causa —
  `NpgsqlException.IsTransient` lo clasifica como transitorio, así que
  `PostgresResilience.IsTransientFailure` lo reintenta de verdad, no lo deja
  pasar directo como haría con una violación de constraint.
- **RabbitMQ**: dentro del delegado que `ReservationEventPublisher.PublishAsync`
  pasa al pipeline de circuit breaker + timeout, justo antes de
  `IBus.Publish`. Así, un fallo inyectado cuenta como un fallo real de cara al
  circuit breaker (contribuye a los 5 fallos consecutivos que lo abren) y la
  latencia inyectada también queda sujeta al timeout de 2s por intento — un
  caos con latencia mayor a 2s dispara `TimeoutRejectedException`, que también
  cuenta como fallo para el circuit breaker.

### Logging

Cada inyección (de latencia o de fallo) se loguea en `Warning` con el prefijo
literal `[CHAOS]` y el nombre de la dependencia afectada, tal como pide el
encargo — para poder grepear o simplemente leer la consola durante el vídeo
demo y ver exactamente cuándo el caos actuó.

## Alternativas descartadas

- **Un único método `IChaosInjector.MaybeInject(string dependencyName, ...)`
  genérico** en vez de dos métodos con nombre por dependencia: se descarta
  porque Postgres y RabbitMQ necesitan tipos de excepción distintos (una
  transitoria para Postgres, cualquiera para RabbitMQ) y probabilidades
  distintas (5% / 10%) — un único método necesitaría parámetros adicionales
  para expresar esas diferencias, sin ganar nada frente a dos métodos
  explícitos con la configuración ya incorporada.
- **Comprobar `CHAOS_MODE` dentro de `RandomChaosInjector`/en el propio
  método** en vez de decidir la implementación en el contenedor de DI: sería
  el "if innecesario en el hot path" que el encargo pide evitar explícitamente
  — cada llamada tendría que volver a evaluar la variable de entorno (o un
  campo cacheado), en vez de que la pregunta se responda una sola vez al
  arrancar.
- **Aplicar el caos también en los consumidores** (`FlashQueue.Consumers.*`):
  fuera de alcance — el retry/circuit breaker que este modo caos existe para
  ejercitar vive solo en `FlashQueue.Workers` (Postgres + publicación); los
  consumidores no tienen ningún mecanismo de resiliencia propio que demostrar
  todavía.

## Consecuencias

- `CHAOS_MODE` es una variable de entorno de verdad (leída vía
  `IConfiguration`, que ya incluye el proveedor de variables de entorno por
  defecto), no una clave de `appsettings.json` — así se puede activar/desactivar
  sin tocar archivos, ideal para un vídeo demo (`CHAOS_MODE=true dotnet run`).
  El resto de parámetros (rango de latencia, probabilidades) sí son
  configurables por `appsettings`/entorno bajo la sección `Chaos`, por
  consistencia con el resto del proyecto.
- `ReservationEventFanoutTests` y el resto de tests de integración que ya
  existían siguen construyendo sus hosts sin fijar `CHAOS_MODE`, así que se
  ejecutan con `NullChaosInjector` — determinismo intacto, el modo caos nunca
  interfiere con un test que no lo pide explícitamente.
- `ChaosServiceCollectionExtensionsTests` verifica, con una aserción de tipo Y
  una de comportamiento (400 llamadas a la implementación resuelta,
  cronometradas), que "ausente" y `"false"` producen exactamente el mismo
  resultado.
