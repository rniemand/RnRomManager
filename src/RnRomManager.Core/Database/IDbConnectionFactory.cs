using System.Data.Common;

namespace RnRomManager.Core.Database;

/// <summary>The single source of DB connections (plan §1.4). Callers open and dispose them.</summary>
public interface IDbConnectionFactory
{
    DbConnection Create();
}
