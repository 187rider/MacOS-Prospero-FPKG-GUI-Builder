namespace LibProsperoPkg;

/// <summary>The kind of PS5 package to produce.</summary>
public enum ProsperoPackageMode
{
	/// <summary>A generic PS5 application/game (already-prepared <c>sce_sys</c> + eboot folder).</summary>
	Application,
	/// <summary>A PS5 homebrew application.</summary>
	Homebrew,
	/// <summary>Additional content (DLC) that ships data.</summary>
	AdditionalContentData,
	/// <summary>Additional content (DLC) entitlement only, no data.</summary>
	AdditionalContentNoData
}
