using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CircuitLord.VRModInstaller.Core.Steam;

/// <summary>Valve binary KeyValues node. Order is preserved for round-tripping.</summary>
public abstract class VdfNode
{
}

public sealed class VdfString : VdfNode
{
    public VdfString(string value) => Value = value;
    public string Value { get; }
}

public sealed class VdfInt : VdfNode
{
    public VdfInt(int value) => Value = value;
    public int Value { get; }
}

public sealed class VdfUInt64 : VdfNode
{
    public VdfUInt64(ulong value) => Value = value;
    public ulong Value { get; }
}

/// <summary>Ordered key/value map; unknown keys survive a parse/serialize round trip.</summary>
public sealed class VdfMap : VdfNode
{
    private readonly List<KeyValuePair<string, VdfNode>> _entries = new();

    public IReadOnlyList<KeyValuePair<string, VdfNode>> Entries => _entries;
    public int Count => _entries.Count;

    public VdfNode? this[string key]
    {
        get => _entries.FirstOrDefault(e => e.Key == key).Value;
        set
        {
            var index = _entries.FindIndex(e => e.Key == key);
            if (value is null)
            {
                if (index >= 0)
                {
                    _entries.RemoveAt(index);
                }
                return;
            }
            if (index >= 0)
            {
                _entries[index] = new KeyValuePair<string, VdfNode>(key, value);
            }
            else
            {
                _entries.Add(new KeyValuePair<string, VdfNode>(key, value));
            }
        }
    }

    public string? GetString(string key) => this[key] is VdfString s ? s.Value : null;
    public int? GetInt(string key) => this[key] is VdfInt i ? i.Value : null;
    public VdfMap? GetMap(string key) => this[key] as VdfMap;

    public void Set(string key, string value) => this[key] = new VdfString(value);
    public void Set(string key, int value) => this[key] = new VdfInt(value);
    public void Set(string key, VdfMap value) => this[key] = value;

    public VdfMap Clone()
    {
        var clone = new VdfMap();
        foreach (var entry in _entries)
        {
            clone[entry.Key] = entry.Value switch
            {
                VdfMap map => map.Clone(),
                VdfString s => new VdfString(s.Value),
                VdfInt i => new VdfInt(i.Value),
                VdfUInt64 u => new VdfUInt64(u.Value),
                _ => entry.Value,
            };
        }
        return clone;
    }
}

public sealed class VdfFormatException : Exception
{
    public VdfFormatException(string message) : base(message)
    {
    }
}

/// <summary>
/// Reader/writer for Valve's binary KeyValues format used by Steam's
/// <c>shortcuts.vdf</c>.
///
/// Type markers: 0x00 nested map, 0x01 string, 0x02 int32, 0x07 uint64,
/// 0x08 end of map. Anything else is rejected rather than guessed at, so a
/// malformed file is never replaced with a partial structure.
/// </summary>
public static class BinaryVdf
{
    private const byte TypeMap = 0x00;
    private const byte TypeString = 0x01;
    private const byte TypeInt32 = 0x02;
    private const byte TypeUInt64 = 0x07;
    private const byte TypeEnd = 0x08;

    public static VdfMap Parse(byte[] data)
    {
        if (data is null || data.Length == 0)
        {
            throw new VdfFormatException("The file is empty.");
        }

        var offset = 0;
        if (data[offset] != TypeMap)
        {
            throw new VdfFormatException($"Expected a root map at offset 0, found 0x{data[offset]:X2}.");
        }
        offset++;

        var rootKey = ReadCString(data, ref offset);
        var root = ParseMapBody(data, ref offset);
        if (offset < data.Length && data[offset] == TypeEnd)
        {
            offset++;
        }

        var wrapper = new VdfMap();
        wrapper[rootKey] = root;
        return wrapper;
    }

