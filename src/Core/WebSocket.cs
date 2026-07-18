namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

using System.Buffers.Binary;
using System.Text;


/// <summary>
/// RFC 6455 WebSocket opcodes (Section 5.2).
/// </summary>
public enum WebSocketOpcode : byte
{
    Continuation = 0x0,
    Text         = 0x1,
    Binary       = 0x2,
    Close        = 0x8,
    Ping         = 0x9,
    Pong         = 0xA
}


/// <summary>
/// A complete (defragmented) application message surfaced to the caller by
/// <see cref="WebSocketConnection.ReceiveAsync"/>. Only ever Text or Binary —
/// control frames (ping/pong/close) are handled internally and never returned
/// as a message.
/// </summary>
public sealed class WebSocketMessage
{
    public required WebSocketOpcode Opcode  { get; init; }
    public required byte[]          Payload { get; init; }
}


/// <summary>
/// Raised for any violation of RFC 6455's framing rules (bad mask bit,
/// fragmented/oversized control frame, reserved bits set, oversized payload).
/// Caught by <see cref="WebSocketConnection.ReceiveAsync"/>, which answers
/// with a Close frame (code 1002, "protocol error") and ends the connection.
/// </summary>
public sealed class WebSocketProtocolException(string Message) : Exception(Message);


/// <summary>Which end of a WebSocket a <see cref="WebSocketConnection"/> is.</summary>
public enum WebSocketRole
{
    /// <summary>Server: sends unmasked frames, requires inbound frames to be masked.</summary>
    Server,
    /// <summary>Client: masks every outbound frame (RFC 6455 §5.3), requires inbound frames to be unmasked.</summary>
    Client
}


/// <summary>
/// RFC 6455 WebSocket framing (masking, opcodes, fragmentation, close
/// handshake) layered on top of an HTTP/2 extended-CONNECT tunnel
/// (RFC 8441) — there is no separate HTTP/1.1-style Upgrade handshake:
/// RFC 8441's :protocol pseudo-header already established this stream is a
/// WebSocket, so the very first bytes exchanged on the tunnel are WebSocket
/// frames.
///
/// Direction-aware via <see cref="WebSocketRole"/> (RFC 6455 Section 5.1):
/// a client masks every frame it sends and requires the ones it receives to be
/// unmasked; a server does the exact opposite. The masking direction is the
/// only thing that differs between the two ends — everything else (opcodes,
/// fragmentation, ping/pong, the close handshake) is identical.
/// </summary>
public sealed class WebSocketConnection
{

    /// <summary>
    /// Hard ceiling on a single frame's declared payload length. RFC 6455
    /// itself sets no limit, but an unbounded 64-bit length field taken
    /// straight off the wire is a memory-exhaustion vector — bounded generously
    /// enough for any reasonable message, small enough to fail fast otherwise.
    /// </summary>
    private const long MaxFramePayloadLength = 16 * 1024 * 1024;   // 16 MiB

    private readonly IHTTP2Tunnel   tunnel;
    private readonly WebSocketRole  role;

    /// <summary>Bytes read from the tunnel but not yet consumed by frame parsing.</summary>
    private byte[] buffer      = [];
    private int    bufferStart;

    private bool   closeSent;


    public WebSocketConnection(IHTTP2Tunnel Tunnel, WebSocketRole Role = WebSocketRole.Server)
    {
        tunnel = Tunnel;
        role   = Role;
    }


    #region Receiving

