using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LibProsperoPkg.PFS;
using LibProsperoPkg.PlayGo;

namespace LibProsperoPkg.PKG;

public static class ProsperoSiArchive
{
	public const string PfsImageXmlPath = "common/etc/pfsimage.xml";

	public const string PlayGoChunkDatPath = "common/etc/playgo-chunk.dat";

	public const string NapsMeta18Path = "common/etc/naps_meta_18.dat";

	public static ReadOnlySpan<int> NapsMeta300Ids => new int[4] { 300, 301, 302, 308 };

	public static IReadOnlyList<ProsperoSiMember> BuildMembers(string contentId, byte[]? pfsImageXml, byte[]? playGoChunkDat = null, byte[]? napsMeta18 = null, byte[]? napsMeta300 = null, byte[]? playGoChunkCrc = null, byte[]? finalizedMountImage = null)
	{
		ArgumentException.ThrowIfNullOrEmpty(contentId, "contentId");
		if (playGoChunkCrc == null)
		{
			playGoChunkCrc = ((finalizedMountImage != null && finalizedMountImage.Length != 0) ? ProsperoPlayGo.BuildChunkCrc(finalizedMountImage) : null);
		}
		List<ProsperoSiMember> list = new List<ProsperoSiMember>();
		if (napsMeta18 != null)
		{
			list.Add(new ProsperoSiMember("common/etc/naps_meta_18.dat", napsMeta18));
		}
		if (napsMeta300 != null)
		{
			ReadOnlySpan<int> napsMeta300Ids = NapsMeta300Ids;
			for (int i = 0; i < napsMeta300Ids.Length; i++)
			{
				int value = napsMeta300Ids[i];
				list.Add(new ProsperoSiMember($"common/etc/naps_meta_{value}.dat", napsMeta300));
			}
		}
		if (pfsImageXml != null)
		{
			list.Add(new ProsperoSiMember("common/etc/pfsimage.xml", pfsImageXml));
		}
		if (playGoChunkDat != null)
		{
			list.Add(new ProsperoSiMember("common/etc/playgo-chunk.dat", playGoChunkDat));
		}
		if (playGoChunkCrc != null)
		{
			list.Add(new ProsperoSiMember("config/" + contentId + "/playgo-chunk.crc", playGoChunkCrc));
		}
		return list;
	}

	public static byte[] BuildDebugSiSegment(ProsperoPfsImageXmlOptions pfsImageXml, byte[]? playGoChunkDat, byte[] mountImage, long innerImageSize = 0L, ICollection<string>? warnings = null, byte[]? napsMeta18 = null, bool includePfsImageXml = true, IReadOnlyList<(string Path, long Size)>? contentFiles = null, ProsperoPs5InnerImageResult? innerImage = null, IProsperoNapsIntegrityProvider? integrityProvider = null, byte[]? pfsImageKey = null, byte[]? pfsImageSeed = null)
	{
		ArgumentNullException.ThrowIfNull(pfsImageXml, "pfsImageXml");
		ArgumentNullException.ThrowIfNull(mountImage, "mountImage");
		byte[] napsMeta19 = null;
		ulong num = (ulong)((innerImageSize > 0) ? innerImageSize : 0);
		if (num == 0L)
		{
			int num2 = 160;
			if (mountImage.Length >= num2 + 8)
			{
				num = BinaryPrimitives.ReadUInt64LittleEndian(mountImage.AsSpan(num2, 8));
			}
		}
		if (num >= 131072)
		{
			napsMeta19 = ProsperoNapsMeta.BuildMeta300FromInnerImageSize(num);
		}
		if (napsMeta18 == null && num >= 131072)
		{
			byte[] array = ProsperoNapsMeta.BuildMeta18(num, mountImage, contentFiles ?? Array.Empty<(string, long)>(), innerImage, integrityProvider, pfsImageKey, pfsImageSeed);
			if (array.Length != 0)
			{
				napsMeta18 = array;
			}
		}
		byte[] pfsImageXml2 = (includePfsImageXml ? Encoding.UTF8.GetBytes(BuildPfsImageXml(pfsImageXml, warnings)) : null);
		return WriteZip(BuildMembers(pfsImageXml.ContentId, pfsImageXml2, playGoChunkDat, napsMeta18, napsMeta19, null, mountImage));
	}