    public static byte[] Serialize(VdfMap root)
    {
        using var stream = new MemoryStream();
        if (root.Count != 1)
        {
            throw new VdfFormatException("A shortcuts document must contain exactly one root map.");
        }

        var entry = root.Entries[0];
        if (entry.Value is not VdfMap map)
        {
            throw new VdfFormatException("The root key must contain a map.");
        }

        stream.WriteByte(TypeMap);
        WriteCString(stream, entry.Key);
        WriteMapBody(stream, map);
        stream.WriteByte(TypeEnd);
        return stream.ToArray();
    }

    private static VdfMap ParseMapBody(byte[] data, ref int offset)
    {
        var map = new VdfMap();
        while (true)
        {
            if (offset >= data.Length)
            {
                throw new VdfFormatException("Unexpected end of file inside a map.");
            }

            var type = data[offset++];
            if (type == TypeEnd)
            {
                return map;
            }

            var key = ReadCString(data, ref offset);
            switch (type)
            {
                case TypeMap:
                    map[key] = ParseMapBody(data, ref offset);
                    break;
                case TypeString:
                    map[key] = new VdfString(ReadCString(data, ref offset));
                    break;
                case TypeInt32:
                    map[key] = new VdfInt(ReadInt32(data, ref offset));
                    break;
                case TypeUInt64:
                    map[key] = new VdfUInt64(ReadUInt64(data, ref offset));
                    break;
                default:
                    throw new VdfFormatException($"Unsupported binary VDF type 0x{type:X2} for key '{key}'.");
            }
        }
    }

    private static void WriteMapBody(Stream stream, VdfMap map)
    {
        foreach (var entry in map.Entries)
        {
            switch (entry.Value)
            {
                case VdfMap nested:
                    stream.WriteByte(TypeMap);
                    WriteCString(stream, entry.Key);
                    WriteMapBody(stream, nested);
                    stream.WriteByte(TypeEnd);
                    break;
                case VdfString s:
                    stream.WriteByte(TypeString);
                    WriteCString(stream, entry.Key);
                    WriteCString(stream, s.Value);
                    break;
                case VdfInt i:
                    stream.WriteByte(TypeInt32);
                    WriteCString(stream, entry.Key);
                    WriteBytes(stream, BitConverter.GetBytes(i.Value));
                    break;
                case VdfUInt64 u:
                    stream.WriteByte(TypeUInt64);
                    WriteCString(stream, entry.Key);
                    WriteBytes(stream, BitConverter.GetBytes(u.Value));
                    break;
                default:
                    throw new VdfFormatException($"Unsupported node for key '{entry.Key}'.");
            }
        }
    }

    private static string ReadCString(byte[] data, ref int offset)
    {
        var start = offset;
        while (offset < data.Length && data[offset] != 0)
        {
            offset++;
        }
        if (offset >= data.Length)
        {
            throw new VdfFormatException("Unterminated string in binary VDF data.");
        }
        var value = Encoding.UTF8.GetString(data, start, offset - start);
        offset++;
        return value;
    }

    private static int ReadInt32(byte[] data, ref int offset)
    {
        Require(data, offset, 4);
        var value = BitConverter.ToInt32(data, offset);
        offset += 4;
        return value;
    }

    private static ulong ReadUInt64(byte[] data, ref int offset)
    {
        Require(data, offset, 8);
        var value = BitConverter.ToUInt64(data, offset);
        offset += 8;
        return value;
    }

    private static void Require(byte[] data, int offset, int count)
    {
        if (offset + count > data.Length)
        {
            throw new VdfFormatException("Unexpected end of file reading a numeric value.");
        }
    }

    private static void WriteBytes(Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);

    private static void WriteCString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0);
    }
}

/// <summary>CRC32 used to derive a stable Steam shortcut AppID.</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(string value)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        }
        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var entry = i;
            for (var j = 0; j < 8; j++)
            {
                entry = (entry & 1) != 0 ? 0xEDB88320 ^ (entry >> 1) : entry >> 1;
            }
            table[i] = entry;
        }
        return table;
    }
}
