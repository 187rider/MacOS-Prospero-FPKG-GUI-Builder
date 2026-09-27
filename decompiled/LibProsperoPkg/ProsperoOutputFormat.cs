namespace LibProsperoPkg;

/// <summary>The container format the builder emits.</summary>
public enum ProsperoOutputFormat
{
	/// <summary>
	/// A metadata container (<c>\x7FCNT</c>) only. This holds nothing but the package metadata and
	/// is <b>not</b> a full, installable package — it cannot be installed on a console. Use it for
	/// inspection / tooling; produce <see cref="F:LibProsperoPkg.ProsperoOutputFormat.DebugImage" /> for an installable package.
	/// </summary>
	MetadataContainer,
	/// <summary>
	/// A finalized <i>debug</i> image (<c>\x7FFIH</c>, signed byte 0x00) — a full package, and the only
	/// form installable on a PS5 with debug mode enabled. The structure and embedded CNT are exact;
	/// the finalization digest table is debug-key gated and filled best-effort (see
	/// <see cref="T:LibProsperoPkg.PKG.ProsperoFihBuilder" />). This is the default output.
	/// </summary>
	DebugImage,
	/// <summary>
	/// A standard finalized Retail image (<c>\x7FFIH</c>, signed byte 0x80). This mode requires a
	/// trusted <see cref="T:LibProsperoPkg.PKG.IProsperoRetailFinalizationProvider" /> that returns the protected
	/// 0x300-byte FIH material. The builder refuses to emit a structural-only 0x80 image.
	/// </summary>
	RetailImage
}
