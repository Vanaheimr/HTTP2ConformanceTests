using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using org.GraphDefined.Vanaheimr.Hermod.HTTP2;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OurServer = org.GraphDefined.Vanaheimr.Hermod.HTTP2.HTTP2Server;

// Client-side RFC 9218 priority signaling: the client EMITS the priority
// header + PRIORITY_UPDATE; the server ACTING on them is proven separately by
// h2priority (raw). Here we verify correct emission (server sees the exact
// header value), that a client-sent PRIORITY_UPDATE is well-formed and accepted
// (connection stays healthy), and interop with .NET Kestrel.

var passed = 0;
var failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"  ✓ {name}" + (detail.Length > 0 ? $"  ({detail})" : "")); }
    else    { failed++; Console.WriteLine($"  ✗ {name}" + (detail.Length > 0 ? $"  — {detail}" : "")); }
}

static X509Certificate2 MakeCert()
{
    using var rsa = RSA.Create(2048);
    var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var san = new SubjectAlternativeNameBuilder();
    san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); san.AddIpAddress(IPAddress.IPv6Loopback);
    req.CertificateExtensions.Add(san.Build());
    var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.UserKeySet);
}
RemoteCertificateValidationCallback acceptAny = (_, _, _, _) => true;

// =========================================================================
// 0. Frame factory sanity (no network)
// =========================================================================
Console.WriteLine("=== PRIORITY_UPDATE frame factory ===");
{
    var frame = HTTP2Frame.CreatePriorityUpdate(7, "u=0, i");
    var bytes = frame.Serialize();
    var parsed = HTTP2Frame.ParseHeader(bytes.AsSpan(0, 9));
    var payload = bytes[9..];
    var sid = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4)) & 0x7FFFFFFFu;
    var val = Encoding.ASCII.GetString(payload.AsSpan(4));
    Check("type = PRIORITY_UPDATE (0x10)", parsed.Type == HTTP2FrameType.PRIORITY_UPDATE);
    Check("frame stream id = 0", parsed.StreamId == 0);
    Check("prioritized stream id = 7", sid == 7, sid.ToString());
    Check("priority field value = 'u=0, i'", val == "u=0, i", val);

    Check("priority header encoding u=0,i", new HTTP2Priority(0, true).ToHeaderValue() == "u=0, i");
    Check("priority header encoding u=5", new HTTP2Priority(5, false).ToHeaderValue() == "u=5");
}

// =========================================================================
// 1. our client -> our server
// =========================================================================
Console.WriteLine("\n=== our client -> our server ===");
{
    const int port = 9448;

    Task<(List<(string Name, string Value)>, byte[]?)> Origin(
        uint sid, List<(string Name, string Value)> h, byte[]? body, CancellationToken ct)
    {
        var path     = h.First(x => x.Name == ":path").Value;
        var priority = h.FirstOrDefault(x => x.Name == "priority").Value ?? "none";

        if (path == "/slow")
            return SlowOk(ct);

        var reply = Encoding.UTF8.GetBytes(priority);   // echo the received priority header
        return Task.FromResult<(List<(string, string)>, byte[]?)>(
            ([(":status", "200"), ("content-length", reply.Length.ToString())], reply));

        static async Task<(List<(string, string)>, byte[]?)> SlowOk(CancellationToken ct)
        {
            await Task.Delay(500, ct);
            var b = Encoding.UTF8.GetBytes("slow-ok");
            return ([(":status", "200"), ("content-length", b.Length.ToString())], b);
        }
    }

    var server = new OurServer(IPAddress.Loopback, port, MakeCert(), Origin);
    var serverTask = server.RunAsync();
    await Task.Delay(400);

    var conn = await HTTP2Client.ConnectAsync("localhost", port, acceptAny);

    // The typed Priority API must land as the exact 'priority' header value.
    var p0 = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/echo", Priority: new HTTP2Priority(0, true));
    Check("Priority u=0,i -> header 'u=0, i' at server", Encoding.UTF8.GetString(p0.Body) == "u=0, i", Encoding.UTF8.GetString(p0.Body));

    var p5 = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/echo", Priority: new HTTP2Priority(5, false));
    Check("Priority u=5 -> header 'u=5' at server", Encoding.UTF8.GetString(p5.Body) == "u=5", Encoding.UTF8.GetString(p5.Body));

    var pNone = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/echo");
    Check("no Priority -> no priority header", Encoding.UTF8.GetString(pNone.Body) == "none", Encoding.UTF8.GetString(pNone.Body));

    // PRIORITY_UPDATE mid-flight: start a slow request, get its stream id,
    // reprioritize it, then await the response. A well-formed PRIORITY_UPDATE
    // keeps the connection healthy (a malformed one would GOAWAY).
    var handle = await conn.StartRequestAsync("GET", "https", $"localhost:{port}", "/slow");
    Check("StartRequestAsync exposes an odd stream id", handle.StreamId % 2 == 1, handle.StreamId.ToString());
    await conn.UpdatePriorityAsync(handle.StreamId, new HTTP2Priority(0, false));
    var slow = await handle.Response;
    Check("request completes after mid-flight PRIORITY_UPDATE", slow.Status == 200, slow.Status.ToString());

    var after = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/echo");
    Check("connection healthy after PRIORITY_UPDATE", after.Status == 200, after.Status.ToString());

    await conn.CloseAsync();
    await server.StopAsync();
    try { await serverTask; } catch { }
}

// =========================================================================
// 2. our client -> .NET Kestrel (interop: signals don't break a real server)
// =========================================================================
Console.WriteLine("\n=== our client -> .NET Kestrel ===");
{
    const int port = 9449;
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(port, l => { l.Protocols = HttpProtocols.Http2; l.UseHttps(MakeCert()); }));
    var app = builder.Build();
    app.MapGet("/", () => "kestrel-ok");
    await app.StartAsync();
    await Task.Delay(400);

    var conn = await HTTP2Client.ConnectAsync("localhost", port, acceptAny);

    var withPriority = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/", Priority: new HTTP2Priority(1, false));
    Check("Kestrel accepts a priority-hinted request", withPriority.Status == 200 && Encoding.UTF8.GetString(withPriority.Body) == "kestrel-ok",
          withPriority.Status.ToString());

    var handle = await conn.StartRequestAsync("GET", "https", $"localhost:{port}", "/");
    await conn.UpdatePriorityAsync(handle.StreamId, new HTTP2Priority(0, false));
    var resp = await handle.Response;
    Check("Kestrel accepts a client PRIORITY_UPDATE", resp.Status == 200, resp.Status.ToString());
    var follow = await conn.SendRequestAsync("GET", "https", $"localhost:{port}", "/");
    Check("Kestrel connection healthy afterward", follow.Status == 200, follow.Status.ToString());

    await conn.CloseAsync();
    await app.StopAsync();
}

Console.WriteLine($"\n=== {passed}/{passed + failed} checks passed ===");
Environment.Exit(failed == 0 ? 0 : 1);
