using System.Net;

// Downloads of Fortnite's streamed texture chunks. They only improve texture quality, so they must never break
// anything: after three failures in a row it stops trying (every later request fails at once and the textures
// use the mips already on disk), and it tells the app why once.
sealed class StreamGuardHandler : DelegatingHandler
{
    int failures;
    int told;

    public StreamGuardHandler() : base(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None
    })
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref failures) >= 3)
            throw new HttpRequestException("streamed texture downloads are switched off after repeated failures");

        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                Volatile.Write(ref failures, 0);
            else
                Failed("HTTP " + (int)response.StatusCode);
            return response;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Failed(e.Message);
            throw;
        }
    }

    void Failed(string why)
    {
        if (Interlocked.Increment(ref failures) < 3 || Interlocked.Exchange(ref told, 1) != 0) return;
        Console.WriteLine("status: Streamed textures could not be downloaded (" + why.Replace('\r', ' ').Replace('\n', ' ') + "), using the low-resolution copies");
        Console.Out.Flush();
    }
}
