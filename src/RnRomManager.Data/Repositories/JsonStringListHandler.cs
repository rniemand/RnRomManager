using System.Data;
using System.Text.Json;
using Dapper;

namespace RnRomManager.Data.Repositories;

/// <summary>Reads/writes the JSON string-array columns (<c>developers</c>, <c>regions</c>, …).</summary>
internal sealed class JsonStringListHandler : SqlMapper.TypeHandler<IReadOnlyList<string>>
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            SqlMapper.AddTypeHandler(new JsonStringListHandler());
    }

    public override IReadOnlyList<string> Parse(object value) => JsonSerializer.Deserialize<List<string>>((string)value) ?? [];

    public override void SetValue(IDbDataParameter parameter, IReadOnlyList<string>? value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = JsonSerializer.Serialize(value ?? []);
    }
}
