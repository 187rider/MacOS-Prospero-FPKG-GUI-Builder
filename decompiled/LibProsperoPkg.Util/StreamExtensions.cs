using System;
using System.IO;
using System.Text;

namespace LibProsperoPkg.Util;

internal static class StreamExtensions
{
	/// <summary>
	/// Read a signed 8-bit integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static sbyte ReadInt8(this Stream s)
	{
		return (sbyte)s.ReadUInt8();
	}

	/// <summary>
	/// Read an unsigned 8-bit integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static byte ReadUInt8(this Stream s)
	{
		byte[] array = new byte[1];
		s.ReadExactly(array, 0, 1);
		return array[0];
	}

	/// <summary>
	/// Read an unsigned 16-bit little-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static ushort ReadUInt16LE(this Stream s)
	{
		return (ushort)s.ReadInt16LE();
	}

	/// <summary>
	/// Read a signed 16-bit little-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static short ReadInt16LE(this Stream s)
	{
		byte[] array = new byte[2];
		s.ReadExactly(array, 0, 2);
		return (short)((array[0] & 0xFF) | ((array[1] << 8) & 0xFF00));
	}

	public static void WriteInt16LE(this Stream s, short i)
	{
		s.WriteUInt16LE((ushort)i);
	}

	public static void WriteUInt16LE(this Stream s, ushort i)
	{
		s.Write(new byte[2]
		{
			(byte)(i & 0xFF),
			(byte)((i >> 8) & 0xFF)
		}, 0, 2);
	}

	/// <summary>
	/// Read an unsigned 16-bit Big-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static ushort ReadUInt16BE(this Stream s)
	{
		return (ushort)s.ReadInt16BE();
	}

	/// <summary>
	/// Read a signed 16-bit Big-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static short ReadInt16BE(this Stream s)
	{
		byte[] array = new byte[2];
		s.ReadExactly(array, 0, 2);
		return (short)(((array[0] << 8) & 0xFF00) | (array[1] & 0xFF));
	}

	public static void WriteInt16BE(this Stream s, short i)
	{
		s.WriteUInt16BE((ushort)i);
	}

	public static void WriteUInt16BE(this Stream s, ushort i)
	{
		s.Write(new byte[2]
		{
			(byte)((i >> 8) & 0xFF),
			(byte)(i & 0xFF)
		}, 0, 2);
	}

	/// <summary>
	/// Read an unsigned 24-bit little-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static int ReadUInt24LE(this Stream s)
	{
		byte[] array = new byte[3];
		s.ReadExactly(array, 0, 3);
		return (array[0] & 0xFF) | ((array[1] << 8) & 0xFF00) | ((array[2] << 16) & 0xFF0000);
	}

	/// <summary>
	/// Read a signed 24-bit little-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static int ReadInt24LE(this Stream s)
	{
		byte[] array = new byte[3];
		s.ReadExactly(array, 0, 3);
		int num = array[0] & 0xFF;
		num |= (array[1] << 8) & 0xFF00;
		num |= (array[2] << 16) & 0xFF0000;
		if ((array[2] & 0x80) == 128)
		{
			num |= -16777216;
		}
		return num;
	}

	/// <summary>
	/// Read an unsigned 24-bit Big-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static uint ReadUInt24BE(this Stream s)
	{
		byte[] array = new byte[3];
		s.ReadExactly(array, 0, 3);
		return (uint)((array[2] & 0xFF) | ((array[1] << 8) & 0xFF00) | ((array[0] << 16) & 0xFF0000));
	}

	/// <summary>
	/// Read a signed 24-bit Big-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static int ReadInt24BE(this Stream s)
	{
		byte[] array = new byte[3];
		s.ReadExactly(array, 0, 3);
		int num = array[2] & 0xFF;
		num |= (array[1] << 8) & 0xFF00;
		num |= (array[0] << 16) & 0xFF0000;
		if ((array[0] & 0x80) == 128)
		{
			num |= -16777216;
		}
		return num;
	}

	/// <summary>
	/// Read an unsigned 32-bit little-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static uint ReadUInt32LE(this Stream s)
	{
		return (uint)s.ReadInt32LE();
	}

	/// <summary>
	/// Read a signed 32-bit little-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static int ReadInt32LE(this Stream s)
	{
		byte[] array = new byte[4];
		s.ReadExactly(array, 0, 4);
		return (array[0] & 0xFF) | ((array[1] << 8) & 0xFF00) | ((array[2] << 16) & 0xFF0000) | (array[3] << 24);
	}

