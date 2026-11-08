using System;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace AnimePlayer;

// One line of mpv's JSON IPC: {"command":[...]} or {"command":[...],"request_id":N}.
// Written directly, into a buffer and writer reused for every command: the glass sends one per screen refresh while the
// bars move, and JsonSerializer on an anonymous type looked each one up by reflection and boxed it into a fresh object.
// The output is what JsonSerializer wrote (same writer, same escaping).
static class MpvJson
{
    [ThreadStatic] static ArrayBufferWriter<byte>? buffer;
    [ThreadStatic] static Utf8JsonWriter? writer;

    public static string Command(object?[] command, int? requestId = null)
    {
        var buf = buffer ??= new ArrayBufferWriter<byte>(256);
        buf.ResetWrittenCount();
        var w = writer ??= new Utf8JsonWriter(buf);
        w.Reset(buf);
        w.WriteStartObject();
        w.WriteStartArray("command");
        foreach (var arg in command) Write(w, arg);
        w.WriteEndArray();
        if (requestId is int id) w.WriteNumber("request_id", id);
        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    static void Write(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case double d: w.WriteNumberValue(d); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case float f: w.WriteNumberValue(f); break;
            case bool b: w.WriteBooleanValue(b); break;
            default: JsonSerializer.Serialize(w, v, v.GetType()); break; // anything else, as before
        }
    }
}
