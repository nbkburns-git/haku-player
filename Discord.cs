using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AnimePlayer;

// "Watching" status on your Discord profile, over Discord's local Rich Presence pipe (\\.\pipe\discord-ipc-N).
// Needs your own Discord application id (its name is what Discord shows: "Watching Haku Player").
// Frames: [int32 opcode][int32 length][JSON]; opcode 0 = handshake, 1 = command.
static class Discord
{
    static NamedPipeClientStream? pipe;
    static JsonNode? pending;       // newest activity not sent yet (null = clear)
    static bool dirty;
    static DateTime lastSent = DateTime.MinValue;
    static readonly SemaphoreSlim gate = new(1, 1);

    // details: show name; state: episode; image: an https poster url
    public static void Watching(string details, string state, bool paused, double position, double duration, string? image)
    {
        var activity = new JsonObject { ["type"] = 3, ["details"] = Clip(details), ["state"] = Clip(paused ? $"Paused · {state}" : state) };
        if (!paused && duration > 0)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            activity["timestamps"] = new JsonObject { ["start"] = now - (long)(position * 1000), ["end"] = now + (long)((duration - position) * 1000) };
        }
        if (image is { Length: > 0 } && image.StartsWith("http")) activity["assets"] = new JsonObject { ["large_image"] = image, ["large_text"] = Clip(details) };
        Queue(activity);
    }

    public static void Clear() => Queue(null);

    static string Clip(string s) => s.Length <= 2 ? s.PadRight(2) : s.Length > 128 ? s[..127] + "…" : s; // Discord wants 2..128 chars

    static void Queue(JsonNode? activity)
    {
        if (!Settings.Current.DiscordStatus || Settings.Current.DiscordAppId.Trim().Length == 0)
        {
            if (pipe != null) { pending = null; dirty = true; _ = Flush(); }
            return;
        }
        pending = activity; dirty = true;
        _ = Flush();
    }

    // Discord allows about 5 updates per 20 s: send the newest one at most every 4 s.
    static async Task Flush()
    {
        if (!await gate.WaitAsync(0)) return; // a flush is already running; it picks up the newest
        try
        {
            while (dirty)
            {
                var wait = lastSent.AddSeconds(4) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                dirty = false;
                var activity = pending?.DeepClone();
                await Task.Run(() => Send(activity));
                lastSent = DateTime.UtcNow;
            }
        }
        catch { }
        finally { gate.Release(); }
    }

    static void Send(JsonNode? activity)
    {
        if (pipe is not { IsConnected: true } && !Connect()) return;
        var cmd = new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = activity },
            ["nonce"] = Guid.NewGuid().ToString(),
        };
        try { Write(1, cmd.ToJsonString()); Read(); }
        catch { try { pipe?.Dispose(); } catch { } pipe = null; } // Discord closed: reconnect next time
    }

    static bool Connect()
    {
        var id = Settings.Current.DiscordAppId.Trim();
        if (id.Length == 0) return false;
        for (int i = 0; i < 10; i++)
        {
            var p = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut);
            try
            {
                p.Connect(300);
                pipe = p;
                Write(0, new JsonObject { ["v"] = 1, ["client_id"] = id }.ToJsonString());
                Read(); // READY (or an error for a bad id)
                return true;
            }
            catch { p.Dispose(); pipe = null; }
        }
        return false; // Discord isn't running
    }

    static void Write(int op, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var buf = new byte[8 + body.Length];
        BitConverter.GetBytes(op).CopyTo(buf, 0);
        BitConverter.GetBytes(body.Length).CopyTo(buf, 4);
        body.CopyTo(buf, 8);
        pipe!.Write(buf);
        pipe.Flush();
    }

    static string Read()
    {
        var head = new byte[8];
        ReadExactly(head);
        int len = BitConverter.ToInt32(head, 4);
        var body = new byte[Math.Clamp(len, 0, 1 << 20)];
        ReadExactly(body);
        return Encoding.UTF8.GetString(body);
    }

    static void ReadExactly(byte[] b)
    {
        int got = 0;
        while (got < b.Length) { int n = pipe!.Read(b, got, b.Length - got); if (n <= 0) throw new IOException("closed"); got += n; }
    }
}
