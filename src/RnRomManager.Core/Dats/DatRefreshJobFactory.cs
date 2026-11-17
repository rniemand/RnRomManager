using RnRomManager.Core.Jobs;

namespace RnRomManager.Core.Dats;

/// <summary>A fresh <see cref="DatRefreshJob"/> for one run (used by <c>POST /api/jobs/dat-refresh</c> and the daily schedule, B29).</summary>
public delegate IJob DatRefreshJobFactory();
