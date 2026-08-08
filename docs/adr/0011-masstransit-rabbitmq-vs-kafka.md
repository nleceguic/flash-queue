# ADR 0011: RabbitMQ (vía MassTransit) vs. Kafka para publicar eventos de dominio

- **Fecha**: 2026-08-07
- **Estado**: Aceptada

## Contexto

Una vez resuelta una reserva, el resto de la plataforma (Pagos,
Notificaciones, Analítica) necesita enterarse, desacoplada del motor de
reservas. El ADR 0003 documenta el **cómo** (estructura de MassTransit,
DI, dead-lettering); este ADR es el **porqué** de la tecnología de broker.

## Decisión

RabbitMQ vía MassTransit. El patrón de mensajería que FlashQueue necesita
es fanout de eventos discretos de negocio (`ReservationConfirmed` /
`ReservationRejected`) a un número pequeño y conocido de consumidores
independientes que ya existen en el momento de publicar — sin necesidad de
releer el historial completo ni de particionar por clave a gran escala.

## Alternativas descartadas

- **Kafka**: pensado para volumen sostenido muy alto y, sobre todo, para
  releer el log (replay, consumidores que llegan tarde y quieren el
  historial completo, procesamiento por streaming). FlashQueue no necesita
  ninguna de las dos cosas — cada evento se consume una vez, por
  consumidores que ya están suscritos cuando se publica. Kafka añadiría la
  complejidad operativa de gestionar particiones/topics/consumer groups
  sin resolver un problema real de este caso de uso.
- **Llamadas HTTP directas de `Workers` a cada consumidor, sin broker**:
  acopla el motor de reservas a la disponibilidad de los tres servicios en
  el instante exacto de la publicación (¿falla la reserva si Pagos está
  caído?) y obliga a `Workers` a conocer la dirección de cada uno — el
  acoplamiento que CLAUDE.md pide evitar explícitamente.
- **Azure Service Bus / AWS SQS+SNS**: viables, pero atan el despliegue a
  un proveedor cloud concreto; RabbitMQ corre igual de bien self-hosted
  (el objetivo declarado del brief: servidor Ubuntu propio) que en
  cualquier cloud.

## Consecuencias

- Dead-lettering y reintentos quedan a cargo del transporte RabbitMQ de
  MassTransit (ver ADR 0003), no de código propio.
- Si en el futuro algún consumidor necesitara replay o streaming real,
  esta decisión habría que revisarla — es la correcta para el escenario
  actual, no una elección universal.
