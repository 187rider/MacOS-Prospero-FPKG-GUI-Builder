using System.Collections.Generic;
using System.Numerics;

namespace LibProsperoPkg.PFS.Compression.Oodle;

/// <summary>Accumulates bits MSB-first into bytes; one instance per bitstream direction.</summary>
internal sealed class KrakenBitWriter
{
	private readonly List<byte> _bytes = new List<byte>();

	private uint _cur;

	private int _curBits;

	/// <summary>Number of whole bytes produced once the trailing partial byte is flushed.</summary>
	public int ByteLength => _bytes.Count + ((_curBits > 0) ? 1 : 0);

	/// <summary>Writes the low <paramref name="n" /> bits of <paramref name="value" />, MSB-first.</summary>
	public void WriteBits(uint value, int n)
	{
		for (int num = n - 1; num >= 0; num--)
		{
			WriteBit((value >> num) & 1);
		}
	}

	/// <summary>Writes <paramref name="count" /> zero bits.</summary>
	public void WriteZeros(int count)
	{
		for (int i = 0; i < count; i++)
		{
			WriteBit(0u);
		}
	}

	private void WriteBit(uint bit)
	{
		_cur = (_cur << 1) | (bit & 1);
		_curBits++;
		if (_curBits == 8)
		{
			_bytes.Add((byte)_cur);
			_cur = 0u;
			_curBits = 0;
		}
	}

	/// <summary>Writes the Elias-gamma length-stream-size prefix for <paramref name="size" /> (&gt;= 0).</summary>
	public void WriteLenStreamSizePrefix(int size)
	{
		uint value = (uint)(size + 1);
		int num = 32 - BitOperations.LeadingZeroCount(value);
		WriteZeros(num - 1);
		WriteBits(value, num);
	}

	/// <summary>Writes a Kraken length code for <paramref name="value" /> (the u32 length-stream value).</summary>
	public void WriteLength(uint value)
	{
		uint value2 = value + 64;
		int num = 32 - BitOperations.LeadingZeroCount(value2);
		WriteZeros(num - 7);
		WriteBits(value2, num);
	}

	/// <summary>
	/// Encodes distance <paramref name="distance" /> (&gt;= 8): returns the packed-offset command byte and
	/// writes the associated extra bits. Only the &lt; 0xF0 bucket (window &lt;= 256 KiB) is produced.
	/// </summary>
	public byte WriteDistance(int distance)
	{
		uint num = (uint)(distance + 248);
		int num2 = 31 - BitOperations.LeadingZeroCount(num) - 8;
		int num3 = (int)num - (1 << num2 + 8);
		uint num4 = (uint)(num3 & 0xF);
		uint value = (uint)num3 >> 4;
		int n = num2 + 4;
		WriteBits(value, n);
		return (byte)((uint)(num2 << 4) | num4);
	}

	/// <summary>Flushes any partial byte (zero-padded) and returns the produced bytes in write order.</summary>
	public byte[] ToBytes()
	{
		if (_curBits > 0)
		{
			_bytes.Add((byte)(_cur << 8 - _curBits));
			_cur = 0u;
			_curBits = 0;
		}
		return _bytes.ToArray();
	}
}
