namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;


/// <summary>
/// Callback delegate invoked when a complete HTTP/2 request has been received
/// (all headers + optional body). The handler returns response headers and an
/// optional body. This is the app-logic seam where an existing HTTP/1.1 handler
/// plugs in; <see cref="HTTPSemantics"/> also produces one of these.
///
/// Version-independent (RFC 9110 request/response shape), so it lives in the
/// shared library rather than the server: a future H1/H3 server, or an
/// abstraction shared with the client, can reuse the exact same seam.
///
/// CancellationToken is cancelled if the peer sends RST_STREAM for this specific
/// stream while the handler is still running — a long-running handler should
/// observe it (e.g. pass it to any awaited I/O) instead of running to completion
/// for a client that already walked away.
/// </summary>
public delegate Task<(List<(string Name, string Value)> ResponseHeaders, byte[]? ResponseBody)>
    HTTP2RequestHandler(
        UInt32                            StreamId,
        List<(string Name, string Value)> RequestHeaders,
        byte[]?                           RequestBody,
        CancellationToken                 CancellationToken
    );