	public static byte[] BuildDebugSiSegment(ProsperoPfsImageXmlOptions pfsImageXml, byte[]? playGoChunkDat, Stream mountImage, long innerImageSize = 0L, ICollection<string>? warnings = null, byte[]? napsMeta18 = null, bool includePfsImageXml = true, IReadOnlyList<(string Path, long Size)>? contentFiles = null, ProsperoPs5InnerImageResult? innerImage = null, IProsperoNapsIntegrityProvider? integrityProvider = null, byte[]? pfsImageKey = null, byte[]? pfsImageSeed = null, Action<string>? log = null, int maxHashingThreads = 2)
	{
		ArgumentNullException.ThrowIfNull(pfsImageXml, "pfsImageXml");
		ArgumentNullException.ThrowIfNull(mountImage, "mountImage");
		if (!mountImage.CanRead || !mountImage.CanSeek)
		{
			throw new ArgumentException("Mount-image stream must be readable and seekable.", "mountImage");
		}
		ulong num = (ulong)((innerImageSize > 0) ? innerImageSize : 0);
		if (num == 0L && mountImage.Length >= 168)
		{
			long position = mountImage.Position;
			try
			{
				Span<byte> span = stackalloc byte[8];
				mountImage.Position = 160L;
				mountImage.ReadExactly(span);
				num = BinaryPrimitives.ReadUInt64LittleEndian(span);
			}
			finally
			{
				mountImage.Position = position;
			}
		}
		byte[] napsMeta19 = ((num >= 131072) ? ProsperoNapsMeta.BuildMeta300FromInnerImageSize(num) : null);
		if (napsMeta18 == null && num >= 131072 && innerImage != null)
		{
			byte[] array = ProsperoNapsMeta.BuildMeta18(num, mountImage.Length, contentFiles ?? Array.Empty<(string, long)>(), innerImage, integrityProvider, pfsImageKey, pfsImageSeed, log, maxHashingThreads);
			if (array.Length != 0)
			{
				napsMeta18 = array;
			}
		}
		byte[] pfsImageXml2 = (includePfsImageXml ? Encoding.UTF8.GetBytes(BuildPfsImageXml(pfsImageXml, warnings)) : null);
		long position2 = mountImage.Position;
		byte[] playGoChunkCrc;
		try
		{
			mountImage.Position = 0L;
			playGoChunkCrc = ProsperoPlayGo.BuildChunkCrc(mountImage, mountImage.Length, log);
		}
		finally
		{
			mountImage.Position = position2;
		}
		return WriteZip(BuildMembers(pfsImageXml.ContentId, pfsImageXml2, playGoChunkDat, napsMeta18, napsMeta19, playGoChunkCrc));
	}

