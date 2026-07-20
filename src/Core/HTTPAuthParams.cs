namespace org.GraphDefined.Vanaheimr.Hermod.HTTP2;

using System.Text;

/// <summary>
/// Shared parser for an RFC 7235 auth-param list — comma-separated
/// <c>key=value</c> pairs, values optionally double-quoted (with backslash
/// escapes and embedded commas). Used by the Digest (RFC 7616) and Token
/// schemes, both of which carry their credentials as such a list.
/// </summary>
internal static class HTTPAuthParams
{
    public static Dictionary<string, string> Parse(string Credentials)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i      = 0;
        var n      = Credentials.Length;

        while (i < n)
        {
            while (i < n && (Credentials[i] == ' ' || Credentials[i] == ','))
                i++;

            var keyStart = i;
            while (i < n && Credentials[i] != '=')
                i++;
            if (i >= n)
                break;

            var key = Credentials[keyStart..i].Trim();
            i++;   // skip '='

            string value;
            if (i < n && Credentials[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < n && Credentials[i] != '"')
                {
                    if (Credentials[i] == '\\' && i + 1 < n) { sb.Append(Credentials[i + 1]); i += 2; }
                    else                                     { sb.Append(Credentials[i]);     i++;    }
                }
                i++;   // skip closing quote
                value = sb.ToString();
            }
            else
            {
                var valStart = i;
                while (i < n && Credentials[i] != ',')
                    i++;
                value = Credentials[valStart..i].Trim();
            }

            if (key.Length > 0)
                result[key] = value;
        }

        return result;
    }
}
