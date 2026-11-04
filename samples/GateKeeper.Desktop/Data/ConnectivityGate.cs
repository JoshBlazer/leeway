namespace GateKeeper.Desktop.Data;

/// <summary>An HTTP pipeline handler behind the "simulate no signal" switch in the UI.</summary>
public sealed class ConnectivityGate : DelegatingHandler
{
    public bool SimulateNoSignal { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => SimulateNoSignal
            ? throw new HttpRequestException("No signal (simulated).")
            : base.SendAsync(request, cancellationToken);
}
