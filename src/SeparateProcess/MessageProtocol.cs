using System.IO;
using MessagePack;
using Microsoft.Extensions.Logging;

namespace SeparateProcess;

public static class MessageProtocol
{
    public static void WriteCall(BinaryWriter writer, int id, string method, string returnType, object?[] args)
    {
        writer.Write((byte)MessageType.Call);
        writer.Write(id);
        writer.Write(method);
        writer.Write(returnType);
        writer.Write(args.Length);
        foreach (var arg in args)
        {
            var argType = arg?.GetType() ?? typeof(object);
            writer.Write(argType.FullName!);
            var argBytes = MessagePackSerializer.Serialize(arg);
            writer.Write(argBytes.Length);
            writer.Write(argBytes);
        }
    }

    public static (int id, string method, string returnType, object?[] args) ReadCall(BinaryReader reader)
    {
        var id = reader.ReadInt32();
        var method = reader.ReadString();
        var returnType = reader.ReadString();
        var numArgs = reader.ReadInt32();
        var args = new object?[numArgs];
        for (int i = 0; i < numArgs; i++)
        {
            var argTypeName = reader.ReadString();
            var length = reader.ReadInt32();
            var bytes = reader.ReadBytes(length);
            var argType = Type.GetType(argTypeName);
            if (argType != null)
            {
                args[i] = MessagePackSerializer.Deserialize(argType, bytes);
            }
            else
            {
                args[i] = MessagePackSerializer.Deserialize<object>(bytes);
            }
        }
        return (id, method, returnType, args);
    }

    public static void WriteResponse(BinaryWriter writer, int id, string status, object? result)
    {
        var resultBytes = result != null ? MessagePackSerializer.Serialize(result) : Array.Empty<byte>();
        writer.Write((byte)MessageType.Response);
        writer.Write(id);
        writer.Write(status);
        writer.Write(resultBytes.Length);
        if (resultBytes.Length > 0)
        {
            writer.Write(resultBytes);
        }
    }

    public static object? DeserializeResult(string returnType, string status, byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        if (status == "error")
        {
            return MessagePackSerializer.Deserialize<string>(bytes);
        }
        else
        {
            var type = Type.GetType(returnType);
            if (type != null && type != typeof(void))
            {
                return MessagePackSerializer.Deserialize(type, bytes);
            }
            else
            {
                return MessagePackSerializer.Deserialize<object>(bytes);
            }
        }
    }

    public static void WriteEvent(BinaryWriter writer, string eventName, object? data)
    {
        var dataBytes = MessagePackSerializer.Serialize(data);
        writer.Write((byte)MessageType.Event);
        writer.Write(eventName);
        writer.Write(dataBytes.Length);
        writer.Write(dataBytes);
    }

    public static (string eventName, byte[] bytes) ReadEvent(BinaryReader reader)
    {
        var eventName = reader.ReadString();
        var length = reader.ReadInt32();
        var bytes = reader.ReadBytes(length);
        return (eventName, bytes);
    }

    public static void WriteLog(BinaryWriter writer, LogLevel level, string message)
    {
        writer.Write((byte)MessageType.Log);
        writer.Write(level.ToString());
        writer.Write(message);
    }

    public static (LogLevel level, string message) ReadLog(BinaryReader reader)
    {
        var levelStr = reader.ReadString();
        var message = reader.ReadString();
        Enum.TryParse<LogLevel>(levelStr, out var level);
        return (level, message);
    }
}