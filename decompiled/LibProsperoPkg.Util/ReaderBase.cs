using System.IO;
using System.Runtime.InteropServices;

namespace LibProsperoPkg.Util;

public class ReaderBase
{
	[StructLayout(LayoutKind.Explicit, Size = 8)]
	private struct Storage
	{
		[FieldOffset(0)]
		public byte u8;

		[FieldOffset(0)]
		public sbyte s8;

		[FieldOffset(0)]
		public ushort u16;

		[FieldOffset(0)]
		public short s16;

		[FieldOffset(0)]
		public uint u32;

		[FieldOffset(0)]
		public int s32;

		[FieldOffset(0)]
		public ulong u64;

		[FieldOffset(0)]
		public long s64;

		[FieldOffset(0)]
		public float f32;

		[FieldOffset(0)]
		public double f64;

		[FieldOffset(0)]
		public unsafe fixed byte buf[8];
	}

	private Storage buffer;

	protected Stream s;

	protected bool flipEndian;

	protected ReaderBase(bool flipEndian, Stream stream)
	{
		s = stream;
		this.flipEndian = flipEndian;
	}

	private unsafe ref Storage ReadEndian(int count)
	{
		if (flipEndian)
		{
			for (int num = count - 1; num >= 0; num--)
			{
				buffer.buf[num] = (byte)s.ReadByte();
			}
		}
		else
		{
			for (int i = 0; i < count; i++)
			{
				buffer.buf[i] = (byte)s.ReadByte();
			}
		}
		return ref buffer;
	}

	protected byte Byte()
	{
		return ReadEndian(1).u8;
	}

	protected sbyte SByte()
	{
		return ReadEndian(1).s8;
	}

	protected ushort UShort()
	{
		return ReadEndian(2).u16;
	}

	protected short Short()
	{
		return ReadEndian(2).s16;
	}

	protected uint UInt()
	{
		return ReadEndian(4).u32;
	}

	protected int Int()
	{
		return ReadEndian(4).s32;
	}

	protected ulong ULong()
	{
		return ReadEndian(8).u64;
	}

	protected long Long()
	{
		return ReadEndian(8).s64;
	}

	protected byte[] ReadBytes(int count)
	{
		byte[] result = new byte[count];
		s.ReadExactly(result, 0, count);
		return result;
	}
}
