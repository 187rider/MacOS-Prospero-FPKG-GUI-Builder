using System;
using System.IO;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Random-access plaintext view of an encrypted outer PFS. Reads are decrypted one outer block at
/// a time and the most recently used block is cached, allowing <see cref="T:LibProsperoPkg.PFS.PfsReader" /> to traverse
/// very large signed indirect maps without creating a second plaintext image.
/// </summary>
public sealed class ProsperoOuterPfsDecryptReader : IMemoryReader, IDisposable
{
	private readonly Stream input;

	private readonly bool ownsInput;

	private readonly long startOffset;

	private readonly long length;

	private readonly int blockSize;

	private readonly ProsperoOuterBlockKind[] blockKinds;

	private readonly XtsBlockTransform xts;

	private readonly byte[] cachedBlock;

	private readonly object sync = new object();

	private int cachedBlockIndex = -1;

	private int cachedBlockLength;

	private bool disposed;

	public ProsperoOuterPfsDecryptReader(Stream input, long length, ReadOnlySpan<byte> tweakKey, ReadOnlySpan<byte> dataKey, ReadOnlySpan<ProsperoOuterBlockKind> blockKinds, int blockSize = 65536, long startOffset = 0L, bool takeOwnership = false)
	{
		ArgumentNullException.ThrowIfNull(input, "input");
		if (!input.CanRead || !input.CanSeek)
		{
			throw new ArgumentException("Input must be a readable, seekable stream.", "input");
		}
		if (length < 0)
		{
			throw new ArgumentOutOfRangeException("length");
		}
		if (startOffset < 0)
		{
			throw new ArgumentOutOfRangeException("startOffset");
		}
		if (blockSize <= 0 || (blockSize & 0xF) != 0)
		{
			throw new ArgumentOutOfRangeException("blockSize", "Block size must be a positive multiple of 16.");
		}
		if (tweakKey.Length != 16)
		{
			throw new ArgumentException("Tweak key must be 16 bytes.", "tweakKey");
		}
		if (dataKey.Length != 16)
		{
			throw new ArgumentException("Data key must be 16 bytes.", "dataKey");
		}
		long num = checked(length + blockSize - 1) / blockSize;
		if (num > int.MaxValue)
		{
			throw new ArgumentOutOfRangeException("length", "Outer-PFS block count exceeds Int32.");
		}
		if (blockKinds.Length != (int)num)
		{
			throw new ArgumentException($"blockKinds length ({blockKinds.Length}) must equal the block count ({num}).", "blockKinds");
		}
		if (checked(startOffset + length) > input.Length)
		{
			throw new ArgumentException("The requested outer-PFS range exceeds the input stream.", "length");
		}
		this.input = input;
		this.length = length;
		this.startOffset = startOffset;
		this.blockSize = blockSize;
		this.blockKinds = blockKinds.ToArray();
		ownsInput = takeOwnership;
		cachedBlock = new byte[blockSize];
		xts = new XtsBlockTransform(dataKey.ToArray(), tweakKey.ToArray());
	}

	public void Read(long pos, byte[] buf, int offset, int count)
	{
		ArgumentNullException.ThrowIfNull(buf, "buf");
		if (pos < 0 || pos > length)
		{
			throw new ArgumentOutOfRangeException("pos");
		}
		if (offset < 0 || count < 0 || offset > buf.Length - count)
		{
			throw new ArgumentOutOfRangeException("offset");
		}
		if (count > length - pos)
		{
			throw new EndOfStreamException("Read exceeds the outer-PFS range.");
		}
		lock (sync)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			while (count != 0)
			{
				int num;
				checked
				{
					int blockIndex = (int)unchecked(pos / blockSize);
					num = (int)unchecked(pos % blockSize);
					LoadBlock(blockIndex);
				}
				int num2 = Math.Min(count, cachedBlockLength - num);
				if (num2 <= 0)
				{
					throw new EndOfStreamException("Read reached a truncated outer-PFS block.");
				}
				Buffer.BlockCopy(cachedBlock, num, buf, offset, num2);
				pos += num2;
				offset += num2;
				count -= num2;
			}
		}
	}

	private void LoadBlock(int blockIndex)
	{
		if (blockIndex == cachedBlockIndex)
		{
			return;
		}
		checked
		{
			long num = unchecked((long)blockIndex) * unchecked((long)blockSize);
			int num2 = (int)Math.Min(blockSize, length - num);
			Array.Clear(cachedBlock);
			input.Position = startOffset + num;
			input.ReadExactly(cachedBlock, 0, num2);
			ProsperoOuterBlockKind prosperoOuterBlockKind = blockKinds[blockIndex];
			if (prosperoOuterBlockKind != ProsperoOuterBlockKind.Plaintext)
			{
				ulong sectorNum = ProsperoOuterPfsSignature.BlockSector(blockIndex, prosperoOuterBlockKind == ProsperoOuterBlockKind.Signed);
				if (num2 == blockSize)
				{
					xts.CryptSector(cachedBlock, sectorNum);
				}
				else
				{
					byte[] array = cachedBlock.AsSpan(0, num2).ToArray();
					xts.CryptSector(array, sectorNum);
					array.CopyTo(cachedBlock, 0);
				}
			}
			cachedBlockLength = num2;
			cachedBlockIndex = blockIndex;
		}
	}

	public void Dispose()
	{
		lock (sync)
		{
			if (!disposed)
			{
				disposed = true;
				xts.Dispose();
				if (ownsInput)
				{
					input.Dispose();
				}
			}
		}
	}
}
