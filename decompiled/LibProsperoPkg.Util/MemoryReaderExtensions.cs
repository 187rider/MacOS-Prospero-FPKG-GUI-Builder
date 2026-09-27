using System.Runtime.InteropServices;

namespace LibProsperoPkg.Util;

internal static class MemoryReaderExtensions
{
	public static void Read<T>(this IMemoryReader reader, long pos, out T value) where T : struct
	{
		int num = Marshal.SizeOf(typeof(T));
		byte[] array = new byte[num];
		reader.Read(pos, array, 0, num);
		value = ByteArrayToStructure<T>(array, 0);
	}

	public static void ReadArray<T>(this IMemoryReader reader, long pos, T[] value, int offset, int count) where T : struct
	{
		if (value is byte[] buf)
		{
			reader.Read(pos, buf, offset, count);
			return;
		}
		int num = Marshal.SizeOf(typeof(T));
		byte[] array = new byte[num * count];
		reader.Read(pos, array, 0, num * count);
		for (int i = 0; i < count; i++)
		{
			value[i] = ByteArrayToStructure<T>(array, i * num);
		}
	}

	private unsafe static T ByteArrayToStructure<T>(byte[] bytes, int offset) where T : struct
	{
		fixed (byte* ptr = &bytes[offset])
		{
			return (T)Marshal.PtrToStructure((nint)ptr, typeof(T));
		}
	}
}
