using System;
using System.IO;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Represents a File in a PFS image builder.
/// </summary>
public class FSFile : FSNode
{
	/// <summary>
	/// Lower values are placed earlier when read-optimized physical file ordering is enabled.
	/// The default preserves the existing traversal order.
	/// </summary>
	public int LayoutPriority;

	private long _compressedSize;

	/// <summary>
	/// Call this to write the file to a stream.
	/// </summary>
	public readonly Action<Stream> Write;

	/// <summary>
	/// Flag for PFSC encoded files
	/// </summary>
	public bool Compress;

	/// <summary>
	/// True for the ppr_pfs PFSC v2 profile, whose inode Size remains the logical size while
	/// Blocks describes the smaller stored container extent.
	/// </summary>
	public bool PprKrakenCompression;

	public string? FilePath { get; set; }

	public override long CompressedSize => _compressedSize;

	/// <summary>
	/// Creates an FSFile from a real on-disk file.
	/// You need to set the name, inode, and parent.
	/// </summary>
	/// <param name="origFileName">Real path to the file.</param>
	public FSFile(string origFileName)
	{
		FilePath = origFileName;
		Write = (Stream destination) =>
		{
			using FileStream fileStream = new FileStream(origFileName, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
			fileStream.CopyTo(destination, 1048576);
		};
		Size = new FileInfo(origFileName).Length;
		_compressedSize = Size;
	}

	/// <summary>
	/// Opens a readable stream for this file's contents.
	/// </summary>
	public virtual Stream OpenRead()
	{
		if (FilePath != null && File.Exists(FilePath))
		{
			return new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
		}
		var ms = new MemoryStream();
		Write(ms);
		ms.Position = 0;
		return ms;
	}

	/// <summary>
	/// Creates an FSFile that represents the PFS image that will be created by the given PfsBuilder.
	/// Useful for creating the pfs_image.dat file within an outer PFS.
	/// You need to set the inode and parent.
	/// </summary>
	/// <param name="b">the PfsBuilder that this file represents</param>
	public FSFile(PfsBuilder b)
	{
		PFSCWriter pfsc = new PFSCWriter(b.CalculatePfsSize());
		Write = (Stream s) =>
		{
			pfsc.WritePFSCHeader(s);
			b.WriteImage(new OffsetStream(s, s.Position));
		};
		_compressedSize = b.CalculatePfsSize();
		Size = _compressedSize + pfsc.HeaderSize;
		name = "pfs_image.dat";
		Compress = true;
	}

	/// <summary>
	/// A generic constructor for anything that can be written to a stream.
	/// Don't forget to set the inode and parent.
	/// </summary>
	/// <param name="writer">A function that takes a Stream and writes this file's data to it.</param>
	/// <param name="name">This file's name</param>
	/// <param name="size">The total size in bytes that will be written by writer</param>
	public FSFile(Action<Stream> writer, string name, long size)
	{
		Write = writer;
		base.name = name;
		Size = size;
		_compressedSize = Size;
	}

	/// <summary>
	/// Constructs an FSFile for a pre-rendered payload whose on-disk size differs from its
	/// logical (decompressed) size — e.g. a genuinely PFSC-compressed pfs_image.dat. This is
	/// the explicit-size counterpart of <see cref="M:LibProsperoPkg.PFS.FSFile.#ctor(LibProsperoPkg.PFS.PfsBuilder)" />, which only emits a
	/// PFSC header over uncompressed blocks.
	/// </summary>
	/// <param name="writer">A function that writes exactly <paramref name="size" /> bytes (the on-disk image).</param>
	/// <param name="name">This file's name.</param>
	/// <param name="size">The on-disk byte count <paramref name="writer" /> produces (drives the inode block table).</param>
	/// <param name="compressedSize">The logical/decompressed size recorded in the inode (SizeCompressed).</param>
	/// <param name="compress">Set true to mark the inode as PFSC-compressed.</param>
	public FSFile(Action<Stream> writer, string name, long size, long compressedSize, bool compress)
	{
		Write = writer;
		base.name = name;
		Size = size;
		_compressedSize = compressedSize;
		Compress = compress;
	}
}
