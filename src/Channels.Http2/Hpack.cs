﻿using Channels.Text.Primitives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Channels.Http2
{
    public struct Header
    {
        public HeaderOptions Options { get; }
        public string Name { get; }
        public string Value { get; }
        public int Length => (Name?.Length ?? 0) + (Value?.Length ?? 0) + 32;

        public bool IsResize => (Options & (HeaderOptions)0x0F) == HeaderOptions.IndexResize;

        public Header(string name, string value, HeaderOptions options = default(HeaderOptions))
        {
            Name = name;
            Value = value;
            Options = options;
        }

        public override string ToString() => $"{Name}: {Value}";

        internal void WriteTo(Span<byte> span)
        {
            long lengths = 0, hashcodes = 0;
            if(Name != null)
            {
                lengths = Name.Length & HeaderTable.Mask32;
                hashcodes = Name.GetHashCode() & HeaderTable.Mask32;
            }
            if(Value != null)
            {
                lengths |= ((long)Value.Length) << 32;
                hashcodes |= ((long)Value.GetHashCode()) << 32;
            }
            span.Write(lengths);
            span.Slice(8).Write(hashcodes);
            
            int offset = 32;
            if(!string.IsNullOrEmpty(Name))
            {
                var tmp = Encoding.ASCII.GetBytes(Name);
                span.Slice(offset, tmp.Length).Set(tmp);
                offset += tmp.Length;
            }
            if (!string.IsNullOrEmpty(Value))
            {
                var tmp = Encoding.ASCII.GetBytes(Value);
                span.Slice(offset, tmp.Length).Set(tmp);
            }
        }

        public static Header Resize(int newSize)
        {
            return new Header(null, newSize.ToString(), HeaderOptions.IndexResize);
        }
    }


    internal static class Hpack
    {
        
        public static Header ReadHeader(ref ReadableBuffer buffer, ref HeaderTable headerTable, IBufferPool memoryPool)
        {
            int firstByte = buffer.Peek();
            if (firstByte < 0) ThrowEndOfStreamException();
            buffer = buffer.Slice(1);
            if ((firstByte & 0x80) != 0)
            {
                // 6.1.  Indexed Header Field Representation
                return headerTable.GetHeader(ReadUInt32(ref buffer, firstByte, 7));
            }
            else if ((firstByte & 0x40) != 0)
            {
                // 6.2.1.  Literal Header Field with Incremental Indexing
                var result = ReadHeader(ref buffer, ref headerTable, firstByte, 6, HeaderOptions.IndexAddNewValue);
                headerTable = headerTable.Add(result, memoryPool);
                return result;
            }
            else if ((firstByte & 0x20) != 0)
            {
                // 6.3. Dynamic Table Size Update
                var newSize = ReadInt32(ref buffer, firstByte, 5);
                headerTable = headerTable.SetMaxLength(newSize, memoryPool);
                return Header.Resize(newSize);
            }
            else 
            {
                // 6.2.2.Literal Header Field without Indexing
                // 6.2.3.Literal Header Field Never Indexed
                return ReadHeader(ref buffer, ref headerTable, firstByte, 4,
                    (firstByte & 0x10) == 0
                    ? HeaderOptions.IndexNotIndexed
                    : HeaderOptions.IndexNeverIndexed);
            }
        }

        public static void WriteHttpHeader(WritableBuffer buffer, HttpHeader httpHeader, ref HeaderTable headerTable, MemoryPool memoryPool)
        {
            foreach(var header in httpHeader)
            {
                WriteHeader(buffer, header, ref headerTable, memoryPool);
            }
        }

        private static void WriteKeyName(WritableBuffer buffer, Header header, ref HeaderTable headerTable, byte preamble, int n)
        {
            var nameIndex = headerTable.GetKey(header.Name);
            WriteUInt32(buffer, nameIndex, preamble, n);
            if (nameIndex == 0)
            {
                WriteNameString(buffer, header.Name, header.Options);
            }
        }
        public static void WriteHeader(WritableBuffer buffer, Header header, ref HeaderTable headerTable, MemoryPool memoryPool)
        {
            switch(header.Options & HeaderOptions.IndexMask)
            {
                case HeaderOptions.IndexAddNewValue:
                    WriteKeyName(buffer, header, ref headerTable, 0x40, 6);
                    WriteValueString(buffer, header.Value, header.Options);
                    headerTable = headerTable.Add(header, memoryPool);
                    break;
                case HeaderOptions.IndexExistingValue:
                    var index = headerTable.GetKey(header.Name, header.Value);
                    if(index == 0)
                    {
                        throw new InvalidOperationException("Attempted to use a header that did not exist: " + header.ToString() + Environment.NewLine + headerTable.ToString());
                    }
                    WriteUInt32(buffer, index, 0x80, 7);
                    break;
                case HeaderOptions.IndexNeverIndexed:
                    WriteKeyName(buffer, header, ref headerTable, 0x10, 4);
                    WriteValueString(buffer, header.Value, header.Options);
                    break;
                case HeaderOptions.IndexNotIndexed:
                    WriteKeyName(buffer, header, ref headerTable, 0x00, 4);
                    WriteValueString(buffer, header.Value, header.Options);
                    break;
                case HeaderOptions.IndexResize:
                    uint len;
                    if(!uint.TryParse(header.Value, out len))
                    {
                        throw new InvalidOperationException("Invalid index length change: " + header.Value);
                    }
                    WriteUInt32(buffer, len, 0x20, 5);
                    break;
                case HeaderOptions.IndexAutomatic:
                    index = headerTable.GetKey(header.Name, header.Value);
                    if(index == 0)
                    {
                        index = headerTable.GetKey(header.Name);
                        switch(index)
                        {
                            case 1: // :authority
                            case 15: // accept-charset
                            case 16: // accept-encoding
                            case 17: // accept-language
                            case 38: // host
                            case 58: // user-agent
                                // AddNewValue
                                WriteUInt32(buffer, index, 0x40, 6);
                                headerTable = headerTable.Add(header, memoryPool);
                                break;
                            default:
                                // NotIndexed
                                WriteUInt32(buffer, index, 0x00, 4);
                                if(index == 0)
                                {
                                    WriteNameString(buffer, header.Name, header.Options);
                                }
                                break;
                        }
                        WriteValueString(buffer, header.Value, header.Options);
                    }
                    else
                    {
                        // ExistingValue
                        WriteUInt32(buffer, index, 0x80, 7);
                    }
                    break;
                default:
                    throw new InvalidOperationException("Invalid header indexing: " + header.Options);
            }
        }

        public static void WriteNameString(WritableBuffer buffer, string value, HeaderOptions options)
        {
            switch(options & HeaderOptions.NameCompressionMask)
            {
                case HeaderOptions.NameCompressionAutomatic:
                    var len = HuffmanCode.GetByteCount(value);
                    if (len < value.Length)
                    {
                        WriteUInt32(buffer, (uint)len, 0x80, 7);
                        HuffmanCode.Write(buffer, value);
                    }
                    else
                    {
                        WriteUInt32(buffer, (uint)value.Length, 0, 7);
                        buffer.WriteAsciiString(value);
                    }
                    break;
                case HeaderOptions.NameCompressionOn:
                    len = HuffmanCode.GetByteCount(value);
                    WriteUInt32(buffer, (uint)len, 0x80, 7);
                    HuffmanCode.Write(buffer, value);
                    break;
                case HeaderOptions.NameCompressionOff:
                    WriteUInt32(buffer, (uint)value.Length, 0, 7);
                    buffer.WriteAsciiString(value);
                    break;
                default:
                    throw new InvalidOperationException("Invalid name compression: " + options);
            }
        }

        public static void WriteValueString(WritableBuffer buffer, string value, HeaderOptions options)
        {
            switch (options & HeaderOptions.ValueCompressionMask)
            {
                case HeaderOptions.ValueCompressionAutomatic:
                    var len = HuffmanCode.GetByteCount(value);
                    if (len < value.Length)
                    {
                        WriteUInt32(buffer, (uint)len, 0x80, 7);
                        HuffmanCode.Write(buffer, value);
                    }
                    else
                    {
                        WriteUInt32(buffer, (uint)value.Length, 0, 7);
                        buffer.WriteAsciiString(value);
                    }
                    break;
                case HeaderOptions.ValueCompressionOn:
                    len = HuffmanCode.GetByteCount(value);
                    WriteUInt32(buffer, (uint)len, 0x80, 7);
                    HuffmanCode.Write(buffer, value);
                    break;
                case HeaderOptions.ValueCompressionOff:
                    WriteUInt32(buffer, (uint)value.Length, 0, 7);
                    buffer.WriteAsciiString(value);
                    break;
                default:
                    throw new InvalidOperationException("Invalid value compression: " + options);
            }
        }

        private static Header ReadHeader(ref ReadableBuffer buffer, ref HeaderTable headerTable, int header, int prefixBytes, HeaderOptions options)
        {
            var index = ReadUInt32(ref buffer, header, prefixBytes);
            string name, value;
            bool compressed;
            if (index == 0)
            {
                name = ReadString(ref buffer, out compressed);
                options |= compressed ? HeaderOptions.NameCompressionOn : HeaderOptions.NameCompressionOff;
            }
            else
            {
                name = headerTable.GetHeaderName(index);
            }
            value = ReadString(ref buffer, out compressed);
            options |= compressed ? HeaderOptions.ValueCompressionOn : HeaderOptions.ValueCompressionOff;
            return new Header(name, value, options);
        }

        public static string ReadString(ref ReadableBuffer buffer, out bool compressed)
        {
            int header = buffer.Peek();
            if (header < 0) ThrowEndOfStreamException();

            compressed = (header & 0x80) != 0;
            buffer = buffer.Slice(1);
            int len = checked((int)ReadUInt64(ref buffer, header, 7));
            string result;
            if (compressed)
            {
                result = HuffmanCode.ReadString(buffer.Slice(0, len));
            }
            else
            {
                result = buffer.Slice(0, len).GetAsciiString();
            }

            buffer = buffer.Slice(len);
            return result;
        }

        public static ulong ReadUInt64(ref ReadableBuffer buffer, int n)
        {
            int firstByte = buffer.Peek();
            if (firstByte < 0) throw new EndOfStreamException();
            buffer = buffer.Slice(1);
            return ReadUInt64(ref buffer, firstByte, n);

        }
        private static void ThrowEndOfStreamException()
        {
            throw new EndOfStreamException();
        }
        public static uint ReadUInt32(ref ReadableBuffer buffer, int firstByte, int n)
            => checked((uint)ReadUInt64(ref buffer, firstByte, n));
        public static int ReadInt32(ref ReadableBuffer buffer, int firstByte, int n)
            => checked((int)ReadUInt64(ref buffer, firstByte, n));

        public static ulong ReadUInt64(ref ReadableBuffer buffer, int firstByte, int n)
        {
            if (n < 0 || n > 8) throw new ArgumentOutOfRangeException(nameof(n));
            int mask = ~(~0 << n);
            int prefix = firstByte & mask;

            if (prefix != mask)
            {
                return (ulong)prefix; // short value encoded directly
            }
            ulong value = 0;
            int shift = 0, nextByte;
            for (int i = 0; i < 9; i++)
            {
                nextByte = buffer.Peek();
                if (nextByte < 0) ThrowEndOfStreamException();
                buffer = buffer.Slice(1);
                value |= ((ulong)nextByte & 0x7F) << shift;

                if ((nextByte & 0x80) == 0)
                {
                    // lack of continuation bit
                    return value + (ulong)mask;
                }
                shift += 7;
            }
            switch (nextByte = buffer.Peek())
            {
                case 0:
                case 1:
                    // note: lack of continuation bit (or anything else)
                    buffer = buffer.Slice(1);
                    value |= ((ulong)nextByte & 0x7F) << shift;
                    return value + (ulong)mask;
                default:
                    if (nextByte < 0) ThrowEndOfStreamException();
                    // 7*9=63, so max 9 groups of 7 bits plus either 0 or 1;
                    // after that: we've overflown
                    throw new OverflowException();
            }
        }

        internal static HttpHeader ParseHttpHeader(ref ReadableBuffer buffer, ref HeaderTable headerTable, IBufferPool memoryPool)
        {
            var headers = new List<Header>();
            while (!buffer.IsEmpty)
            {
                var header = Hpack.ReadHeader(ref buffer, ref headerTable, memoryPool);
                if (!header.IsResize)
                {
                    headers.Add(header);
                }
            }
            return new HttpHeader(headers);
        }

        public static void WriteUInt32(WritableBuffer buffer, uint value, byte preamble, int n)
            => WriteUInt64(buffer, value, preamble, n);

        public static unsafe void WriteUInt64(WritableBuffer buffer, ulong value, byte preamble, int n)
        {
            if (n < 0 || n > 8) throw new ArgumentOutOfRangeException(nameof(n));
            var mask = ~0UL << n;
            byte* scratch = stackalloc byte[16], writeHead = scratch;
            if ((value & mask) == 0)
            {
                // value fits inside the single byte, yay!
                preamble |= (byte)value;
                *writeHead++ = preamble;
            }
            else
            {
                
                preamble |= (byte)(~mask);
                *writeHead++ = preamble;
                value -= ~mask;
                const ulong MoreThanSevenBits = ~(127UL);
                while((value & MoreThanSevenBits) != 0)
                {
                    *writeHead++ = (byte)(0x80 | (value & 0x7F));
                    value >>= 7;
                }
                *writeHead++ = (byte)value;
            }
            buffer.Write(new Span<byte>(scratch, (int)(writeHead - scratch)));
        }
    }
}
