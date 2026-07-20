namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

/// <summary>How a stored response may be used for the current request (result of <see cref="HTTPCache.Evaluate"/>).</summary>
public enum HTTPCacheUsability
{
    /// <summary>Fresh — serve directly, no origin contact.</summary>
    Fresh,
    /// <summary>Stale but the request permits it (max-stale) — serve as-is.</summary>
    Stale,
    /// <summary>Stale but within stale-while-revalidate — serve now, revalidate in the background.</summary>
    StaleWhileRevalidate,
    /// <summary>Must revalidate with the origin before use.</summary>
    MustRevalidate
}
