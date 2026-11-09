using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Tasks;

namespace AnimePlayer;

// mpv's JSON pipe (input-ipc-server): commands out, replies and property changes in. No UI in here. Create it on the UI
// thread: the awaits come back to it, so Property / Event fire there and nothing needs locking (as MainWindow.Listen does
// for the WPF app; the WinUI app uses this).
sealed class MpvIpc
{
    public event Action<string, JsonElement>? Property; // an observed property changed (the element is only valid during the call)
    public event Action<string>? Event;                 // "file-loaded", "playback-restart", "end-file", ...
    public event Action? Closed;                        // the pipe closed: mpv quit
    public bool Connected => writer != null;

    StreamWriter? writer;
    int reqId = 1000;
    readonly Dictionary<int, TaskCompletionSource<string?>> pending = new();

    // Connects (mpv makes the pipe a moment after it starts), observes these properties, then reads until mpv quits.
    public async Task Run(string pipe, IEnumerable<string> observe)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await c.ConnectAsync(5000);
            var w = new StreamWriter(c) { AutoFlush = true };
            var r = new StreamReader(c);
            writer = w;
            foreach (var prop in observe) await w.WriteLineAsync(MpvJson.Command(new object?[] { "observe_property", 2, prop }));
            string? line;
            while ((line = await r.ReadLineAsync()) != null)
            {
                using var j = JsonDocument.Parse(line);
                var m = j.RootElement;
                if (m.TryGetProperty("request_id", out var rid) && rid.ValueKind == JsonValueKind.Number && pending.Remove(rid.GetInt32(), out var waiting))
                {
                    waiting.TrySetResult(m.TryGetProperty("data", out var rd) && rd.ValueKind == JsonValueKind.String ? rd.GetString() : null);
                    continue;
                }
                if (!m.TryGetProperty("event", out var ev)) continue;
                if (ev.ValueEquals("property-change"))
                {
                    m.TryGetProperty("data", out var d);
                    if (m.TryGetProperty("name", out var n) && n.GetString() is { } name) Property?.Invoke(name, d);
                }
                else if (ev.GetString() is { } e) Event?.Invoke(e);
            }
        }
        catch { } // the pipe closes when mpv exits
        finally
        {
            writer = null;
            foreach (var t in pending.Values) t.TrySetResult(null);
            pending.Clear();
            Closed?.Invoke();
        }
    }

    public void Send(object?[] command)
    {
        try { writer?.WriteLine(MpvJson.Command(command)); } catch { }
    }

    // A command whose reply we wait for: mpv answers once it has carried it out (data string, or null; null after 1.5 s).
    public Task<string?> Ask(object?[] command)
    {
        if (writer == null) return Task.FromResult<string?>(null);
        int id = ++reqId;
        var tcs = new TaskCompletionSource<string?>();
        pending[id] = tcs;
        try { writer.WriteLine(MpvJson.Command(command, id)); }
        catch { pending.Remove(id); return Task.FromResult<string?>(null); }
        GiveUpLater(id, tcs);
        return tcs.Task;
    }

    async void GiveUpLater(int id, TaskCompletionSource<string?> tcs)
    {
        await Task.Delay(1500);
        if (pending.Remove(id)) tcs.TrySetResult(null);
    }
}