	public static void WriteInt32LE(this Stream s, int i)
	{
		s.WriteUInt32LE((uint)i);
	}

	public static void WriteUInt32LE(this Stream s, uint i)
	{
		s.Write(new byte[4]
		{
			(byte)(i & 0xFF),
			(byte)((i >> 8) & 0xFF),
			(byte)((i >> 16) & 0xFF),
			(byte)((i >> 24) & 0xFF)
		}, 0, 4);
	}

	/// <summary>
	/// Read an unsigned 32-bit Big-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static uint ReadUInt32BE(this Stream s)
	{
		return (uint)s.ReadInt32BE();
	}

	/// <summary>
	/// Read a signed 32-bit Big-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static int ReadInt32BE(this Stream s)
	{
		byte[] array = new byte[4];
		s.ReadExactly(array, 0, 4);
		return (array[0] << 24) | ((array[1] << 16) & 0xFF0000) | ((array[2] << 8) & 0xFF00) | (array[3] & 0xFF);
	}

	public static void WriteInt32BE(this Stream s, int i)
	{
		s.WriteUInt32BE((uint)i);
	}

	public static void WriteUInt32BE(this Stream s, uint i)
	{
		byte[] array = new byte[4];
		array[3] = (byte)(i & 0xFF);
		array[2] = (byte)((i >> 8) & 0xFF);
		array[1] = (byte)((i >> 16) & 0xFF);
		array[0] = (byte)((i >> 24) & 0xFF);
		s.Write(array, 0, 4);
	}

	/// <summary>
	/// Read an unsigned 64-bit little-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static ulong ReadUInt64LE(this Stream s)
	{
		return (ulong)s.ReadInt64LE();
	}

	/// <summary>
	/// Read a signed 64-bit little-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static long ReadInt64LE(this Stream s)
	{
		byte[] array = new byte[8];
		s.ReadExactly(array, 0, 8);
		return (long)(((((ulong)array[4] & 0xFFuL) | ((ulong)(array[5] << 8) & 0xFF00uL) | ((ulong)(array[6] << 16) & 0xFF0000uL) | (ulong)((array[7] << 24) & 0xFF000000u)) << 32) | ((ulong)array[0] & 0xFFuL) | ((ulong)(array[1] << 8) & 0xFF00uL) | ((ulong)(array[2] << 16) & 0xFF0000uL)) | ((array[3] << 24) & 0xFF000000u);
	}

	public static void WriteInt64LE(this Stream s, long i)
	{
		s.WriteUInt64LE((ulong)i);
	}

	public static void WriteUInt64LE(this Stream s, ulong i)
	{
		byte[] array = new byte[8]
		{
			(byte)(i & 0xFF),
			(byte)((i >> 8) & 0xFF),
			(byte)((i >> 16) & 0xFF),
			(byte)((i >> 24) & 0xFF),
			0,
			0,
			0,
			0
		};
		i >>= 32;
		array[4] = (byte)(i & 0xFF);
		array[5] = (byte)((i >> 8) & 0xFF);
		array[6] = (byte)((i >> 16) & 0xFF);
		array[7] = (byte)((i >> 24) & 0xFF);
		s.Write(array, 0, 8);
	}

	/// <summary>
	/// Read an unsigned 64-bit big-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static ulong ReadUInt64BE(this Stream s)
	{
		return (ulong)s.ReadInt64BE();
	}

	/// <summary>
	/// Read a signed 64-bit big-endian integer from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static long ReadInt64BE(this Stream s)
	{
		byte[] array = new byte[8];
		s.ReadExactly(array, 0, 8);
		return (long)(((((ulong)array[3] & 0xFFuL) | ((ulong)(array[2] << 8) & 0xFF00uL) | ((ulong)(array[1] << 16) & 0xFF0000uL) | (ulong)((array[0] << 24) & 0xFF000000u)) << 32) | ((ulong)array[7] & 0xFFuL) | ((ulong)(array[6] << 8) & 0xFF00uL) | ((ulong)(array[5] << 16) & 0xFF0000uL)) | ((array[4] << 24) & 0xFF000000u);
	}

	public static void WriteInt64BE(this Stream s, long i)
	{
		s.WriteUInt64BE((ulong)i);
	}

	public static void WriteUInt64BE(this Stream s, ulong i)
	{
		byte[] array = new byte[8];
		array[7] = (byte)(i & 0xFF);
		array[6] = (byte)((i >> 8) & 0xFF);
		array[5] = (byte)((i >> 16) & 0xFF);
		array[4] = (byte)((i >> 24) & 0xFF);
		i >>= 32;
		array[3] = (byte)(i & 0xFF);
		array[2] = (byte)((i >> 8) & 0xFF);
		array[1] = (byte)((i >> 16) & 0xFF);
		array[0] = (byte)((i >> 24) & 0xFF);
		s.Write(array, 0, 8);
	}

