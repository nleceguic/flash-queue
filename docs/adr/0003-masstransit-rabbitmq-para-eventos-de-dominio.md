# ADR 0003: MassTransit sobre RabbitMQ para publicar eventos de dominio

- **Fecha**: 2026-08-07
- **Estado**: Aceptada

## Contexto

CLAUDE.md (sección 1) exige que el resto de la plataforma (Pagos, Notificaciones,
Analítica) se entere del resultado de una reserva sin conocer ni acoplarse a la
lógica interna del motor de reservas. Hasta ahora, `ReservationRepository` /
`PostgresReservationProcessor` resuelven una reserva (Confirmed/Rejected) pero
nadie fuera del proceso `FlashQueue.Workers` se entera del resultado.

## Decisión

- `FlashQueue.Infrastructure/Messaging/MessagingServiceCollectionExtensions.cs`
  centraliza la configuración de MassTransit sobre RabbitMQ
  (`AddRabbitMqMessaging`), reutilizada tanto por `FlashQueue.Workers` (solo
  publica) como por los tres `FlashQueue.Consumers.*` (solo consumen). El host
  de RabbitMQ es 100% configurable por `appsettings`/variables de entorno
  (sección `RabbitMq`: `Host`, `Port`, `VirtualHost`, `Username`, `Password`),
  sin nada hardcodeado.
- `PostgresReservationProcessor.ProcessAsync` publica `ReservationConfirmed` o
  `ReservationRejected` (`FlashQueue.Contracts.Events`) justo después de que
  `ReservationRepository.ReserveAsync` resuelve la reserva, usando
  `IPublishEndpoint` inyectado. La publicación vive en el processor (el borde
  entre el worker y la persistencia), no en `ReservationRepository`: el
  repositorio permanece una pieza de persistencia pura, sin saber nada de
  mensajería, más fácil de probar de forma aislada (ver
  `ReservationRepositoryOversellingTests`, que no necesita RabbitMQ).
- Cada `FlashQueue.Consumers.*` (Payments, Notifications, Analytics) es un
  Worker Service independiente y desplegable por separado, que solo referencia
  `FlashQueue.Infrastructure` (para `AddRabbitMqMessaging`) y
  `FlashQueue.Contracts` (para los tipos de evento) — nunca `Application` ni
  `Domain`, para que quede demostrado en el propio grafo de dependencias que no
  conocen el motor de reservas. Cada uno registra sus propios
  `IConsumer<ReservationConfirmed>` / `IConsumer<ReservationRejected>`, que en
  esta fase son stubs: solo loguean y simulan latencia con
  `Task.Delay(Random.Shared.Next(...))`.
- `AddRabbitMqMessaging` recibe un `serviceName` opcional (`"payments"`,
  `"notifications"`, `"analytics"`) usado como prefijo de
  `KebabCaseEndpointNameFormatter`. Es necesario porque los tres servicios
  registran una clase con el mismo nombre simple (`ReservationConfirmedConsumer`
  en namespaces distintos); sin el prefijo, el formateador de nombres de
  MassTransit generaría la misma cola para los tres y competirían por los
  mismos mensajes en vez de recibir cada uno su propia copia (fanout real vía
  el exchange que MassTransit crea por tipo de mensaje).
- Reintentos: `cfg.UseMessageRetry(retry => retry.Exponential(3, 200ms, 5s,
  500ms))` configurado a nivel de bus (aplica a todos los endpoints de
  recepción). Cuando un consumidor agota los 3 reintentos, el transporte
  RabbitMQ de MassTransit mueve automáticamente el mensaje a la cola
  `<nombre-cola>_error` (dead-letter) — comportamiento nativo del transporte
  una vez que el pipeline de reintentos se rinde, no requiere topología manual.

## Alternativas descartadas

- **Publicar desde `ReservationRepository` en la misma transacción SQL (outbox
  transaccional)**: sería la solución correcta para eliminar la ventana en la
  que la reserva se confirma en Postgres pero el proceso muere antes de
  publicar el evento (ver Consecuencias). Se descarta por ahora porque añade
  una tabla de outbox + un publicador en segundo plano, complejidad que no
  aporta al objetivo central del proyecto (demostrar concurrencia/async) tanto
  como para justificarla en esta fase; queda anotado como mejora futura.
- **Un único proyecto `FlashQueue.Consumers` con los tres consumidores en el
  mismo proceso**: más simple de ejecutar, pero contradice el requisito
  explícito de "consumidores independientes" (CLAUDE.md sección 2, punto 5) y
  no demuestra que un servicio pueda desplegarse, escalar o caerse sin afectar
  a los otros dos — que es justo lo que este ADR existe para demostrar.
- **Nombrar las colas manualmente con `ReceiveEndpoint("nombre", ...)` en vez
  del `serviceName` + `KebabCaseEndpointNameFormatter`**: funcionalmente
  equivalente, pero más verboso y hay que repetirlo por cada consumidor nuevo;
  el prefijo en el formateador se aplica automáticamente a cualquier consumidor
  que se añada más adelante en cada servicio.

## Consecuencias

- Existe una ventana de "doble escritura" entre el commit de Postgres y la
  publicación a RabbitMQ: si el proceso `FlashQueue.Workers` muere justo entre
  ambas operaciones, la reserva queda confirmada en base de datos pero el
  evento nunca se publica. Aceptable para el alcance de portfolio actual;
  mitigarlo del todo requeriría un outbox transaccional (ver alternativas
  descartadas).
- Añadir un cuarto consumidor (o un cuarto tipo de evento) es una operación
  aislada: un `FlashQueue.Consumers.*` nuevo, sin tocar `FlashQueue.Workers` ni
  a los otros consumidores — es la prueba directa del desacoplamiento que pide
  CLAUDE.md sección 1.
- El test de integración `ReservationEventFanoutTests` (Testcontainers, RabbitMQ
  real) es la verificación empírica de que confirmar una reserva hace llegar el
  evento a los tres servicios de forma independiente, no solo de que
  `IPublishEndpoint.Publish` se invoca.
