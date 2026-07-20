namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

/// <summary>The usability verdict plus the response's current age (added as the <c>Age</c> header when served).</summary>
public readonly record struct HTTPCacheDecision(HTTPCacheUsability Usability, TimeSpan Age);