	/// <summary>
	/// Reads a multibyte value of the specified length from the stream.
	/// </summary>
	/// <param name="s">The stream</param>
	/// <param name="bytes">Must be less than or equal to 8</param>
	/// <returns></returns>
	public static long ReadMultibyteBE(this Stream s, byte bytes)
	{
		if (bytes > 8)
		{
			return 0L;
		}
		long num = 0L;
		byte[] array = s.ReadBytes(bytes);
		for (uint num2 = 0u; num2 < array.Length; num2++)
		{
			num <<= 8;
			num |= array[num2];
		}
		return num;
	}

	/// <summary>
	/// Read a single-precision (4-byte) floating-point value from the stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static float ReadFloat(this Stream s)
	{
		byte[] array = new byte[4];
		s.ReadExactly(array, 0, 4);
		return BitConverter.ToSingle(array, 0);
	}

	/// <summary>
	/// Read a null-terminated ASCII string from the given stream.
	/// </summary>
	/// <param name="s"></param>
	/// <param name="limit"></param>
	/// <returns></returns>
	public static string ReadASCIINullTerminated(this Stream s, int limit = -1)
	{
		StringBuilder stringBuilder = new StringBuilder(255);
		int num;
		while ((limit == -1 || stringBuilder.Length < limit) && (num = s.ReadByte()) > 0)
		{
			stringBuilder.Append((char)num);
		}
		return stringBuilder.ToString();
	}

	/// <summary>
	/// Read a length-prefixed string of the specified encoding type from the file.
	/// The length is a 32-bit little endian integer.
	/// </summary>
	/// <param name="s"></param>
	/// <param name="e">The encoding to use to decode the string.</param>
	/// <returns></returns>
	public static string ReadLengthPrefixedString(this Stream s, Encoding e)
	{
		int num = s.ReadInt32LE();
		byte[] array = new byte[num];
		s.ReadExactly(array, 0, num);
		return e.GetString(array);
	}

	/// <summary>
	/// Read a length-prefixed UTF-8 string from the given stream.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static string ReadLengthUTF8(this Stream s)
	{
		return s.ReadLengthPrefixedString(Encoding.UTF8);
	}

	/// <summary>
	/// Read a given number of bytes from a stream into a new byte array.
	/// </summary>
	/// <param name="s"></param>
	/// <param name="count">Number of bytes to read (maximum)</param>
	/// <returns>New byte array of size &lt;=count.</returns>
	public static byte[] ReadBytes(this Stream s, int count)
	{
		int num = (int)((s.Position + count > s.Length) ? (s.Length - s.Position) : count);
		byte[] array = new byte[num];
		s.ReadExactly(array, 0, num);
		return array;
	}

	/// <summary>
	/// Read a variable-length integral value as found in MIDI messages.
	/// </summary>
	/// <param name="s"></param>
	/// <returns></returns>
	public static int ReadMidiMultiByte(this Stream s)
	{
		int num = 0;
		byte b = (byte)s.ReadByte();
		num += b & 0x7F;
		if (128 == (b & 0x80))
		{
			num <<= 7;
			b = (byte)s.ReadByte();
			num += b & 0x7F;
			if (128 == (b & 0x80))
			{
				num <<= 7;
				b = (byte)s.ReadByte();
				num += b & 0x7F;
				if (128 == (b & 0x80))
				{
					num <<= 7;
					b = (byte)s.ReadByte();
					num += b & 0x7F;
					if (128 == (b & 0x80))
					{
						throw new InvalidDataException("Variable-length MIDI number > 4 bytes");
					}
				}
			}
		}
		return num;
	}

	public static void WriteLE(this Stream s, ushort i)
	{
		s.WriteUInt16LE(i);
	}

	public static void WriteLE(this Stream s, uint i)
	{
		s.WriteUInt32LE(i);
	}

	public static void WriteLE(this Stream s, ulong i)
	{
		s.WriteUInt64LE(i);
	}

	public static void WriteLE(this Stream s, short i)
	{
		s.WriteInt16LE(i);
	}

	public static void WriteLE(this Stream s, int i)
	{
		s.WriteInt32LE(i);
	}

	public static void WriteLE(this Stream s, long i)
	{
		s.WriteInt64LE(i);
	}
}