    /// <summary>
    /// Receive the next complete application message, transparently:
    ///  - reassembling fragmented messages (a Text/Binary start frame with
    ///    FIN=0 followed by one or more Continuation frames, RFC 6455
    ///    Section 5.4),
    ///  - answering Ping with Pong without surfacing either to the caller,
    ///  - completing the close handshake on a Close frame (echoing it back
    ///    per Section 5.5.1) and on any protocol violation.
    /// Returns null once the connection is closed — either a normal close
    /// handshake or the underlying tunnel simply ending.
    /// </summary>
    public async Task<WebSocketMessage?> ReceiveAsync(CancellationToken CancellationToken)
    {

        WebSocketOpcode? fragmentOpcode = null;
        List<byte>?      fragmentBuffer = null;

        while (true)
        {

            RawFrame? frame;

            try
            {
                frame = await ReadRawFrameAsync(CancellationToken);
            }
            catch (WebSocketProtocolException ex)
            {
                await CloseAsync(1002, ex.Message, CancellationToken);
                return null;
            }

            if (frame is null)
                return null;   // Tunnel ended without a close handshake

            switch (frame.Opcode)
            {

                case WebSocketOpcode.Ping:
                    await SendFrameAsync(WebSocketOpcode.Pong, frame.Payload, CancellationToken);
                    continue;

                case WebSocketOpcode.Pong:
                    continue;   // Unsolicited pong — nothing to do

                case WebSocketOpcode.Close:
                    await HandleCloseAsync(frame.Payload, CancellationToken);
                    return null;

                case WebSocketOpcode.Text:
                case WebSocketOpcode.Binary:

                    if (fragmentOpcode is not null)
                    {
                        await CloseAsync(1002, "Expected a continuation frame", CancellationToken);
                        return null;
                    }

                    if (frame.Fin)
                        return new WebSocketMessage { Opcode = frame.Opcode, Payload = frame.Payload };

                    fragmentOpcode = frame.Opcode;
                    fragmentBuffer = [.. frame.Payload];
                    continue;

                case WebSocketOpcode.Continuation:

                    if (fragmentOpcode is null)
                    {
                        await CloseAsync(1002, "Unexpected continuation frame", CancellationToken);
                        return null;
                    }

                    fragmentBuffer!.AddRange(frame.Payload);

                    if (!frame.Fin)
                        continue;

                    var message = new WebSocketMessage { Opcode = fragmentOpcode.Value, Payload = [.. fragmentBuffer] };
                    return message;

                default:
                    await CloseAsync(1002, "Unknown opcode", CancellationToken);
                    return null;

            }

        }

    }

    private async Task HandleCloseAsync(byte[] Payload, CancellationToken CancellationToken)
    {
        // RFC 6455, Section 5.5.1: a peer that receives a close frame MUST
        // send one back before closing the connection; echoing the status
        // code/reason it sent us is a valid reply.
        if (!closeSent)
            await SendFrameAsync(WebSocketOpcode.Close, Payload, CancellationToken);
    }

    #endregion


    #region Sending

    public Task SendTextAsync(string Text, CancellationToken CancellationToken)
        => SendFrameAsync(WebSocketOpcode.Text, Encoding.UTF8.GetBytes(Text), CancellationToken);

    public Task SendBinaryAsync(byte[] Data, CancellationToken CancellationToken)
        => SendFrameAsync(WebSocketOpcode.Binary, Data, CancellationToken);

    /// <summary>
    /// Initiate (or reply to) the close handshake. Safe to call more than
    /// once — only the first call actually sends a frame.
    /// </summary>
    public Task CloseAsync(ushort Code, string Reason, CancellationToken CancellationToken)
    {

        if (closeSent)
            return Task.CompletedTask;

        var reasonBytes = Encoding.UTF8.GetBytes(Reason);
        var payload      = new byte[2 + reasonBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, Code);
        reasonBytes.CopyTo(payload, 2);

        return SendFrameAsync(WebSocketOpcode.Close, payload, CancellationToken);

    }

    /// <summary>
    /// Serialize and send a single, unfragmented frame (RFC 6455 Section 5.2).
    /// A client masks the payload with a random 4-byte key (Section 5.3) and
    /// sets the MASK bit; a server sends it unmasked. We never fragment our own
    /// output.
    /// </summary>
    private async Task SendFrameAsync(WebSocketOpcode Opcode, byte[] Payload, CancellationToken CancellationToken)
    {

        if (Opcode == WebSocketOpcode.Close)
            closeSent = true;

        var mask     = role == WebSocketRole.Client;
        var maskFlag = mask ? 0x80 : 0x00;

        var header = new List<byte> { (byte) (0x80 | (byte) Opcode) };   // FIN=1

        if (Payload.Length <= 125)
        {
            header.Add((byte) (maskFlag | Payload.Length));
        }
        else if (Payload.Length <= 65535)
        {
            header.Add((byte) (maskFlag | 126));
            var ext = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(ext, (ushort) Payload.Length);
            header.AddRange(ext);
        }
        else
        {
            header.Add((byte) (maskFlag | 127));
            var ext = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(ext, (ulong) Payload.Length);
            header.AddRange(ext);
        }

        byte[] body;

        if (mask)
        {
            var key = new byte[4];
            System.Security.Cryptography.RandomNumberGenerator.Fill(key);
            header.AddRange(key);

            body = new byte[Payload.Length];
            for (var i = 0; i < Payload.Length; i++)
                body[i] = (byte) (Payload[i] ^ key[i % 4]);
        }
        else
            body = Payload;

        var frameBytes = new byte[header.Count + body.Length];
        header.CopyTo(frameBytes);
        body.CopyTo(frameBytes, header.Count);

        await tunnel.WriteAsync(frameBytes, CancellationToken);

    }