	public static byte[] WriteZip(IReadOnlyList<ProsperoSiMember> members)
	{
		ArgumentNullException.ThrowIfNull(members, "members");
		using MemoryStream memoryStream = new MemoryStream();
		using BinaryWriter binaryWriter = new BinaryWriter(memoryStream, Encoding.ASCII, leaveOpen: true);
		(uint, int, long)[] array = new (uint, int, long)[members.Count];
		for (int i = 0; i < members.Count; i++)
		{
			ProsperoSiMember prosperoSiMember = members[i];
			byte[] bytes = Encoding.ASCII.GetBytes(prosperoSiMember.Path);
			uint num = ZipCrc32(prosperoSiMember.Content);
			array[i] = (num, prosperoSiMember.Content.Length, memoryStream.Position);
			binaryWriter.Write(67324752u);
			binaryWriter.Write((ushort)20);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)33);
			binaryWriter.Write(num);
			binaryWriter.Write((uint)prosperoSiMember.Content.Length);
			binaryWriter.Write((uint)prosperoSiMember.Content.Length);
			binaryWriter.Write((ushort)bytes.Length);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write(bytes);
			binaryWriter.Write(prosperoSiMember.Content);
		}
		long position = memoryStream.Position;
		for (int j = 0; j < members.Count; j++)
		{
			byte[] bytes2 = Encoding.ASCII.GetBytes(members[j].Path);
			binaryWriter.Write(33639248u);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)20);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)33);
			binaryWriter.Write(array[j].Item1);
			binaryWriter.Write((uint)array[j].Item2);
			binaryWriter.Write((uint)array[j].Item2);
			binaryWriter.Write((ushort)bytes2.Length);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write(0u);
			binaryWriter.Write((uint)array[j].Item3);
			binaryWriter.Write(bytes2);
		}
		long num2 = memoryStream.Position - position;
		if (members.Count > 65535 || position > uint.MaxValue || num2 > uint.MaxValue)
		{
			throw new InvalidDataException("SI ZIP exceeds the non-Zip64 publisher layout.");
		}
		binaryWriter.Write(101010256u);
		binaryWriter.Write((ushort)0);
		binaryWriter.Write((ushort)0);
		binaryWriter.Write((ushort)members.Count);
		binaryWriter.Write((ushort)members.Count);
		binaryWriter.Write((uint)num2);
		binaryWriter.Write((uint)position);
		binaryWriter.Write((ushort)0);
		binaryWriter.Flush();
		return memoryStream.ToArray();
	}

	private static uint ZipCrc32(ReadOnlySpan<byte> data)
	{
		uint num = uint.MaxValue;
		ReadOnlySpan<byte> readOnlySpan = data;
		for (int i = 0; i < readOnlySpan.Length; i++)
		{
			byte b = readOnlySpan[i];
			num ^= b;
			for (int j = 0; j < 8; j++)
			{
				num = (num >> 1) ^ (0xEDB88320u & (0 - (num & 1)));
			}
		}
		return num ^ 0xFFFFFFFFu;
	}

	public static string FormatDigest(ReadOnlySpan<byte> digest)
	{
		ReadOnlySpan<byte> readOnlySpan = (digest.IsEmpty ? ((ReadOnlySpan<byte>)stackalloc byte[32]) : digest);
		ReadOnlySpan<byte> readOnlySpan2 = readOnlySpan;
		StringBuilder stringBuilder = new StringBuilder(readOnlySpan2.Length * 5);
		for (int i = 0; i < readOnlySpan2.Length; i++)
		{
			if (i != 0)
			{
				stringBuilder.Append((i % 16 == 0) ? '\n' : ' ');
			}
			stringBuilder.Append("0x").Append(readOnlySpan2[i].ToString("x2", CultureInfo.InvariantCulture));
		}
		return stringBuilder.ToString();
	}

	public static string BuildPfsImageXml(ProsperoPfsImageXmlOptions options, ICollection<string>? warnings = null)
	{
		ArgumentNullException.ThrowIfNull(options, "options");
		ArgumentException.ThrowIfNullOrEmpty(options.ContentId, "options.ContentId");
		NoteIfPlaceholder(options.ContentDigest, "content-digest");
		NoteIfPlaceholder(options.GameDigest, "game-digest");
		NoteIfPlaceholder(options.HeaderDigest, "header-digest");
		NoteIfPlaceholder(options.SystemDigest, "system-digest");
		NoteIfPlaceholder(options.ParamDigest, "param-digest");
		NoteIfPlaceholder(options.PackageDigest, "package-digest");
		NoteIfPlaceholder(options.BodyDigest, "body-digest");
		NoteIfPlaceholder(options.SblockDigest ?? options.GameDigest, "sblock-digest");
		NoteIfPlaceholder(options.FixedInfoDigest, "fixed-info-digest");
		byte[] pfsImageSeed = options.PfsImageSeed;
		string value = Indent(FormatDigest((pfsImageSeed != null && pfsImageSeed.Length != 0) ? pfsImageSeed : null), "      ");
		long containerSize = options.ContainerSize;
		long bodyOffset = options.BodyOffset;
		long num = containerSize - bodyOffset;
		long num2 = options.PfsImageOffset + options.PfsImageSize;
		long num3 = num2 + containerSize;
		string value2 = options.LongName ?? BuildLongName(options);
		StringBuilder stringBuilder = new StringBuilder(8192);
		stringBuilder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
		stringBuilder.Append("<package-configuration version=\"1.0\" type=\"package-info\">\n");
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder3 = stringBuilder2;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(49, 1, stringBuilder2);
		handler.AppendLiteral("  <config version=\"");
		handler.AppendFormatted(options.ContentVersion);
		handler.AppendLiteral("\" metadata=\"0\" primary=\"yes\">\n");
		stringBuilder3.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder4 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(30, 1, stringBuilder2);
		handler.AppendLiteral("    <content-id>");
		handler.AppendFormatted(options.ContentId);
		handler.AppendLiteral("</content-id>\n");
		stringBuilder4.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder5 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(30, 1, stringBuilder2);
		handler.AppendLiteral("    <primary-id>");
		handler.AppendFormatted(options.PrimaryId ?? options.ContentId);
		handler.AppendLiteral("</primary-id>\n");
		stringBuilder5.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder6 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(26, 1, stringBuilder2);
		handler.AppendLiteral("    <longname>");
		handler.AppendFormatted(value2);
		handler.AppendLiteral("</longname>\n");
		stringBuilder6.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder7 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(56, 1, stringBuilder2);
		handler.AppendLiteral("    <required-system-version>");
		handler.AppendFormatted(options.RequiredSystemVersion);
		handler.AppendLiteral("</required-system-version>\n");
		stringBuilder7.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder8 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(26, 1, stringBuilder2);
		handler.AppendLiteral("    <drm-type>");
		handler.AppendFormatted(options.DrmType);
		handler.AppendLiteral("</drm-type>\n");
		stringBuilder8.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder9 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(34, 1, stringBuilder2);
		handler.AppendLiteral("    <content-type>");
		handler.AppendFormatted(options.ContentType);
		handler.AppendLiteral("</content-type>\n");
		stringBuilder9.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder10 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(42, 1, stringBuilder2);
		handler.AppendLiteral("    <application-type>");
		handler.AppendFormatted(options.ApplicationType);
		handler.AppendLiteral("</application-type>\n");
		stringBuilder10.Append(ref handler);
		stringBuilder.Append("    <num-of-images>1</num-of-images>\n");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder11 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(34, 1, stringBuilder2);
		handler.AppendLiteral("    <package-size>");
		handler.AppendFormatted(options.PackageSize);
		handler.AppendLiteral("</package-size>\n");
		stringBuilder11.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder12 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(36, 1, stringBuilder2);
		handler.AppendLiteral("    <version-date>0x");
		handler.AppendFormatted(options.VersionDate, "x8");
		handler.AppendLiteral("</version-date>\n");
		stringBuilder12.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder13 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(36, 1, stringBuilder2);
		handler.AppendLiteral("    <version-hash>0x");
		handler.AppendFormatted(options.VersionHash, "x8");
		handler.AppendLiteral("</version-hash>\n");
		stringBuilder13.Append(ref handler);
		stringBuilder.Append("  </config>\n");
		stringBuilder.Append("  <digests version=\"1.2\" major-param-version=\"0\">\n");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder14 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(50, 1, stringBuilder2);
		handler.AppendLiteral("    <content-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.ContentDigest), "      "));
		handler.AppendLiteral("\n    </content-digest>\n");
		stringBuilder14.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder15 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(44, 1, stringBuilder2);
		handler.AppendLiteral("    <game-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.GameDigest), "      "));
		handler.AppendLiteral("\n    </game-digest>\n");
		stringBuilder15.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder16 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(48, 1, stringBuilder2);
		handler.AppendLiteral("    <header-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.HeaderDigest), "      "));
		handler.AppendLiteral("\n    </header-digest>\n");
		stringBuilder16.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder17 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(48, 1, stringBuilder2);
		handler.AppendLiteral("    <system-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.SystemDigest), "      "));
		handler.AppendLiteral("\n    </system-digest>\n");
		stringBuilder17.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder18 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(46, 1, stringBuilder2);
		handler.AppendLiteral("    <param-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.ParamDigest), "      "));
		handler.AppendLiteral("\n    </param-digest>\n");
		stringBuilder18.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder19 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(50, 1, stringBuilder2);
		handler.AppendLiteral("    <package-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.PackageDigest), "      "));
		handler.AppendLiteral("\n    </package-digest>\n");
		stringBuilder19.Append(ref handler);
		stringBuilder.Append("  </digests>\n");
		stringBuilder.Append("  <params>\n");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder20 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(46, 1, stringBuilder2);
		handler.AppendLiteral("    <applicationDrmType>");
		handler.AppendFormatted(options.ApplicationDrmType ?? options.ApplicationType);
		handler.AppendLiteral("</applicationDrmType>\n");
		stringBuilder20.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder21 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(28, 1, stringBuilder2);
		handler.AppendLiteral("    <contentId>");
		handler.AppendFormatted(options.ContentId);
		handler.AppendLiteral("</contentId>\n");
		stringBuilder21.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder22 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(38, 1, stringBuilder2);
		handler.AppendLiteral("    <contentVersion>");
		handler.AppendFormatted(options.ContentVersion);
		handler.AppendLiteral("</contentVersion>\n");
		stringBuilder22.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder23 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(36, 1, stringBuilder2);
		handler.AppendLiteral("    <masterVersion>");
		handler.AppendFormatted(options.MasterVersion);
		handler.AppendLiteral("</masterVersion>\n");
		stringBuilder23.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder24 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(68, 1, stringBuilder2);
		handler.AppendLiteral("    <requiredSystemSoftwareVersion>");
		handler.AppendFormatted(options.RequiredSystemSoftwareVersion);
		handler.AppendLiteral("</requiredSystemSoftwareVersion>\n");
		stringBuilder24.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder25 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(30, 1, stringBuilder2);
		handler.AppendLiteral("    <sdkVersion>");
		handler.AppendFormatted(options.SdkVersion);
		handler.AppendLiteral("</sdkVersion>\n");
		stringBuilder25.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder26 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(28, 1, stringBuilder2);
		handler.AppendLiteral("    <titleName>");
		handler.AppendFormatted(options.TitleName);
		handler.AppendLiteral("</titleName>\n");
		stringBuilder26.Append(ref handler);
		stringBuilder.Append("  </params>\n");
		stringBuilder.Append("  <container nth-of-image=\"1\">\n");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder27 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(38, 1, stringBuilder2);
		handler.AppendLiteral("    <container-size>");
		handler.AppendFormatted(Hex(containerSize));
		handler.AppendLiteral("</container-size>\n");
		stringBuilder27.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder28 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(38, 1, stringBuilder2);
		handler.AppendLiteral("    <mandatory-size>");
		handler.AppendFormatted(Hex(options.MandatorySize));
		handler.AppendLiteral("</mandatory-size>\n");
		stringBuilder28.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder29 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(32, 1, stringBuilder2);
		handler.AppendLiteral("    <body-offset>");
		handler.AppendFormatted(Hex(bodyOffset));
		handler.AppendLiteral("</body-offset>\n");
		stringBuilder29.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder30 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(28, 1, stringBuilder2);
		handler.AppendLiteral("    <body-size>");
		handler.AppendFormatted(Hex(num));
		handler.AppendLiteral("</body-size>\n");
		stringBuilder30.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder31 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(44, 1, stringBuilder2);
		handler.AppendLiteral("    <body-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.BodyDigest), "      "));
		handler.AppendLiteral("\n    </body-digest>\n");
		stringBuilder31.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder32 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(34, 1, stringBuilder2);
		handler.AppendLiteral("    <promote-size>");
		handler.AppendFormatted(Hex8(containerSize));
		handler.AppendLiteral("</promote-size>\n");
		stringBuilder32.Append(ref handler);
		stringBuilder.Append("  </container>\n");
		stringBuilder.Append("  <mount-image nth-of-image=\"1\" nested-image=\"yes\">\n");
		stringBuilder.Append("    <pfs-offset-align>0x0000000000010000</pfs-offset-align>\n");
		stringBuilder.Append("    <pfs-size-align>0x0000000000010000</pfs-size-align>\n");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder33 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(42, 1, stringBuilder2);
		handler.AppendLiteral("    <pfs-image-offset>");
		handler.AppendFormatted(Hex(options.PfsImageOffset));
		handler.AppendLiteral("</pfs-image-offset>\n");
		stringBuilder33.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder34 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(38, 1, stringBuilder2);
		handler.AppendLiteral("    <pfs-image-size>");
		handler.AppendFormatted(Hex(options.PfsImageSize));
		handler.AppendLiteral("</pfs-image-size>\n");
		stringBuilder34.Append(ref handler);
		stringBuilder.Append("    <fixed-info-size>0x00010000</fixed-info-size>\n");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder35 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(50, 1, stringBuilder2);
		handler.AppendLiteral("    <pfs-image-seed>\n      ");
		handler.AppendFormatted(value);
		handler.AppendLiteral("\n    </pfs-image-seed>\n");
		stringBuilder35.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder36 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(48, 1, stringBuilder2);
		handler.AppendLiteral("    <sblock-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.SblockDigest ?? options.GameDigest), "      "));
		handler.AppendLiteral("\n    </sblock-digest>\n");
		stringBuilder36.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder37 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(56, 1, stringBuilder2);
		handler.AppendLiteral("    <fixed-info-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.FixedInfoDigest), "      "));
		handler.AppendLiteral("\n    </fixed-info-digest>\n");
		stringBuilder37.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder38 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(46, 1, stringBuilder2);
		handler.AppendLiteral("    <mount-image-offset>");
		handler.AppendFormatted(Hex(0L));
		handler.AppendLiteral("</mount-image-offset>\n");
		stringBuilder38.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder39 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(42, 1, stringBuilder2);
		handler.AppendLiteral("    <mount-image-size>");
		handler.AppendFormatted(Hex(num3));
		handler.AppendLiteral("</mount-image-size>\n");
		stringBuilder39.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder40 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(42, 1, stringBuilder2);
		handler.AppendLiteral("    <container-offset>");
		handler.AppendFormatted(Hex(num2));
		handler.AppendLiteral("</container-offset>\n");
		stringBuilder40.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder41 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(48, 1, stringBuilder2);
		handler.AppendLiteral("    <supplemental-offset>");
		handler.AppendFormatted(Hex(options.SupplementalOffset));
		handler.AppendLiteral("</supplemental-offset>\n");
		stringBuilder41.Append(ref handler);
		stringBuilder.Append("  </mount-image>\n");
		IReadOnlyList<ProsperoPfsImageEntry> entries = options.Entries;
		if (entries != null && entries.Count > 0)
		{
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder42 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(36, 1, stringBuilder2);
			handler.AppendLiteral("  <entries nth-of-image=\"1\" num=\"");
			handler.AppendFormatted(entries.Count);
			handler.AppendLiteral("\">\n");
			stringBuilder42.Append(ref handler);
			foreach (ProsperoPfsImageEntry item in entries)
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder43 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(39, 3, stringBuilder2);
				handler.AppendLiteral("    <entry offset=\"");
				handler.AppendFormatted(Hex8(item.Offset));
				handler.AppendLiteral("\" size=\"");
				handler.AppendFormatted(Hex8(item.Size));
				handler.AppendLiteral("\" name=\"");
				handler.AppendFormatted(item.Name);
				handler.AppendLiteral("\"/>\n");
				stringBuilder43.Append(ref handler);
			}
			stringBuilder.Append("  </entries>\n");
		}
		AppendStageB(stringBuilder, options);
		stringBuilder.Append("</package-configuration>\n");
		return stringBuilder.ToString();
		static string Hex(long num4)
		{
			return "0x" + num4.ToString("x16", CultureInfo.InvariantCulture);
		}
		static string Hex8(long num4)
		{
			return "0x" + num4.ToString("x8", CultureInfo.InvariantCulture);
		}
		static string Indent(string digest, string pad)
		{
			return digest.Replace("\n", "\n" + pad);
		}
		void NoteIfPlaceholder(byte[]? array, string field)
		{
			if (array == null || array.Length == 0)
			{
				warnings?.Add("pfsimage.xml <" + field + "> emitted as all-zero placeholder (supply the digest explicitly when using the low-level XML API).");
			}
		}
	}

	private static string BuildLongName(ProsperoPfsImageXmlOptions o)
	{
		string value = o.ContentVersion.Replace(".", "", StringComparison.Ordinal);
		string value2 = (o.ContentType.StartsWith("PS5", StringComparison.OrdinalIgnoreCase) ? o.ContentType.Substring(3) : o.ContentType);
		string value3 = o.LongNameMasterValue.ToString("x16", CultureInfo.InvariantCulture);
		return $"{o.ContentId}-C{value}-M{value3}-{value2}";
	}

	private static void AppendStageB(StringBuilder sb, ProsperoPfsImageXmlOptions options)
	{
		ProsperoChunkInfoModel chunkInfo = options.ChunkInfo;
		if (chunkInfo != null)
		{
			AppendChunkInfo(sb, options.ContentId, chunkInfo);
		}
		ProsperoPfsImageTreeInfo outerPfsTree = options.OuterPfsTree;
		if (outerPfsTree != null)
		{
			AppendPfsImage(sb, outerPfsTree, options.PfsImageOffset);
		}
		ProsperoPfsImageTreeInfo nestedPfsTree = options.NestedPfsTree;
		if (nestedPfsTree != null)
		{
			AppendNestedImage(sb, nestedPfsTree);
		}
	}

	private static void AppendChunkInfo(StringBuilder sb, string contentId, ProsperoChunkInfoModel c)
	{
		string value = "0x" + c.LanguageMask.ToString("x16", CultureInfo.InvariantCulture);
		StringBuilder stringBuilder = sb;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(52, 3, stringBuilder);
		handler.AppendLiteral("  <chunkinfo size=\"");
		handler.AppendFormatted(c.PlayGoChunkDatSize);
		handler.AppendLiteral("\" nested=\"true\" sdk=\"");
		handler.AppendFormatted(c.Sdk);
		handler.AppendLiteral("\" disps=\"");
		handler.AppendFormatted(c.Disps);
		handler.AppendLiteral("\">\n");
		stringBuilder2.Append(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder3 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(28, 1, stringBuilder);
		handler.AppendLiteral("    <contentid>");
		handler.AppendFormatted(contentId);
		handler.AppendLiteral("</contentid>\n");
		stringBuilder3.Append(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder4 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(40, 1, stringBuilder);
		handler.AppendLiteral("    <languages default=\"1\">");
		handler.AppendFormatted(value);
		handler.AppendLiteral("</languages>\n");
		stringBuilder4.Append(ref handler);
		sb.Append("    <scenarios num=\"1\" default=\"0\" groups=\"0\">\n");
		sb.Append("      <scenario id=\"0\" type=\"33\" name=\"Scenario #0\">\n");
		stringBuilder = sb;
		StringBuilder stringBuilder5 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(72, 2, stringBuilder);
		handler.AppendLiteral("        <overall initials=\"1\" num=\"1\" init-size=\"");
		handler.AppendFormatted(c.TotalSize);
		handler.AppendLiteral("\" total=\"");
		handler.AppendFormatted(c.TotalSize);
		handler.AppendLiteral("\">0</overall>\n");
		stringBuilder5.Append(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder6 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(72, 2, stringBuilder);
		handler.AppendLiteral("        <default initials=\"1\" num=\"1\" init-size=\"");
		handler.AppendFormatted(c.TotalSize);
		handler.AppendLiteral("\" total=\"");
		handler.AppendFormatted(c.TotalSize);
		handler.AppendLiteral("\">0</default>\n");
		stringBuilder6.Append(ref handler);
		sb.Append("      </scenario>\n");
		sb.Append("    </scenarios>\n");
		sb.Append("    <chunks num=\"1\" default=\"0xffffffffffffffff\">\n");
		stringBuilder = sb;
		StringBuilder stringBuilder7 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(110, 3, stringBuilder);
		handler.AppendLiteral("      <chunk id=\"0\" flag=\"0x80\" locus=\"0x03\" language=\"");
		handler.AppendFormatted(value);
		handler.AppendLiteral("\" disps=\"");
		handler.AppendFormatted(c.Disps);
		handler.AppendLiteral("\" num=\"2\" size=\"");
		handler.AppendFormatted(c.TotalSize);
		handler.AppendLiteral("\" name=\"Chunk #0\">0 1</chunk>\n");
		stringBuilder7.Append(ref handler);
		sb.Append("    </chunks>\n");
		sb.Append("    <outers num=\"2\" overlapped=\"0\" language-overlapped=\"0\">\n");
		stringBuilder = sb;
		StringBuilder stringBuilder8 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(61, 2, stringBuilder);
		handler.AppendLiteral("      <outer id=\"0\" image=\"0\" offset=\"");
		handler.AppendFormatted(Hex16Blob(0L));
		handler.AppendLiteral("\" size=\"");
		handler.AppendFormatted(Hex16Blob(c.Outer0Size));
		handler.AppendLiteral("\" chunks=\"1\"/>\n");
		stringBuilder8.Append(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder9 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(61, 2, stringBuilder);
		handler.AppendLiteral("      <outer id=\"1\" image=\"0\" offset=\"");
		handler.AppendFormatted(Hex16Blob(c.Outer0Size));
		handler.AppendLiteral("\" size=\"");
		handler.AppendFormatted(Hex16Blob(c.Outer1Size));
		handler.AppendLiteral("\" chunks=\"1\"/>\n");
		stringBuilder9.Append(ref handler);
		sb.Append("    </outers>\n");
		sb.Append("  </chunkinfo>\n");
	}

	private static void AppendPfsImage(StringBuilder sb, ProsperoPfsImageTreeInfo info, long pfsImageOffset)
	{
		long value = (long)info.DinodeBlock * (long)info.BlockSize;
		StringBuilder stringBuilder = sb;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(64, 2, stringBuilder);
		handler.AppendLiteral("  <pfs-image version=\"2\" readonly=\"true\" offset=\"");
		handler.AppendFormatted(pfsImageOffset);
		handler.AppendLiteral("\" metadata=\"");
		handler.AppendFormatted(value);
		handler.AppendLiteral("\">\n");
		stringBuilder2.Append(ref handler);
		string value2 = (info.Signed ? " signed=\"true\"" : "");
		string value3 = (info.Encrypted ? " encrypted=\"true\"" : "");
		stringBuilder = sb;
		StringBuilder stringBuilder3 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(70, 3, stringBuilder);
		handler.AppendLiteral("    <sblock");
		handler.AppendFormatted(value2);
		handler.AppendFormatted(value3);
		handler.AppendLiteral(" ignore-case=\"true\" index-size=\"32\" blocks=\"");
		handler.AppendFormatted(info.DinodeBlockCount);
		handler.AppendLiteral("\" backups=\"0\">\n");
		stringBuilder3.Append(ref handler);
		AppendSuperInode(sb, info);
		stringBuilder = sb;
		StringBuilder stringBuilder4 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder);
		handler.AppendLiteral("      <seed>");
		handler.AppendFormatted(Hex16Blob(info.Seed, 16));
		handler.AppendLiteral("</seed>\n");
		stringBuilder4.Append(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder5 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(18, 1, stringBuilder);
		handler.AppendLiteral("      <icv>");
		handler.AppendFormatted(Hex16Blob(info.SuperblockIcv, 32));
		handler.AppendLiteral("</icv>\n");
		stringBuilder5.Append(ref handler);
		sb.Append("    </sblock>\n");
		AppendOuterNode(sb, info.Root, info.BlockSize, "    ");
		sb.Append("  </pfs-image>\n");
	}

	private static void AppendNestedImage(StringBuilder sb, ProsperoPfsImageTreeInfo info)
	{
		sb.Append("  <nested-image version=\"2\" readonly=\"true\" offset=\"0\">\n");
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(70, 1, sb);
		handler.AppendLiteral("    <sblock ignore-case=\"true\" index-size=\"32\" blocks=\"");
		handler.AppendFormatted(info.DinodeBlockCount);
		handler.AppendLiteral("\" backups=\"0\">\n");
		sb.Append(ref handler);
		AppendSuperInode(sb, info);
		sb.Append("    </sblock>\n");
		int afid = 0;
		AppendNestedNode(sb, info.Root, info.BlockSize, ref afid, "    ");
		sb.Append("  </nested-image>\n");
	}

	private static void AppendSuperInode(StringBuilder sb, ProsperoPfsImageTreeInfo info)
	{
		StringBuilder stringBuilder = sb;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(53, 3, stringBuilder);
		handler.AppendLiteral("      <image-size block-size=\"");
		handler.AppendFormatted(info.BlockSize);
		handler.AppendLiteral("\" num=\"");
		handler.AppendFormatted(info.ImageBlocks);
		handler.AppendLiteral("\">");
		handler.AppendFormatted(Hex16Blob(info.ImageBlocks * info.BlockSize));
		handler.AppendLiteral("</image-size>\n");
		stringBuilder2.Append(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder3 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(48, 3, stringBuilder);
		handler.AppendLiteral("      <super-inode blocks=\"");
		handler.AppendFormatted(info.DinodeBlockCount);
		handler.AppendLiteral("\" inodes=\"");
		handler.AppendFormatted(info.InodeCount);
		handler.AppendLiteral("\" root=\"");
		handler.AppendFormatted(info.RootInodeNumber);
		handler.AppendLiteral("\">\n");
		stringBuilder3.Append(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder4 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(67, 3, stringBuilder);
		handler.AppendLiteral("        <inode size=\"");
		handler.AppendFormatted(info.DinodeSize);
		handler.AppendLiteral("\" links=\"1\" mode=\"0x0000\" imode=\"");
		handler.AppendFormatted(Imode(info.DinodeFlags));
		handler.AppendLiteral("\" index=\"");
		handler.AppendFormatted(info.DinodeBlock);
		handler.AppendLiteral("\"/>\n");
		stringBuilder4.Append(ref handler);
		sb.Append("      </super-inode>\n");
	}

	private static void AppendOuterNode(StringBuilder sb, ProsperoPfsImageNode n, int blockSize, string pad)
	{
		string pad2 = pad + "  ";
		if (n.IsDirectory)
		{
			string value = ((n.Name.Length == 0) ? "root" : "dir");
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(55, 8, stringBuilder);
			handler.AppendFormatted(pad);
			handler.AppendLiteral("<");
			handler.AppendFormatted(value);
			handler.AppendLiteral(" size=\"");
			handler.AppendFormatted(n.StoredSize);
			handler.AppendLiteral("\" links=\"");
			handler.AppendFormatted(n.Nlink);
			handler.AppendLiteral("\" imode=\"");
			handler.AppendFormatted(Imode(n.Flags));
			handler.AppendLiteral("\" index=\"");
			handler.AppendFormatted(n.StartBlock);
			handler.AppendLiteral("\" inode=\"");
			handler.AppendFormatted(n.InodeNumber);
			handler.AppendLiteral("\" name=\"");
			handler.AppendFormatted(n.Name);
			handler.AppendLiteral("\">\n");
			stringBuilder2.Append(ref handler);
			foreach (ProsperoPfsImageNode child in n.Children)
			{
				AppendOuterNode(sb, child, blockSize, pad2);
			}
			stringBuilder = sb;
			StringBuilder stringBuilder3 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(4, 2, stringBuilder);
			handler.AppendFormatted(pad);
			handler.AppendLiteral("</");
			handler.AppendFormatted(value);
			handler.AppendLiteral(">\n");
			stringBuilder3.Append(ref handler);
		}
		else
		{
			string value2 = ((!n.Internal && n.Compressed) ? $" plain=\"{n.PlainSize}\" comp=\"{CompLabel(n.StoredSize, n.PlainSize, blockSize)}\"" : "");
			StringBuilder.AppendInterpolatedStringHandler handler2 = new StringBuilder.AppendInterpolatedStringHandler(51, 7, sb);
			handler2.AppendFormatted(pad);
			handler2.AppendLiteral("<file size=\"");
			handler2.AppendFormatted(n.StoredSize);
			handler2.AppendLiteral("\"");
			handler2.AppendFormatted(value2);
			handler2.AppendLiteral(" imode=\"");
			handler2.AppendFormatted(Imode(n.Flags));
			handler2.AppendLiteral("\" index=\"");
			handler2.AppendFormatted(IndexRange(n));
			handler2.AppendLiteral("\" inode=\"");
			handler2.AppendFormatted(n.InodeNumber);
			handler2.AppendLiteral("\" name=\"");
			handler2.AppendFormatted(n.Name);
			handler2.AppendLiteral("\"/>\n");
			sb.Append(ref handler2);
		}
	}

	private static void AppendNestedNode(StringBuilder sb, ProsperoPfsImageNode n, int blockSize, ref int afid, string pad)
	{
		string pad2 = pad + "  ";
		if (n.IsDirectory)
		{
			StringBuilder stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler;
			if (n.Name.Length == 0)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder2 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(51, 5, stringBuilder);
				handler.AppendFormatted(pad);
				handler.AppendLiteral("<root plain=\"");
				handler.AppendFormatted(n.PlainSize);
				handler.AppendLiteral("\" links=\"");
				handler.AppendFormatted(n.Nlink);
				handler.AppendLiteral("\" imode=\"");
				handler.AppendFormatted(Imode(n.Flags));
				handler.AppendLiteral("\" inode=\"");
				handler.AppendFormatted(n.InodeNumber);
				handler.AppendLiteral("\" name=\"\">\n");
				stringBuilder2.Append(ref handler);
				foreach (ProsperoPfsImageNode child in n.Children)
				{
					AppendNestedNode(sb, child, blockSize, ref afid, pad2);
				}
				stringBuilder = sb;
				StringBuilder stringBuilder3 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(8, 1, stringBuilder);
				handler.AppendFormatted(pad);
				handler.AppendLiteral("</root>\n");
				stringBuilder3.Append(ref handler);
				return;
			}
			stringBuilder = sb;
			StringBuilder stringBuilder4 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(58, 7, stringBuilder);
			handler.AppendFormatted(pad);
			handler.AppendLiteral("<dir plain=\"");
			handler.AppendFormatted(n.PlainSize);
			handler.AppendLiteral("\" links=\"");
			handler.AppendFormatted(n.Nlink);
			handler.AppendLiteral("\" mode=\"");
			handler.AppendFormatted(Mode4(n.Mode));
			handler.AppendLiteral("\" imode=\"");
			handler.AppendFormatted(Imode(n.Flags));
			handler.AppendLiteral("\" inode=\"");
			handler.AppendFormatted(n.InodeNumber);
			handler.AppendLiteral("\" name=\"");
			handler.AppendFormatted(n.Name);
			handler.AppendLiteral("\">\n");
			stringBuilder4.Append(ref handler);
			foreach (ProsperoPfsImageNode child2 in n.Children)
			{
				AppendNestedNode(sb, child2, blockSize, ref afid, pad2);
			}
			stringBuilder = sb;
			StringBuilder stringBuilder5 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder);
			handler.AppendFormatted(pad);
			handler.AppendLiteral("</dir>\n");
			stringBuilder5.Append(ref handler);
		}
		else if (n.Internal)
		{
			StringBuilder.AppendInterpolatedStringHandler handler2 = new StringBuilder.AppendInterpolatedStringHandler(43, 5, sb);
			handler2.AppendFormatted(pad);
			handler2.AppendLiteral("<file plain=\"");
			handler2.AppendFormatted(n.PlainSize);
			handler2.AppendLiteral("\" imode=\"");
			handler2.AppendFormatted(Imode(n.Flags));
			handler2.AppendLiteral("\" inode=\"");
			handler2.AppendFormatted(n.InodeNumber);
			handler2.AppendLiteral("\" name=\"");
			handler2.AppendFormatted(n.Name);
			handler2.AppendLiteral("\"/>\n");
			sb.Append(ref handler2);
		}
		else
		{
			string value = (n.Compressed ? (" comp=\"" + CompLabel(n.StoredSize, n.PlainSize, blockSize) + "\"") : "");
			long value2 = (long)n.StartBlock * (long)blockSize;
			StringBuilder.AppendInterpolatedStringHandler handler3 = new StringBuilder.AppendInterpolatedStringHandler(87, 10, sb);
			handler3.AppendFormatted(pad);
			handler3.AppendLiteral("<file size=\"");
			handler3.AppendFormatted(n.StoredSize);
			handler3.AppendLiteral("\" plain=\"");
			handler3.AppendFormatted(n.PlainSize);
			handler3.AppendLiteral("\"");
			handler3.AppendFormatted(value);
			handler3.AppendLiteral(" offset=\"");
			handler3.AppendFormatted(value2);
			handler3.AppendLiteral("\" mode=\"");
			handler3.AppendFormatted(Mode4(n.Mode));
			handler3.AppendLiteral("\" imode=\"");
			handler3.AppendFormatted(Imode(n.Flags));
			handler3.AppendLiteral("\" inode=\"");
			handler3.AppendFormatted(n.InodeNumber);
			handler3.AppendLiteral("\" afid=\"");
			handler3.AppendFormatted(afid++);
			handler3.AppendLiteral("\" chunk=\"0\" name=\"");
			handler3.AppendFormatted(n.Name);
			handler3.AppendLiteral("\"/>\n");
			sb.Append(ref handler3);
		}
	}

	private static string IndexRange(ProsperoPfsImageNode n)
	{
		if (n.Blocks <= 1)
		{
			return n.StartBlock.ToString(CultureInfo.InvariantCulture);
		}
		return $"{n.StartBlock}-{n.StartBlock + (int)n.Blocks - 1}";
	}

	private static string CompLabel(long stored, long plain, int blockSize)
	{
		long value = ((plain > 0) ? ((long)Math.Round((double)stored * 100.0 / (double)plain)) : 0);
		long value2 = ((blockSize > 0) ? ((stored + blockSize - 1) / blockSize) : 0);
		long value3 = ((blockSize > 0) ? ((plain + blockSize - 1) / blockSize) : 0);
		return $"{value}% ({value2}/{value3})";
	}

	private static string Imode(uint flags)
	{
		return "0x" + flags.ToString("x8", CultureInfo.InvariantCulture);
	}

	private static string Mode4(ushort mode)
	{
		return "0x" + mode.ToString("x4", CultureInfo.InvariantCulture);
	}

	private static string Hex16Blob(long v)
	{
		return "0x" + v.ToString("x16", CultureInfo.InvariantCulture);
	}

	private static string Hex16Blob(byte[]? data, int len)
	{
		StringBuilder stringBuilder = new StringBuilder(2 + len * 2);
		stringBuilder.Append("0x");
		for (int i = 0; i < len; i++)
		{
			stringBuilder.Append(((byte)((data != null && i < data.Length) ? data[i] : 0)).ToString("X2", CultureInfo.InvariantCulture));
		}
		return stringBuilder.ToString();
	}
}
