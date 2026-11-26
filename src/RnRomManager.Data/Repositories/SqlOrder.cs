namespace RnRomManager.Data.Repositories;

/// <summary>SQL <c>ORDER BY</c> fragments built only from enums and bools, never from input text.</summary>
public static class SqlOrder
{
    public static string Dir(bool descending) => descending ? "DESC" : "";
}
