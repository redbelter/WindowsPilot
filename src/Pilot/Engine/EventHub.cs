using System.Threading.Channels;

namespace WindowsPilot.Engine;

/// <summary>
/// Bounded ring (500) + live fan-out. Same {n,t,type,detail} row shape the
/// hyperv JobStore exposes as /api/log (design §17). Consumers: SSE /events,
/// /api/log poll fallback, pilot_get_events, Operator tab activity strip.
/// </summary>
public sealed class EventHub(int capacity = 500)
{
    readonly object _gate = new();
    readonly Queue<(long N, string T, string Type, object Detail)> _q = new();
    long _seq;
    readonly List<Channel<(long, string, string, object)>> _subs = [];

    public long Push(string type, object detail)
    {
        long n;
        lock (_gate)
        {
            _seq++;
            n = _seq;
            _q.Enqueue((n, DateTime.Now.ToString("HH:mm:ss"), type, detail));
            while (_q.Count > capacity) _q.Dequeue();
            foreach (var s in _subs) s.Writer.TryWrite((n, _q.Peek().T, type, detail));
        }
        return n;
    }

    public long SinceSeq { get { lock (_gate) return _seq; } }

    public List<(long N, string T, string Type, object Detail)> Since(long since, int max)
    {
        lock (_gate) return _q.Where(x => x.N > since).Take(max).ToList();
    }

    public Channel<(long, string, string, object)> Subscribe()
    {
        var ch = Channel.CreateBounded<(long, string, string, object)>(new BoundedChannelOptions(200)
        { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) _subs.Add(ch);
        return ch;
    }

    public void Unsubscribe(Channel<(long, string, string, object)> ch)
    {
        lock (_gate) _subs.Remove(ch);
        ch.Writer.TryComplete();
    }
}

/// <summary>
/// Long-op jobs — shapes byte-compatible with HermesHyperV JobStore (proven
/// dashboard contract): /api/jobs -> {jobs:[{id,name,status,started,result,error}]}.
/// </summary>
public sealed class PilotJobStore(EventHub events, ILogger<PilotJobStore> log)
{
    readonly Dictionary<string, System.Text.Json.Nodes.JsonObject> _jobs = new();
    readonly object _gate = new();

    public string Spawn(string name, Func<CancellationToken, Task<object?>> work)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        lock (_gate)
            _jobs[id] = new()
            {
                ["id"] = id, ["name"] = name, ["status"] = "running",
                ["started"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
        events.Push("job_started", new { id, name });

        _ = Task.Run(async () =>
        {
            try
            {
                var res = await work(CancellationToken.None);
                Finish(id, "done", res, null);
            }
            catch (PilotException pe)
            {
                Finish(id, "error", null, $"{pe.Code}: {pe.Message}");
            }
            catch (Exception e)
            {
                Finish(id, "error", null, e.Message[..Math.Min(300, e.Message.Length)]);
            }
        });
        return id;
    }

    void Finish(string id, string status, object? result, string? error)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var j)) return;
            j["status"] = status;
            j["result"] = result is null ? null : System.Text.Json.Nodes.JsonNode.Parse(
                System.Text.Json.JsonSerializer.Serialize(result));
            if (error is not null) j["error"] = error;
        }
        events.Push(status == "done" ? "job_done" : "job_error", new { id, name = status, error });
        log.LogInformation("job {Id} {Status}", id, status);
    }

    public List<System.Text.Json.Nodes.JsonObject> Jobs()
    {
        lock (_gate) return _jobs.Values.Select(j => (System.Text.Json.Nodes.JsonObject)j.DeepClone()).ToList();
    }

    public System.Text.Json.Nodes.JsonObject? Job(string id)
    {
        lock (_gate) return _jobs.TryGetValue(id, out var j) ? (System.Text.Json.Nodes.JsonObject)j.DeepClone() : null;
    }
}
