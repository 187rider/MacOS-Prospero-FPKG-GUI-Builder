using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace LibProsperoPkg.GP5;

/// <summary>
/// In-memory representation of a <c>*.gp5</c> project. Use <see cref="M:LibProsperoPkg.GP5.Gp5Project.Create(LibProsperoPkg.GP5.Gp5VolumeType,System.String)" /> to build
/// a new project, <see cref="M:LibProsperoPkg.GP5.Gp5Project.WriteTo(LibProsperoPkg.GP5.Gp5Project,System.String)" /> to serialize it and
/// <see cref="M:LibProsperoPkg.GP5.Gp5Project.ReadFrom(System.String)" /> to load one back.
/// </summary>
[XmlRoot(ElementName = "psproject")]
public sealed class Gp5Project
{
	/// <summary>The XML namespaces written for a GP5 project (none, matching the reference tool).</summary>
	private static readonly XmlSerializerNamespaces EmptyNamespaces = new XmlSerializerNamespaces(new XmlQualifiedName[1] { XmlQualifiedName.Empty });

	[XmlAttribute("fmt")]
	public string Format { get; set; } = "gp5";

	[XmlAttribute("version")]
	public int Version { get; set; } = 1000;

	/// <summary>
	/// Whether the optional <c>version</c> attribute was present. Newly-created projects omit it,
	/// matching current Publishing Tools; older SDK samples that contain <c>version="1000"</c>
	/// retain it when read and written again.
	/// </summary>
	[XmlIgnore]
	public bool VersionSpecified { get; set; }

	[XmlElement(ElementName = "volume", Order = 1)]
	public Gp5Volume Volume { get; set; } = new Gp5Volume();

	[XmlElement(ElementName = "global_exclude", Order = 2)]
	public string GlobalExclude { get; set; } = "";

	[XmlElement(ElementName = "rootdir", Order = 3)]
	public Gp5RootDir RootDir { get; set; } = new Gp5RootDir();

	/// <summary>
	/// The explicit top-level <c>&lt;files&gt;</c> listing (flat layout). When this is non-empty the
	/// project is written in the <see cref="F:LibProsperoPkg.GP5.Gp5Layout.Flat" /> style and the <c>&lt;rootdir&gt;</c> /
	/// <c>&lt;global_exclude&gt;</c> elements are omitted.
	/// </summary>
	[XmlArray(ElementName = "files", Order = 4)]
	[XmlArrayItem(ElementName = "file", Type = typeof(Gp5File))]
	public List<Gp5File> Files { get; set; } = new List<Gp5File>();

	/// <summary>Preserves an explicit empty <c>&lt;files/&gt;</c>, used by blank and AL projects.</summary>
	[XmlIgnore]
	public bool FilesSpecified { get; set; }

	/// <summary>
	/// The optional explicit top-level <c>&lt;folders&gt;</c> listing (flat layout) that maps whole
	/// source directories to package destination paths, parallel to <see cref="P:LibProsperoPkg.GP5.Gp5Project.Files" />.
	/// </summary>
	[XmlArray(ElementName = "folders", Order = 5)]
	[XmlArrayItem(ElementName = "dir", Type = typeof(Gp5Dir))]
	public List<Gp5Dir> Folders { get; set; } = new List<Gp5Dir>();

	[XmlIgnore]
	public bool FoldersSpecified { get; set; }

	/// <summary>
	/// The layout this project is in: <see cref="F:LibProsperoPkg.GP5.Gp5Layout.Flat" /> when it carries an explicit
	/// <see cref="P:LibProsperoPkg.GP5.Gp5Project.Files" /> / <see cref="P:LibProsperoPkg.GP5.Gp5Project.Folders" /> listing, otherwise <see cref="F:LibProsperoPkg.GP5.Gp5Layout.Normal" />
	/// (a recursively-walked <see cref="P:LibProsperoPkg.GP5.Gp5Project.RootDir" />).
	/// </summary>
	[XmlIgnore]
	public Gp5Layout Layout
	{
		get
		{
			if (!FilesSpecified && !FoldersSpecified && Files.Count <= 0 && Folders.Count <= 0)
			{
				return Gp5Layout.Normal;
			}
			return Gp5Layout.Flat;
		}
	}

	public bool ShouldSerializeGlobalExclude()
	{
		return Layout == Gp5Layout.Normal;
	}

	public bool ShouldSerializeRootDir()
	{
		return Layout == Gp5Layout.Normal;
	}

	public bool ShouldSerializeFiles()
	{
		if (!FilesSpecified)
		{
			return Files.Count > 0;
		}
		return true;
	}

	public bool ShouldSerializeFolders()
	{
		if (!FoldersSpecified)
		{
			return Folders.Count > 0;
		}
		return true;
	}

	/// <summary>
	/// Creates a new, valid GP5 project with sensible Prospero defaults for the
	/// supplied volume type.
	/// </summary>
	public static Gp5Project Create(Gp5VolumeType type, string passcode = "00000000000000000000000000000000")
	{
		return new Gp5Project
		{
			FilesSpecified = true,
			Volume = new Gp5Volume
			{
				VolumeTypeName = type.ToString(),
				Package = new Gp5Package
				{
					Passcode = passcode
				}
			}
		};
	}

	/// <summary>Serializes the project to the given stream.</summary>
	public static void WriteTo(Gp5Project project, Stream stream)
	{
		ArgumentNullException.ThrowIfNull(project, "project");
		ArgumentNullException.ThrowIfNull(stream, "stream");
		XmlWriterSettings settings = new XmlWriterSettings
		{
			Indent = true,
			IndentChars = "  ",
			Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			OmitXmlDeclaration = false
		};
		using XmlWriter xmlWriter = XmlWriter.Create(stream, settings);
		new XmlSerializer(typeof(Gp5Project)).Serialize(xmlWriter, project, EmptyNamespaces);
	}

	/// <summary>Serializes the project to the given file path.</summary>
	public static void WriteTo(Gp5Project project, string path)
	{
		using FileStream stream = File.Create(path);
		WriteTo(project, stream);
	}

	/// <summary>Reads a GP5 project from the given stream.</summary>
	public static Gp5Project ReadFrom(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		return (Gp5Project)new XmlSerializer(typeof(Gp5Project)).Deserialize(stream);
	}

	/// <summary>Reads a GP5 project from the given file path.</summary>
	public static Gp5Project ReadFrom(string path)
	{
		using FileStream stream = File.OpenRead(path);
		return ReadFrom(stream);
	}
}
