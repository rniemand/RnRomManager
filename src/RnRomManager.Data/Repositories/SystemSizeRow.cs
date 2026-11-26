namespace RnRomManager.Data.Repositories;

// SUM() is DECIMAL and COUNT() BIGINT on MariaDB: map loosely, then narrow.
internal sealed class SystemSizeRow
{
    public string SystemId { get; init; } = "";
    public decimal Bytes { get; init; }
    public long GameCount { get; init; }
    public decimal UpdateCount { get; init; }
}
