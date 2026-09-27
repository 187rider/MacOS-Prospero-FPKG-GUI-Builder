using System;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>
/// Implements the 13 pre-compression shuffles of <see cref="T:LibProsperoPkg.PFS.Compression.ProsperoPfsShufflePattern" /> and their
/// exact inverses. Both directions are deterministic and allocation-light, and round-trip for
/// any input length.
/// </summary>
/// <remarks>
/// Whole vectors (8 or 16 bytes) are shuffled; a trailing partial vector (when the input length
/// is not a multiple of the stride) is passed through unchanged so that
/// <see cref="M:LibProsperoPkg.PFS.Compression.ProsperoPfsShuffle.Deshuffle(System.ReadOnlySpan{System.Byte},LibProsperoPkg.PFS.Compression.ProsperoPfsShufflePattern)" /> exactly reverses
/// <see cref="M:LibProsperoPkg.PFS.Compression.ProsperoPfsShuffle.Shuffle(System.ReadOnlySpan{System.Byte},LibProsperoPkg.PFS.Compression.ProsperoPfsShufflePattern)" />.
/// </remarks>
public static class ProsperoPfsShuffle
{
	private static readonly int[] Fields44 = new int[2] { 4, 4 };

	private static readonly int[] Fields224 = new int[3] { 2, 2, 4 };

	private static readonly int[] Fields116 = new int[3] { 1, 1, 6 };

	private static readonly int[] Fields11111111 = new int[8] { 1, 1, 1, 1, 1, 1, 1, 1 };

	private static readonly int[] Fields8224 = new int[4] { 8, 2, 2, 4 };

	private static readonly int[] Fields116224 = new int[6] { 1, 1, 6, 2, 2, 4 };

	private static readonly int[] Fields116116 = new int[6] { 1, 1, 6, 1, 1, 6 };

	private static readonly int[] Fields4444 = new int[4] { 4, 4, 4, 4 };

	private static readonly int[] Fields88 = new int[2] { 8, 8 };

	private static readonly int[] Fields844 = new int[3] { 8, 4, 4 };

	private static readonly int[] Fields26 = new int[2] { 2, 6 };

	private static readonly int[] Fields2626 = new int[4] { 2, 6, 2, 6 };

	/// <summary>
	/// Returns the byte stride (8 or 16, or 0 for <see cref="F:LibProsperoPkg.PFS.Compression.ProsperoPfsShufflePattern.None" />) and the
	/// field sizes that make up one vector for <paramref name="pattern" />.
	/// </summary>
	/// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="pattern" /> is not a defined pattern.</exception>
	public static (int Stride, int[] Fields) Describe(ProsperoPfsShufflePattern pattern)
	{
		return pattern switch
		{
			ProsperoPfsShufflePattern.None => (Stride: 0, Fields: Array.Empty<int>()), 
			ProsperoPfsShufflePattern.Shuffle44 => (Stride: 8, Fields: Fields44), 
			ProsperoPfsShufflePattern.Shuffle224 => (Stride: 8, Fields: Fields224), 
			ProsperoPfsShufflePattern.Shuffle116 => (Stride: 8, Fields: Fields116), 
			ProsperoPfsShufflePattern.Shuffle11111111 => (Stride: 8, Fields: Fields11111111), 
			ProsperoPfsShufflePattern.Shuffle8224 => (Stride: 16, Fields: Fields8224), 
			ProsperoPfsShufflePattern.Shuffle116224 => (Stride: 16, Fields: Fields116224), 
			ProsperoPfsShufflePattern.Shuffle116116 => (Stride: 16, Fields: Fields116116), 
			ProsperoPfsShufflePattern.Shuffle4444 => (Stride: 16, Fields: Fields4444), 
			ProsperoPfsShufflePattern.Shuffle88 => (Stride: 16, Fields: Fields88), 
			ProsperoPfsShufflePattern.Shuffle844 => (Stride: 16, Fields: Fields844), 
			ProsperoPfsShufflePattern.Shuffle26 => (Stride: 8, Fields: Fields26), 
			ProsperoPfsShufflePattern.Shuffle2626 => (Stride: 16, Fields: Fields2626), 
			_ => throw new ArgumentOutOfRangeException("pattern", pattern, "Undefined PFS shuffle pattern."), 
		};
	}

	/// <summary>Applies the forward shuffle for <paramref name="pattern" /> to <paramref name="input" />.</summary>
	/// <returns>A new buffer the same length as <paramref name="input" /> with the bytes shuffled.</returns>
	/// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="pattern" /> is not a defined pattern.</exception>
	public static byte[] Shuffle(ReadOnlySpan<byte> input, ProsperoPfsShufflePattern pattern)
	{
		(int Stride, int[] Fields) tuple = Describe(pattern);
		int item = tuple.Stride;
		int[] item2 = tuple.Fields;
		byte[] array = new byte[input.Length];
		if (item == 0)
		{
			input.CopyTo(array);
			return array;
		}
		int num = input.Length / item;
		int num2 = 0;
		int num3 = 0;
		int[] array2 = item2;
		foreach (int num4 in array2)
		{
			for (int j = 0; j < num; j++)
			{
				input.Slice(j * item + num3, num4).CopyTo(array.AsSpan(num2, num4));
				num2 += num4;
			}
			num3 += num4;
		}
		int start = num * item;
		input.Slice(start).CopyTo(array.AsSpan(num2));
		return array;
	}

	/// <summary>Reverses <see cref="M:LibProsperoPkg.PFS.Compression.ProsperoPfsShuffle.Shuffle(System.ReadOnlySpan{System.Byte},LibProsperoPkg.PFS.Compression.ProsperoPfsShufflePattern)" /> for <paramref name="pattern" />.</summary>
	/// <returns>A new buffer the same length as <paramref name="input" /> with the bytes restored.</returns>
	/// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="pattern" /> is not a defined pattern.</exception>
	public static byte[] Deshuffle(ReadOnlySpan<byte> input, ProsperoPfsShufflePattern pattern)
	{
		(int Stride, int[] Fields) tuple = Describe(pattern);
		int item = tuple.Stride;
		int[] item2 = tuple.Fields;
		byte[] array = new byte[input.Length];
		if (item == 0)
		{
			input.CopyTo(array);
			return array;
		}
		int num = input.Length / item;
		int num2 = 0;
		int num3 = 0;
		int[] array2 = item2;
		foreach (int num4 in array2)
		{
			for (int j = 0; j < num; j++)
			{
				input.Slice(num2, num4).CopyTo(array.AsSpan(j * item + num3, num4));
				num2 += num4;
			}
			num3 += num4;
		}
		int start = num * item;
		input.Slice(num2).CopyTo(array.AsSpan(start));
		return array;
	}
}
