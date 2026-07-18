namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

using System.Threading.Channels;


/// <summary>
/// Server-side <see cref="IHTTP2RequestStream"/> over a single streaming
/// request's <see cref="HTTP2Stream"/>: request headers, the body pulled from
/// the stream's inbound channel as DATA frames arrive, and the trailers (if any)
/// once the body ends.
/// </summary>
internal sealed class HTTP2RequestStream : IHTTP2RequestStream
{

    private static readonly List<(string Name, string Value)> EmptyFields = [];

    private readonly HTTP2Stream                             stream;
    private readonly IReadOnlyList<(string Name, string Value)> headers;

    internal HTTP2RequestStream(HTTP2Stream Stream, IReadOnlyList<(string Name, string Value)> Headers)
    {
        stream  = Stream;
        headers = Headers;
    }

    public IReadOnlyList<(string Name, string Value)> Headers  => headers;

    public IReadOnlyList<(string Name, string Value)> Trailers => stream.Trailers ?? EmptyFields;

    public async ValueTask<byte[]?> ReadAsync(CancellationToken CancellationToken = default)
    {
        var reader = stream.RequestBodyChannel!.Reader;

        if (await reader.WaitToReadAsync(CancellationToken) && reader.TryRead(out var chunk))
            return chunk;

        return null;   // body ended (channel completed at END_STREAM)
    }

}


/// <summary>
/// Server-side <see cref="IHTTP2ResponseStream"/> — writes an incrementally
/// produced response over a single <see cref="HTTP2Stream"/>, reusing the
/// connection's HEADERS/DATA/trailer machinery (flow-controlled, prioritized,
/// HPACK-encoded under the write lock) so a streamed response is byte-identical
/// on the wire to a buffered one, just produced piece by piece.
/// </summary>
internal sealed class HTTP2ResponseStream : IHTTP2ResponseStream
{

    private readonly HTTP2Connection connection;
    private readonly HTTP2Stream     stream;

    private bool headersSent;
    private bool completed;

    internal HTTP2ResponseStream(HTTP2Connection Connection, HTTP2Stream Stream)
    {
        connection = Connection;
        stream     = Stream;
    }

    /// <summary>Whether the response HEADERS have gone out yet (drives error fallback).</summary>
    internal bool HeadersSent => headersSent;

    /// <summary>Whether the response has been completed (END_STREAM sent).</summary>
    internal bool Completed   => completed;

    public async Task WriteHeadersAsync(IEnumerable<(string Name, string Value)> Headers, CancellationToken CancellationToken = default)
    {
        if (headersSent)
            throw new InvalidOperationException("Response headers have already been sent");

        var list = Headers.ToList();
        connection.EnforceOutboundHeaderListSize(stream.StreamId, list);
        HTTP2Connection.ApplyResponsePriorityOverride(stream, list);

        headersSent = true;
        await connection.SendHeaderListAsync(stream.StreamId, list, EndStream: false);
    }

    public Task WriteAsync(byte[] Data, CancellationToken CancellationToken = default)
    {
        if (!headersSent)
            throw new InvalidOperationException("WriteHeadersAsync must be called before WriteAsync");
        if (completed)
            throw new InvalidOperationException("The response has already been completed");
        if (Data.Length == 0)
            return Task.CompletedTask;

        return connection.EnqueueOutboundAsync(stream, Data, EndStream: false);
    }

    public async Task CompleteAsync(IEnumerable<(string Name, string Value)>? Trailers = null, CancellationToken CancellationToken = default)
    {
        if (completed)
            return;

        // A handler that produced nothing still needs a valid response.
        if (!headersSent)
            await WriteHeadersAsync([(":status", "200")], CancellationToken);

        completed = true;

        var trailerList = Trailers?.ToList();

        if (trailerList is { Count: > 0 })
        {
            HTTP2Connection.ValidateOutboundTrailers(stream.StreamId, trailerList);
            await connection.EnqueueOutboundAsync(stream, [], EndStream: true, trailerList);
        }
        else
            await connection.EnqueueOutboundAsync(stream, [], EndStream: true);
    }

    /// <summary>Auto-complete when a handler returns without ending the response itself.</summary>
    internal Task EnsureCompletedAsync()
        => completed ? Task.CompletedTask : CompleteAsync();

}
