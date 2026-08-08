using System.Diagnostics;
using FlashQueue.Domain.Entities;

namespace FlashQueue.Application.Ingestion;

/// <summary>La petición más el contexto de traza capturado al encolar, ya que el channel no propaga <see cref="Activity.Current"/>.</summary>
public sealed record ReservationIngestItem(ReservationRequest Request, ActivityContext TraceContext);
