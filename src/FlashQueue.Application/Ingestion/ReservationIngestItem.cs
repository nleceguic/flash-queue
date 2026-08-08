using System.Diagnostics;
using FlashQueue.Domain.Entities;

namespace FlashQueue.Application.Ingestion;

/// <summary>
/// Lo que realmente viaja por <see cref="ReservationIngestChannel"/>: la petición de dominio más
/// el contexto de traza (W3C trace/span id) capturado en el momento de encolar. Un
/// <see cref="System.Threading.Channels.Channel{T}"/> no propaga <see cref="Activity.Current"/>
/// automáticamente — el lector corre en un bucle de fondo completamente ajeno al flujo async de
/// la petición HTTP original — así que sin este envoltorio, la traza distribuida se rompería
/// justo en el punto que existe para demostrar (HTTP → channel → worker → Postgres/RabbitMQ).
/// <see cref="Request"/> (el dominio puro) no sabe nada de esto: es <c>FlashQueue.Application</c>,
/// no <c>FlashQueue.Domain</c>, quien conoce el mecanismo de ingesta.
/// </summary>
public sealed record ReservationIngestItem(ReservationRequest Request, ActivityContext TraceContext);
