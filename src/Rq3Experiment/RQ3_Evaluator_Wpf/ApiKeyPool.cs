namespace RQ3Evaluator.Wpf;

public sealed record ApiKeyLease(string Id, string Key);
public sealed record ApiKeyUsageSnapshot(string Id,int Requests,int Successes,int RateLimited,int Disabled,int TransientFailures,int InputTokens,int OutputTokens,double CostUsd,string State,DateTimeOffset? NextAvailableAt);

public sealed class ApiKeyPool
{
    private sealed class Entry(string id,string key)
    {
        public string Id { get; }=id;
        public string Key { get; }=key;
        public DateTimeOffset NextAvailableAt { get; set; }=DateTimeOffset.MinValue;
        public bool Disabled { get; set; }
        public bool DailyExhausted { get; set; }
        public int Consecutive429 { get; set; }
        public int Requests { get; set; }
        public int Successes { get; set; }
        public int RateLimited { get; set; }
        public int DisabledCount { get; set; }
        public int TransientFailures { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public double CostUsd { get; set; }
    }

    private readonly object _gate=new();
    private readonly Entry[] _entries;
    private readonly TimeSpan _minimumInterval;
    private int _cursor;

    public ApiKeyPool(IEnumerable<string> keys,int perKeyRpm=0)
    {
        var unique=keys.Select(x=>x?.Trim()??"").Where(x=>x.Length>0).Distinct(StringComparer.Ordinal).ToArray();
        if(unique.Length==0)throw new ArgumentException("At least one non-empty API key is required.",nameof(keys));
        _entries=unique.Select((key,index)=>new Entry($"K{index+1:00}",key)).ToArray();
        _minimumInterval=perKeyRpm>0?TimeSpan.FromMilliseconds(60000.0/perKeyRpm):TimeSpan.Zero;
    }

    public static string[] LoadFile(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!File.Exists(path))return Array.Empty<string>();
        return File.ReadLines(path).Select(x=>x.Trim()).Where(x=>x.Length>0).Distinct(StringComparer.Ordinal).ToArray();
    }

    public int Count=>_entries.Length;

    public async Task<ApiKeyLease> AcquireAsync(TimeSpan maxWait,CancellationToken ct)
    {
        var started=DateTimeOffset.UtcNow;
        while(true)
        {
            TimeSpan delay;
            lock(_gate)
            {
                var now=DateTimeOffset.UtcNow;
                for(var offset=0;offset<_entries.Length;offset++)
                {
                    var index=(_cursor+offset)%_entries.Length;
                    var entry=_entries[index];
                    if(entry.Disabled||entry.DailyExhausted||entry.NextAvailableAt>now)continue;
                    _cursor=(index+1)%_entries.Length;
                    entry.Requests++;
                    if(_minimumInterval>TimeSpan.Zero)entry.NextAvailableAt=now+_minimumInterval;
                    return new ApiKeyLease(entry.Id,entry.Key);
                }

                var recoverable=_entries.Where(x=>!x.Disabled&&!x.DailyExhausted).ToArray();
                if(recoverable.Length==0)throw new InvalidOperationException("API key pool exhausted: every key is disabled or daily-exhausted.");
                var earliest=recoverable.Min(x=>x.NextAvailableAt);
                delay=earliest>now?earliest-now:TimeSpan.FromMilliseconds(25);
            }
            var elapsed=DateTimeOffset.UtcNow-started;
            if(elapsed+delay>maxWait)throw new TimeoutException("No API key became available within the configured request timeout.");
            await Task.Delay(delay,ct);
        }
    }

    public void ReportSuccess(string id,int inputTokens,int outputTokens,double costUsd)
    {
        lock(_gate){var e=Find(id);e.Successes++;e.Consecutive429=0;e.InputTokens+=inputTokens;e.OutputTokens+=outputTokens;e.CostUsd+=costUsd;}
    }

    public void ReportRateLimit(string id,TimeSpan? retryAfter,string responseBody)
    {
        lock(_gate)
        {
            var e=Find(id);e.RateLimited++;e.Consecutive429++;
            if(IsDailyQuota(responseBody)){e.DailyExhausted=true;e.NextAvailableAt=NextPacificMidnight();return;}
            var fallback=TimeSpan.FromSeconds(Math.Min(900,60*Math.Pow(2,Math.Min(4,e.Consecutive429-1))));
            e.NextAvailableAt=DateTimeOffset.UtcNow+(retryAfter is { } value&&value>TimeSpan.Zero?value:fallback);
        }
    }

    public void ReportTransientFailure(string id,TimeSpan? cooldown=null)
    {
        lock(_gate){var e=Find(id);e.TransientFailures++;e.NextAvailableAt=DateTimeOffset.UtcNow+(cooldown??TimeSpan.FromSeconds(5));}
    }

    public void Disable(string id)
    {
        lock(_gate){var e=Find(id);e.Disabled=true;e.DisabledCount++;}
    }

    public IReadOnlyList<ApiKeyUsageSnapshot> Snapshot()
    {
        lock(_gate)return _entries.Select(e=>new ApiKeyUsageSnapshot(e.Id,e.Requests,e.Successes,e.RateLimited,e.DisabledCount,e.TransientFailures,e.InputTokens,e.OutputTokens,e.CostUsd,e.Disabled?"DISABLED":e.DailyExhausted?"DAILY_EXHAUSTED":e.NextAvailableAt>DateTimeOffset.UtcNow?"COOLDOWN":"AVAILABLE",e.NextAvailableAt>DateTimeOffset.MinValue?e.NextAvailableAt:null)).ToArray();
    }

    private Entry Find(string id)=>_entries.Single(x=>x.Id==id);
    private static bool IsDailyQuota(string body)=>body.Contains("PerDay",StringComparison.OrdinalIgnoreCase)||body.Contains("per_day",StringComparison.OrdinalIgnoreCase)||body.Contains("requests per day",StringComparison.OrdinalIgnoreCase)||body.Contains("RPD",StringComparison.OrdinalIgnoreCase);
    private static DateTimeOffset NextPacificMidnight()
    {
        var zone=TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows()?"Pacific Standard Time":"America/Los_Angeles");
        var local=TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,zone);
        var nextLocal=local.Date.AddDays(1);
        return new DateTimeOffset(nextLocal,zone.GetUtcOffset(nextLocal)).ToUniversalTime();
    }
}