    #endregion


    #region Raw frame parsing

    private sealed record RawFrame(bool Fin, WebSocketOpcode Opcode, byte[] Payload);

    /// <summary>
    /// Read and unmask a single WebSocket frame (RFC 6455 Section 5.2).
    /// Returns null if the tunnel ended before a complete frame arrived.
    /// </summary>
    private async Task<RawFrame?> ReadRawFrameAsync(CancellationToken CancellationToken)
    {

        var header = await ReadExactAsync(2, CancellationToken);
        if (header is null)
            return null;

        var fin    = (header[0] & 0x80) != 0;
        var rsv    =  header[0] & 0x70;
        var opcode = (WebSocketOpcode) (header[0] & 0x0F);
        var masked = (header[1] & 0x80) != 0;
        var len7   =  header[1] & 0x7F;

        if (rsv != 0)
            throw new WebSocketProtocolException("Reserved bits must be 0 (no extension negotiated)");

        // RFC 6455, Section 5.1: a server MUST close on an unmasked client frame;
        // a client MUST close on a masked server frame.
        if (role == WebSocketRole.Server && !masked)
            throw new WebSocketProtocolException("Client frames must be masked");
        if (role == WebSocketRole.Client && masked)
            throw new WebSocketProtocolException("Server frames must not be masked");

        long payloadLength = len7;

        if (len7 == 126)
        {
            var ext = await ReadExactAsync(2, CancellationToken);
            if (ext is null) return null;
            payloadLength = BinaryPrimitives.ReadUInt16BigEndian(ext);
        }
        else if (len7 == 127)
        {
            var ext = await ReadExactAsync(8, CancellationToken);
            if (ext is null) return null;
            payloadLength = (long) BinaryPrimitives.ReadUInt64BigEndian(ext);

            if (payloadLength < 0)
                throw new WebSocketProtocolException("Payload length must not have the high bit set");
        }

        if (payloadLength > MaxFramePayloadLength)
            throw new WebSocketProtocolException($"Frame payload too large ({payloadLength} bytes)");

        // RFC 6455, Section 5.5: control frames must not be fragmented and
        // are capped at 125 bytes of payload.
        if (opcode is WebSocketOpcode.Close or WebSocketOpcode.Ping or WebSocketOpcode.Pong)
        {
            if (!fin)
                throw new WebSocketProtocolException("Control frames must not be fragmented");

            if (payloadLength > 125)
                throw new WebSocketProtocolException("Control frame payload must be <= 125 bytes");
        }

        byte[]? maskingKey = null;
        if (masked)
        {
            maskingKey = await ReadExactAsync(4, CancellationToken);
            if (maskingKey is null) return null;
        }

        var rawPayload = payloadLength > 0
                             ? await ReadExactAsync((int) payloadLength, CancellationToken)
                             : [];
        if (rawPayload is null) return null;

        byte[] payload;

        if (masked)
        {
            // RFC 6455, Section 5.3: unmask by XOR-ing each payload byte with the
            // masking key, cycling through its 4 bytes.
            payload = new byte[rawPayload.Length];
            for (var i = 0; i < payload.Length; i++)
                payload[i] = (byte) (rawPayload[i] ^ maskingKey![i % 4]);
        }
        else
            payload = rawPayload;

        return new RawFrame(fin, opcode, payload);

    }

    /// <summary>
    /// Read exactly Count bytes off the tunnel, buffering across multiple
    /// underlying reads — HTTP/2 DATA frame boundaries have no relationship
    /// to WebSocket frame boundaries, so a single tunnel chunk might contain
    /// less than one WS frame header, or several whole WS frames at once.
    /// Returns null if the tunnel ends before Count bytes are available.
    /// </summary>
    private async Task<byte[]?> ReadExactAsync(int Count, CancellationToken CancellationToken)
    {

        while (buffer.Length - bufferStart < Count)
        {

            var chunk = await tunnel.ReadAsync(CancellationToken);

            if (chunk is null)
                return null;

            if (bufferStart > 0)
            {
                buffer      = buffer[bufferStart..];
                bufferStart = 0;
            }

            var combined = new byte[buffer.Length + chunk.Length];
            buffer.CopyTo(combined, 0);
            chunk.CopyTo(combined, buffer.Length);
            buffer = combined;

        }

        var result = buffer[bufferStart..(bufferStart + Count)];
        bufferStart += Count;

        return result;

    }

    #endregion

}
