using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Altinn.Apps.Monitoring.Application;

#pragma warning disable CA1724 // Type name conflicts with namespace name
internal sealed record Query
#pragma warning restore CA1724 // Type name conflicts with namespace name
{
    public string Name { get; }
    public QueryType Type { get; }
    public string QueryTemplate { get; }
    public string Hash { get; }

    public string Format(Instant searchFrom, Instant searchTo) =>
        // Default instant string format is ISO 8601
        string.Format(CultureInfo.InvariantCulture, QueryTemplate, searchFrom.ToString(), searchTo.ToString());

    public Query(string name, QueryType type, string queryTemplate)
    {
        Name = name;
        Type = type;
        QueryTemplate = queryTemplate;
        Hash = HashQuery(queryTemplate);
    }

    private static string HashQuery(string queryTemplate)
    {
        var hash = XxHash128.Hash(Encoding.UTF8.GetBytes(queryTemplate));
        return Convert.ToBase64String(hash);
    }
}
