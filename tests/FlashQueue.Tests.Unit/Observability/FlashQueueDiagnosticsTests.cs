using System.Diagnostics.Metrics;
using System.Reflection;
using FlashQueue.Application.Observability;
using FluentAssertions;

namespace FlashQueue.Tests.Unit.Observability;

/// <summary><see cref="FlashQueueDiagnostics"/> es la identidad de instrumentación compartida de FlashQueue.</summary>
public class FlashQueueDiagnosticsTests
{
    [Fact]
    public void ActivitySource_HasExpectedNameAndVersion()
    {
        FlashQueueDiagnostics.ActivitySource.Name.Should().Be("FlashQueue");
        FlashQueueDiagnostics.ActivitySource.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void Meter_HasExpectedNameAndVersion()
    {
        FlashQueueDiagnostics.Meter.Name.Should().Be("FlashQueue");
        FlashQueueDiagnostics.Meter.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void ActivitySourceAndMeter_ShareTheSameNameAs_FlashQueueDiagnosticsName()
    {
        FlashQueueDiagnostics.Name.Should().Be("FlashQueue");
        FlashQueueDiagnostics.ActivitySource.Name.Should().Be(FlashQueueDiagnostics.Name);
        FlashQueueDiagnostics.Meter.Name.Should().Be(FlashQueueDiagnostics.Name);
    }

    [Fact]
    public void ObserveChannelSize_TracksTheFirstRegisteredProvider_AndIgnoresLaterCalls()
    {
        // Un solo test a propósito: Meter es estático y no se puede "desregistrar" un gauge, así
        // que un segundo test seguiría viendo el instrumento creado por este.
        ResetRegistrationGuard();

        using var listener = new MeterListener();
        var observed = new List<int>();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == FlashQueueDiagnostics.Name
                && instrument.Name == "flashqueue.reservation_channel.size")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, measurement, _, _) => observed.Add(measurement));
        listener.Start();

        FlashQueueDiagnostics.ObserveChannelSize(() => 7);
        FlashQueueDiagnostics.ObserveChannelSize(() => 999);

        listener.RecordObservableInstruments();

        observed.Should().ContainSingle().Which.Should().Be(7);
    }

    private static void ResetRegistrationGuard()
    {
        typeof(FlashQueueDiagnostics)
            .GetField("_channelSizeGaugeRegistered", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, false);
    }
}
