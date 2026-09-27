using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace LibProsperoPkg.PFS.Compression.Oodle;

/// <summary>
/// Managed Kraken (newLZ) encoder. Compresses a PFS block into one or two headerless newLZ "excess
/// mode" chunks that round-trip through the PS5 block decompressor.
/// <para>
/// <b>Length escapes.</b> A literal run longer than 257 bytes (packed value <c>litLen-3 &gt;= 255</c>) and
/// a match longer than <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.MaxMatch" /> (packed value <c>matchLen-17 &gt;= 255</c>) are carried by a
/// 0xFF packed_litlen marker plus a u32 escape value. Every escape in a chunk is collected in packed_litlen
/// order and written into a trailing excess sub-stream, split even index -&gt; forward writer / odd index
/// -&gt; backward writer and laid out <c>forward ++ reverse(backward)</c>, mirroring the paired readers the
/// decoder uses. The post-seed control byte is <c>0x80 | low6(excessByteCount)</c> with a continuation byte
/// when the count exceeds 0x1F. Over-long matches are split into &lt;= <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.MaxMatch" /> pieces so in
/// practice only literal runs escape; a chunk needing more than <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.MaxEscapes" /> escapes falls back
/// to the stored path.
/// </para>
/// <para>
/// <b>No match may start in the last <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.NoMatchZone" /> (16) bytes of a chunk.</b> The newLZ release
/// parse loop enforces <c>match_zone_end - to_ptr &gt;= lrl</c> where <c>match_zone_end = chunk_end - 16</c>:
/// the literal-run end (= match start) must be &lt;= chunk_end - 16. A match that starts later drifts the
/// decode pointer and the block is rejected with error -1007. The parser therefore stops emitting matches
/// past <c>chunkEnd - NoMatchZone</c> and flushes the remainder as the trailing literal run; a store-raw
/// safety net rejects any chunk that would still violate it.
/// </para>
/// Returns null when compression is not worthwhile (caller stores the block).
/// </summary>
internal static class OodleKrakenEncoder
{
	private readonly struct Command(int litStart, int litLen, int distance, int matchLen, int offsIndex)
	{
		public readonly int LitStart = litStart;

		public readonly int LitLen = litLen;

		public readonly int Distance = distance;

		public readonly int MatchLen = matchLen;

		public readonly int OffsIndex = offsIndex;
	}

	private readonly record struct EncodedChunk(byte[] Payload, int LiteralMode);

	/// <summary>
	/// A candidate match. <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Cand.Idx" /> 0/1/2 = repeat-offset reuse (no offset transmitted),
	/// 3 = new offset, -1 = none. <see cref="P:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Cand.Value" /> is the match value model
	/// (<c>algorithm</c>):<c>len*4 - (isRep ? 0 : bitlen(dist)+2)</c>. A none candidate scores a
	/// large negative value so it never wins a comparison.
	/// </summary>
	private readonly struct Cand(int len, int dist, int idx)
	{
		public readonly int Len = len;

		public readonly int Dist = dist;

		public readonly int Idx = idx;

		public bool Valid => Idx >= 0;

		public int Value
		{
			get
			{
				if (Idx >= 0)
				{
					return Len * 4 - ((Idx >= 3) ? (BitLen((uint)Dist) + 2) : 0);
				}
				return -1073741824;
			}
		}
	}

	/// <summary>A forward-DP arrival: the cheapest way to reach a buffer position (the 0x28-byte record).</summary>
	private struct Arrival
	{
		public int Cost;

		public int R0;

		public int R1;

		public int R2;

		public int Ml;

		public int Lrl;

		public int Src;

		public int Idx;

		public int Dist;

		public int ContLrl;

		public int ContMl;
	}

	/// <summary>
	/// The level-7 match finder is a <b>flat 2-row, 16-way check-bit cache table</b> (NOT a hash chain):
	/// <list type="bullet">
	/// <item><b>One flat table</b> <c>uint[1&lt;&lt;N]</c>, <b>zero-initialised</b> (memset 0), shared by both rows;
	/// N = <c>clamp(ceil_log2(len), 18, 24)</c> (so N=18 for our vectors). Each entry packs
	/// <c>{position: low 26 bits (0x03FFFFFF), check: top 6 bits (0xFC000000)}</c>; window = 2^26.</item>
	/// <item><b>Row A (4-byte context):</b> <c>hash1 = rotl32(low32(read32(ptr)·0xB7A56463), N)</c>; index =
	/// <c>hash1 &amp; ((1&lt;&lt;N)−16)</c> (bits [4..N), 16-aligned); the stored/compared <c>check</c> = the top
	/// 6 bits of hash1. <b>Row B (8-byte context):</b> index =
	/// <c>((read64(ptr)·0xCF1BBCDCB7A56463) &gt;&gt; (64−N)) &amp; ~0xF</c> (16-aligned). Both select a 16-entry
	/// window <c>table[idx.. idx+16)</c>; the rows are decorrelated.</item>
	/// <item><b>Insert</b> FIFO-shifts both rows: <c>memmove(ways+1, ways, 15·4)</c> then
	/// <c>ways[0] = (pos &amp; 0x3FFFFFF) | (check &amp; 0xFC000000)</c> — newest at slot 0.</item>
	/// <item><b>Query</b> scans row A then row B (newest-first). Per way: reject unless
	/// <c>(entry &amp; 0xFC000000) == check</c>; <c>dist = ((pos−1−epos) &amp; 0x3FFFFFF) + 1</c>; require
	/// <c>dist &lt;= pos</c> (in-window); 4-byte verify; full length; emit <c>(len,dist)</c> only when
	/// <c>len &gt; prevml</c> (the asserted <c>"len &gt; prevml"</c>, ctmf.cpp:0xe7/0x11d). <c>prevml</c>
	/// is <b>reset to 0 at the start of each row</b> (match finder), so each row emits its
	/// own strictly length-increasing subsequence; the two rows' emissions are then sort+dedup-by-length, keep ≤4.</item>
	/// </list>
	/// Empty slots (entry 0) are naturally rejected by the check compare + 4-byte verify (a position-0 spurious
	/// hit only survives when the bytes truly match — the reference-faithful, benign). This exact bounded retention is what
	/// the depth sweep proved no hash-chain depth reproduces (shallow under-supplies cmd-34's far copy, deep
	/// over-supplies cmd-35). the separate windowed long-range matcher emits only matches ≥256 B and is
	/// excluded here (it cannot supply the 55-byte far copy).
	/// </summary>
	private sealed class Ctmf
	{
		private const uint Mul4 = 3081069667u;

		private const ulong Mul8 = 14923729446382167139uL;

		private const int Ways = 16;

		private const uint PosMask = 67108863u;

		private const uint CheckMask = 4227858432u;

		private readonly int _bits;

		private readonly int _ways;

		private readonly int _maskA;

		private readonly int _shiftB;

		private readonly uint[] _table;

		public Ctmf(int bits, int dataLength)
		{
			_bits = bits & 0x1F;
			if (_bits < 5)
			{
				_bits = 5;
			}
			_ways = ((CtmfWaysOverride > 0) ? CtmfWaysOverride : 16);
			_maskA = (1 << _bits) - 16;
			_shiftB = 64 - _bits;
			_table = new uint[(1 << _bits) + 64];
		}

		private static uint Read32(ReadOnlySpan<byte> d, int p)
		{
			uint num = 0u;
			int length = d.Length;
			for (int i = 0; i < 4; i++)
			{
				int num2 = p + i;
				if ((uint)num2 < (uint)length)
				{
					num |= (uint)(d[num2] << 8 * i);
				}
			}
			return num;
		}

		private static ulong Read64(ReadOnlySpan<byte> d, int p)
		{
			ulong num = 0uL;
			int length = d.Length;
			for (int i = 0; i < 8; i++)
			{
				int num2 = p + i;
				if ((uint)num2 < (uint)length)
				{
					num |= (ulong)d[num2] << 8 * i;
				}
			}
			return num;
		}

		private static uint Rotl32(uint v, int r)
		{
			r &= 0x1F;
			if (r != 0)
			{
				return (v << r) | (v >> 32 - r);
			}
			return v;
		}

		private uint Hash1(ReadOnlySpan<byte> d, int p)
		{
			return Rotl32(Read32(d, p) * 3081069667u, _bits);
		}

		private int RowA(uint h1)
		{
			return (int)h1 & _maskA;
		}

		private int RowB(ReadOnlySpan<byte> d, int p)
		{
			return (int)((long)Read64(d, p) * -3523014627327384477L >>> _shiftB) & -16;
		}

		/// <summary>Inserts position <paramref name="p" /> into both hash rows (16-way FIFO shift, newest at slot 0).</summary>
		public void Insert(ReadOnlySpan<byte> d, int p)
		{
			uint num = Hash1(d, p);
			uint entry = (uint)(p & 0x3FFFFFF) | (num & 0xFC000000u);
			InsertRow(RowA(num), entry);
			InsertRow(RowB(d, p), entry);
		}

		private void InsertRow(int row, uint entry)
		{
			uint[] table = _table;
			for (int num = 15; num > 0; num--)
			{
				table[row + num] = table[row + num - 1];
			}
			table[row] = entry;
		}

		/// <summary>
		/// Scans row A then row B (newest-first), emitting strictly-length-improving (len,dist) pairs into
		/// <paramref name="outLen" />/<paramref name="outDist" /> with a shared monotonic prevml (the 
		/// across both rows). Returns the pair count (ascending, distinct lengths == the post sort+dedup set).
		/// </summary>
		public int Query(ReadOnlySpan<byte> d, int pos, int matchLimit, Span<int> outLen, Span<int> outDist)
		{
			int n = 0;
			uint num = Hash1(d, pos);
			uint check = num & 0xFC000000u;
			if (!CtmfRowAOff)
			{
				int prevml = 0;
				n = ScanRow(d, pos, matchLimit, RowA(num), check, ref prevml, outLen, outDist, n);
			}
			int prevml2 = 0;
			return ScanRow(d, pos, matchLimit, RowB(d, pos), check, ref prevml2, outLen, outDist, n);
		}

		private int ScanRow(ReadOnlySpan<byte> d, int pos, int matchLimit, int row, uint check, ref int prevml, Span<int> outLen, Span<int> outDist, int n)
		{
			uint[] table = _table;
			for (int i = 0; i < 16; i++)
			{
				uint num = table[row + i];
				if ((num & 0xFC000000u) != check)
				{
					continue;
				}
				int num2 = (int)(num & 0x3FFFFFF);
				int num3 = ((pos - 1 - num2) & 0x3FFFFFF) + 1;
				if (num3 < 8 || num3 > pos)
				{
					continue;
				}
				int num4 = pos - num3;
				if (Read32(d, num4) != Read32(d, pos))
				{
					continue;
				}
				int num5 = MatchLength(d, num4, pos, matchLimit);
				if (num5 > prevml)
				{
					if (n < outLen.Length)
					{
						outLen[n] = num5;
						outDist[n] = num3;
						n++;
					}
					prevml = num5;
				}
			}
			return n;
		}
	}

	private const int ChunkMax = 131072;

	private const int MinChunk = 64;

	private const int MinMatch = 4;

	private const int MinRepMatch = 2;

	private const int MinDistance = 8;

	private const int LiteralTail = 8;

	private const int NoMatchZone = 16;

	private const int MaxMatch = 271;

	private const int MaxArrayLength = 262143;

	private const int MaxFirstChunkComp = 131071;

	private const int MaxChainWalk = 128;

	[ThreadStatic]
	private static int ActiveCompressionLevel;

	[ThreadStatic]
	private static bool HasActiveCompressionLevel;

	private const int HashBits = 17;

	private const int HashSize = 131072;

	private const byte CtrlExcessMode = 128;

	private const int MaxEscapes = 512;

	private static readonly int[] NormalMatchOffsetThreshold = new int[6] { 0, 0, 0, 16384, 131072, 1048576 };

	/// <summary>
	/// When true, <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.EncodeChunk(System.ReadOnlySpan{System.Byte},System.Int32[],System.Int32[],System.Int32,System.Int32,System.Boolean,System.Boolean,System.Boolean,System.Boolean)" /> parses with the validated reference Optimal3 forward cost DP
	/// (<see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.ParseOptimal(System.ReadOnlySpan{System.Byte},System.Int32[],System.Int32[],System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Boolean,System.Boolean)" />) instead of the value-model greedy <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Parse(System.ReadOnlySpan{System.Byte},System.Int32[],System.Int32[],System.Int32,System.Int32,System.Int32,System.Int32)" />. Default off:
	/// the greedy parse is the validated production default until the DP is byte-identical to the reference.
	/// </summary>
	internal static bool UseOptimalParse = false;

	/// <summary>
	/// Production switch (default ON) for the Optimal3 multi-candidate parse on single-chunk
	/// blocks. A single-chunk block has no cross-chunk backward dependency, so the reference's
	/// best-of-{greedy mml=4/3/8, seeded forward-DP} selection (by real entropy-coded emit size)
	/// reproduces the codec byte-for-byte where a greedy candidate wins, and never enlarges the
	/// block (the plain greedy is always one candidate). Multi-chunk blocks are unaffected (their seedless
	/// second chunk needs the shared match chain). Set false to force the plain value-model greedy on all
	/// blocks (useful for deterministic debugging or maximum throughput).
	/// </summary>
	internal static bool ProductionOptimalSingleChunk = true;

	/// <summary>
	/// Upper bound (in bytes) on a single-chunk block that <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.ProductionOptimalSingleChunk" /> will
	/// parse with the Optimal3 DP. The matcher is a bounded suffix-trie; this managed
	/// encoder's forward-DP is O(n^2) in the block length (≈0.2 s at 4 KiB, seconds by 8–16 KiB), so optimal
	/// parsing is restricted to genuinely small standalone blocks — the small config/text system files the
	/// reference itself compresses as per-file standalone blocks — where it is both fast and byte-faithful.
	/// Larger single-chunk blocks fall back to the linear greedy parse. Raising this trades build time for
	/// fidelity on medium blocks and is only worthwhile with a bounded suffix-trie matcher in place.
	/// </summary>
	internal static int ProductionOptimalMaxBlock = 4096;

	/// <summary>
	/// When true (and <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.UseOptimalParse" /> is set), the forward DP's candidate finder is the reference
	/// CacheTableMatchFinder (CTMF) instead of an unbounded hash chain. validated against
	/// the implementation <c>cache-table match finder</c>: a
	/// <b>two-row, sixteen-way</b> check-bit cache. Each entry is a u32 {position:26, check:6}; a query scans
	/// the 32 most-recent same-context positions (16 ways × 2 hash rows) and emits strictly-length-improving
	/// (len,dist) pairs (the <c>len &gt; prevml</c> selection in match finder). The bounded cache evicts the
	/// cheap periodic near-copies the unbounded chain surfaces, which is what forces the farther-offset
	/// choices. The far periodic copies the 16-way recency eviction drops (text cmd-20 dist-551, cmd-34
	/// dist-950) come from the windowed long-range matcher (see <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.UseLrmSupplement" />), unioned in.
	/// </summary>
	internal static bool UseCtmfFinder = true;

	/// <summary>
	/// Diagnostic: when CTMF is active, ALSO walk the unbounded hash chain and union both candidate sets
	/// (the <c>find_all_matches</c> returns up to 4 pairs unioned from the CTMF and a long-range matcher).
	/// Lets the DP see far periodic copies the 4-way cache evicted (e.g. the text-vector cmd-34 dist-950 match).
	/// </summary>
	/// <summary>Diagnostic (default 0 = 16):override the CTMF bucket depth (ways per bucket, power of two).</summary>
	internal static int CtmfWaysOverride = 0;

	internal static bool UseChainUnion = false;

	/// <summary>
	/// Diagnostic (LRM-equivalent): when the CTMF is active, ALSO walk the deep hash chain but contribute ONLY
	/// matches STRICTLY LONGER than the CTMF's best at this position. This mirrors the windowed long-range
	/// matcher (windowed long-range matcher, algorithm), which surfaces the FAR periodic copy the
	/// CTMF's 16-way recency eviction drops (text-vector cmd-20 dist-551, cmd-34 dist-950) — without re-introducing
	/// the equal-length NEAR copies a naive <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.UseChainUnion" /> surfaces (which re-breaks cmd-20). The
	/// strictly-longer gate is the faithful "len &gt; prevml" pair-selection semantics observed in match finder.
	/// </summary>
	internal static bool UseLrmSupplement = false;

	/// <summary>
	/// Reproduces the OUTPUT of the level-7 (Optimal3) match finder
	/// <c>suffix-trie match finder</c> (the implementation
	/// algorithm; ctor algorithm installs <c>suffix-trie vtable</c>,
	/// source <c>suffixtrie2.inl</c>; created by <c>suffix-trie match-finder creation</c> algorithm,
	/// selected for levels ≥6 by <c>level-based matcher selection</c> algorithm). The optimal parser asserts
	/// <c>find_all_matches_num_pairs == 4</c>, i.e. up to 4 (len,offset) pairs
	/// per position.
	/// <para>★ ROOT-CAUSE CORRECTION: levels 0-4 (greedy/fast) use the 2-row 16-way
	/// <c>CacheTableMatchFinder</c> (the <see cref="T:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Ctmf" /> implementation); levels 5/6+ (Optimal1/2/3…) use this
	/// <b>full-history suffix trie</b>. A suffix trie keeps the ENTIRE window (no 16-way recency eviction),
	/// so it retains the far periodic copy the cache drops (text cmd-34 dist-950 @ pos 6, ~17 periods back).
	/// That is exactly why the faithful 16-way cache could not surface dist-950 while the emits it.</para>
	/// <para>A suffix trie is only an acceleration structure; its <c>find_all_matches</c> output is the
	/// per-position <b>Pareto frontier</b> (closest offset for each achievable length), capped to the 4
	/// longest, ml-descending. That output is reproducible by an exhaustive full-depth walk of the 4-byte
	/// hash chain: every length≥4 match source shares the position's first 4 bytes, so it is in the bucket;
	/// <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.MatchLength(System.ReadOnlySpan{System.Byte},System.Int32,System.Int32,System.Int32)" /> verifies the bytes. When set (with <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.UseOptimalParse" />), the
	/// finder supplies this exhaustive Pareto set instead of the cache query.</para>
	/// <para>★★★ EMPIRICALLY DECISIVE (diagnostic comparison fixpoint reference, analysis-grounded): switching the finder from
	/// the 16-way cache to this full-history Pareto walk advanced the text-vector parse divergence from
	/// <b>cmd 34 → cmd 124</b> (90 commands) — the single largest convergence jump of the whole effort, and
	/// proof the suffix-trie-full-history model is the correct level-7 finder (the prior "check-bit cache
	/// retention" / "LRM union" conclusions were artifacts of modeling the wrong, levels-0-4 finder). LRM
	/// stays delta-0 (DP invariant holds). Default ON so <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.UseOptimalParse" /> uses the validated-
	/// correct finder; it stays gated behind <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.UseOptimalParse" /> (default off) so production is
	/// unaffected.</para>
	/// <para>★ THE cmd-124 RESIDUAL is NOT this finder and NOT the sublen-fill window. The sublen-fill gate
	/// <c>algorithm(cfg,len) = (uint)(len-cfg.lo) &lt; cfg.range</c> with level-7 cfg <c>rep={3,125}</c>,
	/// <c>new={mml+1,123}</c> reduces to <c>len &lt; (1&lt;&lt;min(level,8)) = 128</c> (algorithm lines
	/// 376-379) — every text match (len ≤ ~57) fills sublens, exactly like this implementation's FILL-ALL, so there is
	/// no over-supply there. The DP min-match is <c>mml = max(4, options[4]) = 4</c>; the reference
	/// length-3 NEW matches (text cmd-125 <c>mat=3 dist=237</c>) come ONLY from the <b>mml=3 greedy
	/// pre-pass</b> not the DP. The residual is the outer selection:
	/// it runs greedy pre-passes at mml=3, mml=4 and mml=8 (<c>greedy optimal pre-pass</c>
	/// algorithm), seeds the histogram from the cheapest, and keeps the min-cost parse across
	/// {greedy-mml-3/4/8, DP} under a FLOAT size estimate. That float-cost
	/// multi-greedy-mml selection is the documented reference-less cost-fixpoint wall (same class as
	/// cmd-23/cmd-35), now pushed from cmd-34 to cmd-124. Reproducing it byte-exact needs the short-match
	/// (3-byte) greedy finder + the float outer-cost order — not yet validated; not speculated.</para>
	/// </summary>
	internal static bool UseSuffixTrieFinder = true;

	/// <summary>Diagnostic (default off):query only CTMF row B (the validated 8-byte hash2), ignoring row A.</summary>
	internal static bool CtmfRowAOff = false;

	/// <summary>Diagnostic (default 0 = auto):override the CTMF table bits (tableBits) used by the finder.</summary>
	internal static int CtmfBitsOverride = 0;

	/// <summary>
	/// Greedy minimum-match override (0 = use <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.MinMatch" /> = 4). the level-7 optimal encoder
	/// runs <c>greedy optimal pre-pass</c> as up to three pre-passes with
	/// mml ∈ {4, 3, 8} (mml=4 always, mml=3 when level&gt;6 &amp;&amp; opt[4]&lt;4,
	/// mml=8 when opt[4]&lt;8) and keeps each whose float emit-cost is lower. The mml=3 pre-pass is the ONLY
	/// source of the length-3 NEW matches (the DP min-match is fixed at 4). Set per-thread by
	/// <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.DiagGreedyParse(System.Byte[],System.Int32,System.Int32,System.Int32[]@,System.Int32[]@,System.Int32[]@,System.Int32[]@,System.Int32[]@)" /> so a single parameterised greedy can model each pre-pass.
	/// </summary>
	[ThreadStatic]
	internal static int GreedyMmlOverride;

	/// <summary>
	/// Greedy hash-chain width override (0 = the default 4-byte hash). For the mml=3 pre-pass the finder must
	/// surface length-3 new matches, which a 4-byte hash chain cannot (its members share 4 bytes). Set to 3
	/// to switch <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Hash(System.ReadOnlySpan{System.Byte},System.Int32)" /> to a 3-byte hash so positions sharing only 3 bytes land in the same
	/// bucket; <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.MatchLength(System.ReadOnlySpan{System.Byte},System.Int32,System.Int32,System.Int32)" /> still verifies the true match length. Set per-thread by
	/// <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.DiagGreedyParse(System.Byte[],System.Int32,System.Int32,System.Int32[]@,System.Int32[]@,System.Int32[]@,System.Int32[]@,System.Int32[]@)" />.
	/// </summary>
	[ThreadStatic]
	internal static int GreedyHashBytesOverride;

	/// <summary>
	/// When true (and <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.UseOptimalParse" /> is set), each match relax also runs the rep0-continuation
	/// pre-relax: after placing a match ending at E with new front-offset d, it values the
	/// immediate rep0 continuation [3-byte transition + forward rep0 match at d] and relaxes the arrival at its
	/// end, bundling the continuation into field [6] of that arrival. This is a "poor man's multi-arrival" that
	/// lets a locally-non-minimal match still propagate its free rep cascade (the cmd-21 defer-to-lock-rep case).
	/// validated against the pre-relax helper and its two call sites.
	/// </summary>
	internal static bool UseFaef0 = true;

	/// <summary>
	/// When set (with the greedy <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Parse(System.ReadOnlySpan{System.Byte},System.Int32[],System.Int32[],System.Int32,System.Int32,System.Int32,System.Int32)" />), the per-position selector is the byte-exact implementation of
	/// the <c>match heuristic</c> — <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.FindMatchExact(System.ReadOnlySpan{System.Byte},System.Int32,System.Int32[],System.Int32[],System.Int32,System.Int32,System.Int32,System.Int32,System.Int32)" /> — instead of the
	/// value-model <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.FindMatch(System.ReadOnlySpan{System.Byte},System.Int32,System.Int32[],System.Int32[],System.Int32,System.Int32,System.Int32,System.Int32)" />. It reproduces the greedy decision chain exactly: rep search with
	/// mml2 quantization, <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.IsAllowedNormalMatch(System.Int32,System.Int32)" /> (algorithm + the always-true
	/// far gate algorithm for ≤256KB windows), <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.IsNormalMatchBetter(System.Int32,System.Int32,System.Int32,System.Int32)" /> and
	/// <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.TakeNewOverRep(System.Int32,System.Int32,System.Int32)" />, reading the 4-pair Pareto frontier from the full-history
	/// suffix-trie walk. The goal is <c>Tally(the greedy parse) == Tally(the greedy)</c> so the single level-7 DP
	/// pass under the greedy histogram reproduces the emitted parse (the one-DP-pass byte-identity model).
	/// Thread-static, default off ⇒ the production greedy path is byte-identical to before.
	/// </summary>
	[ThreadStatic]
	internal static bool UseExactGreedy;

	/// <summary>
	/// Diagnostic (default off): apply the <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.DecaySeedPassinfo(System.Int32[])" /> per-region
	/// histogram decay to the greedy seed Tally before the single DP pass builds its cost tables, exactly
	/// as the does for a standalone level-7 chunk. Thread-static so the production path is unaffected.
	/// </summary>
	[ThreadStatic]
	internal static bool UseSeedDecay = false;

	/// <summary>
	/// Diagnostic (default off): run the level-7 forward DP with the suffix-trie match finder
	/// minimum match length of <b>3</b> (mml=3, <c>suffix-trie match-finder creation</c>
	/// firstbytes=2 ⇒ the trie reports length-3 matches at lvl7). The DP's candidate chain is built with a
	/// 3-byte hash so positions sharing only 3 bytes collide, the Pareto frontier records length-3 pairs, and
	/// a length-3 NEW match is relaxed as a primary (the sublen-FILL floor stays 4 per the new cfg
	/// <c>lo = 4</c>). This surfaces the text cmd-125 <c>mat3 dist237</c> the 4-byte finder
	/// structurally cannot. Thread-static so the production path (4-byte finder) is unaffected.
	/// </summary>
	[ThreadStatic]
	internal static bool UseDpMml3;

	/// <summary>The suffix-trie match finder minimum match length at level 7 (firstbytes=2 ⇒ 3).</summary>
	private const int DpMml3 = 3;

	/// <summary>
	/// Diagnostic (default off): enable the per-match <b>sublen-FILL</b> in <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.ForwardDp(System.ReadOnlySpan{System.Byte},System.Int32[],System.Int32[],System.Int32,System.Int32,System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts,System.Int32[],System.Int32)" />
	/// instead of filling every sublength unconditionally. The per-pass config uses a high bound of 128 at level 7.
	/// REP cfg is <c>{3, 125}</c>; NEW cfg is <c>{mml+1, 128-(mml+1)}</c>.
	/// Both collapse to "fill sublengths only when the match length &lt; 128": the DP always relaxes the
	/// full-length match, then fills shorter lengths only inside the window. Earlier FILL-ALL over-relaxed
	/// long matches (≥128 B, e.g. the rep8_4k/zeros4k 4080-byte runs), creating thousands of extra arrivals.
	/// Thread-static, default off so the production path is byte-identical to before; A/B'd via diagnostic comparison.
	/// </summary>
	[ThreadStatic]
	internal static bool UseSublenGate = false;

	/// <summary>the sublen-fill window high bound (level 7 ⇒ 128).</summary>
	private const int SublenFillThreshold = 128;

	/// <summary>
	/// Diagnostic (default off): on an EXACT-cost arrival tie, keep the LATER-tried relaxation instead of the
	/// first one (i.e. use <c>total &lt;= arr[dst].Cost</c> rather than strict <c>&lt;</c> in
	/// <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.RelaxRep(LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Arrival[],System.Int32,System.Int32,System.Int32,System.Int64,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts)" />/<see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.RelaxNew(LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Arrival[],System.Int32,System.Int32,System.Int64,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts)" />). validated from the cmd-20 divergence: under the exact
	/// captured seed the two local paths (<c>1 lit + match@556</c> vs <c>2 lit + match@557</c>, both len-47,
	/// distances 551 vs 496 in the SAME offset bucket) cost <b>byte-identically</b> (proven term-by-term via the
	/// diagnostic comparison cost probe: lit/packet/offset all equal, delta 0). The tie-break keeps the path whose match starts one
	/// byte EARLIER (src 603, the longer LRL=2 run at pos 605) — i.e. with the ascending LRL loop the last equal
	/// relaxation wins. Strict-<c>&lt;</c> kept the first (src 604) path. Thread-static, default off so the
	/// production path is unchanged; A/B'd via diagnostic comparison (must extend EXTSEED call0 past cmd-20 while keeping the
	/// zeros/rep8_4k vectors byte-exact).
	/// </summary>
	[ThreadStatic]
	internal static bool UseTieBreakLast = false;

	/// <summary>
	/// Diagnostic (default off): run <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.ForwardDp(System.ReadOnlySpan{System.Byte},System.Int32[],System.Int32[],System.Int32,System.Int32,System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts,System.Int32[],System.Int32)" /> as the reference's LIMITED-LOOKAHEAD, GREEDY-COMMITTED,
	/// ADAPTIVE WINDOWED optimal parse (num_tlls==1 / lvl7) instead of a single monolithic
	/// full-chunk DP. Reversed byte-exact: the parse advances in a 256-byte <b>commit stride</b>
	/// (<see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.WindowCommitStride" />) with a 4096-byte <b>lookahead window</b>
	/// (<see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.WindowLookahead" />). A segment is committed at a clean re-anchor when the
	/// frontier <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.DpMaxReached" /> satisfies <c>maxReached &lt;= pos</c> (⇒ nothing relaxed
	/// past <c>pos</c>, so all later arrivals are still unreached — the invariant that makes the cut clean), or on a
	/// window overflow (<c>maxReached &gt; windowEnd</c>). On each mid-commit the committed segment's commands are
	/// tallied into a cumulative passinfo (<see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Tally(System.Collections.Generic.List{LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Command},System.ReadOnlySpan{System.Byte},System.Int32,System.Int32,System.Int32)" />) and the cost tables are rebuilt
	/// (<see cref="M:LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.BuildFromPassinfo(System.ReadOnlySpan{System.Int32},System.Int32)" />); at lvl7 the rebuild
	/// interval is 1 ⇒ rebuild on EVERY commit. Requires an <c>adaptPassinfo</c> seed (the
	/// post-decay greedy Tally) to be threaded into <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.ForwardDp(System.ReadOnlySpan{System.Byte},System.Int32[],System.Int32[],System.Int32,System.Int32,System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts,System.Int32[],System.Int32)" />; when null the classic single-pass DP runs
	/// unchanged (preserving the shipped manifest.json 279==279 byte-identity). Thread-static, default off.
	/// </summary>
	[ThreadStatic]
	internal static bool UseWindowedParse = false;

	/// <summary>The optimal-parse frontier: the furthest position any relaxation has
	/// reached in the current segment. Updated by <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.RelaxRep(LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Arrival[],System.Int32,System.Int32,System.Int32,System.Int64,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts)" />/<see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.RelaxNew(LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Arrival[],System.Int32,System.Int32,System.Int64,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts)" />/<see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Faef0(LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Arrival[],System.ReadOnlySpan{System.Byte},System.Int32,System.Int64,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts)" />
	/// (only when <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.UseWindowedParse" /> is set) to <c>max(DpMaxReached, dst)</c>; the windowed segment loop
	/// commits when it satisfies <c>maxReached &lt;= pos</c> (clean re-anchor) or exceeds the lookahead window.</summary>
	[ThreadStatic]
	internal static int DpMaxReached;

	/// <summary>the commit stride (256) — the windowed parse re-anchors/commits in strides
	/// of this many positions.</summary>
	private const int WindowCommitStride = 256;

	/// <summary>the lookahead window (4096) — a segment may look ahead at most this far
	/// before a forced window-overflow commit.</summary>
	private const int WindowLookahead = 4096;

	/// <summary>Diagnostic (default 0/0 = off): when <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.DpProbeHi" /> &gt; 0, <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.ForwardDp(System.ReadOnlySpan{System.Byte},System.Int32[],System.Int32[],System.Int32,System.Int32,System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts,System.Int32[],System.Int32)" />
	/// dumps the final arrival row (cost, src, lrl, ml, idx, dist, reps) for every position in
	/// <c>[DpProbeLo, DpProbeHi]</c> after the forward pass. Lets the diagnostic see exactly which arrivals the DP
	/// materialized at the cmd-20 reconvergence (is the match@556→arrival[603] path even created?).</summary>
	[ThreadStatic]
	internal static int DpProbeLo = 0;

	/// <summary>Upper bound (inclusive) of the <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.DpProbeLo" /> arrival dump; 0 disables.</summary>
	[ThreadStatic]
	internal static int DpProbeHi = 0;

	/// <summary>Diagnostic (default 0 = off): when a rep/new relax writes (or attempts) arr[DiagWatchDst],
	/// log pos / ml / loi / base / matchcost / total / won. Pins which path sets a contested arrival cost.</summary>
	[ThreadStatic]
	internal static int DiagWatchDst = 0;

	/// <summary>Diagnostic / reference diagnostic ONLY (default null): when set, <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.FindCandidates(System.ReadOnlySpan{System.Byte},System.Int32,System.Int32[],System.Int32[],System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts,System.Span{System.Int32},System.Span{System.Int32},System.Span{System.Int32},LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Ctmf)" />
	/// ignores the managed hash-chain finder and instead reads the captured per-position match table —
	/// a flat array of <c>npos*8</c> ints (4 <c>(len,dist)</c> pairs/pos, length-descending, len&lt;=0 terminator),
	/// exactly the suffix-trie match finder output in <c>finder_call1.bin</c>. Used to prove the LZ-parse gap is
	/// 100% the finder (len-2/3 short matches the 4-byte-hash finder can't surface) without implementing the trie.</summary>
	[ThreadStatic]
	internal static int[]? DiagExternalFinder = null;

	/// <summary>DIAGNOSTIC only: raw 0x1808-byte codecost blob captured live from the reference
	/// encoder. When set, <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.DiagDpFromExternalPassinfo(System.Byte[],System.Int32[],System.Int32,System.Int32[]@,System.Int32[]@,System.Int32[]@,System.Int32[]@)" /> drives the DP
	/// with this EXACT codecost (via <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.FromRawBlob(System.ReadOnlySpan{System.Byte})" />) instead of building from
	/// the passinfo seed — isolating "wrong codecost" from "wrong DP recurrence".</summary>
	[ThreadStatic]
	internal static byte[]? DiagExternalCodeCostBlob = null;

	/// <summary>Diagnostic (default null): when non-null, <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.ForwardDp(System.ReadOnlySpan{System.Byte},System.Int32[],System.Int32[],System.Int32,System.Int32,System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts,System.Int32[],System.Int32)" /> copies its final
	/// per-position arrival table into this array right after the forward pass (before the backward trace),
	/// so a caller can replay a known parse through arr[] to test reachability vs. trace selection.
	/// Layout per index: (cost, src, ml, idx, dist, lrl, contMl, r0). cost==int.MaxValue ⇒ unreached.</summary>
	[ThreadStatic]
	internal static (int cost, int src, int ml, int idx, int dist, int lrl, int contMl, int r0)[]? DiagArrCapture;

	/// <summary>Diagnostic: populated by <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.DiagDpFromSeed(System.Byte[],System.Int32[],System.Int32[],System.Int32[],System.Int32[],System.Int32[],System.Int32[]@,System.Int32[]@,System.Int32[]@,System.Int32[]@,System.Int32[]@,System.Int64@,System.Int64@)" /> — a human-readable trace of replaying the
	/// seed parse through the DP's captured arrival table, reporting the first reference command whose end-position is
	/// unreached or reached at a HIGHER cost than the cumulative (localizing a reachability/relaxation gap vs a
	/// pure backward-trace selection difference).</summary>
	[ThreadStatic]
	internal static string? DiagReachReport;

	/// <summary>
	/// Diagnostic (default off): implement the tiny-offset round-up branch in the exact-greedy selector
	/// and the level-7 DP candidate set. A new-match candidate whose distance is
	/// &lt; 8 (sub-<see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.MinDistance" />, un-codeable in the offset44 scheme) is NOT discarded as the Pareto
	/// walk otherwise does; the encoder rounds the distance up to the smallest codeable distance that preserves the
	/// tiny period (<see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.RoundUpTinyTable" />), recomputes the match length at that rounded distance
	/// (the same length recomputation path), and — if it still reaches the mml — feeds it through the normal
	/// allowed/better gates. This is the last not-yet-modeled greedy micro-branch; surfacing these short-period
	/// candidates perturbs the greedy <c>Tally</c> histogram that seeds the one-DP-pass cost tables. Thread-static
	/// so the production path (which never sees dist&lt;8) is byte-identical to before.
	/// </summary>
	[ThreadStatic]
	internal static bool UseTinyOffsetRemap = false;

	/// <summary>
	/// <c>tiny-offset round-up</c> table (read byte-exact against reference behavior): maps a
	/// tiny distance 1..7 to the smallest offset44-codeable distance (≥8) that preserves the tiny period
	/// (1→8, 2→8, 3→9, 4→8, 5→10, 6→12, 7→14). Index 0 is unused (distance is always ≥1).
	/// </summary>
	private static readonly int[] RoundUpTinyTable = new int[8] { 0, 8, 8, 9, 8, 10, 12, 14 };

	private static int ChainWalkLimit
	{
		get
		{
			if (!HasActiveCompressionLevel)
			{
				return 64; // Default to Level 6 (fast & optimal for managed C#)
			}
			int activeCompressionLevel = ActiveCompressionLevel;
			return (activeCompressionLevel <= -4) ? 4 : (activeCompressionLevel switch
			{
				-3 => 8, 
				-2 => 16, 
				-1 => 32, 
				0 => 48, 
				1 => 64, 
				2 => 80, 
				3 => 96, 
				_ => 128, 
			});
		}
	}

	/// <summary>
	/// Compresses <paramref name="data" /> (one PFS block) into a section-7 payload of one or two
	/// newLZ chunks, or returns null if the data is too small or does not compress below its original
	/// size.
	/// </summary>
	public static EncodedBlock? EncodeBlock(ReadOnlySpan<byte> data)
	{
		return EncodeBlock(data, useHuffmanArrays: false);
	}

	/// <summary>
	/// Compresses a block using a Kraken level in the supported -4..9 range. Negative fast
	/// levels reduce match-search effort and skip Huffman arrays; non-negative levels retain
	/// entropy coding when requested.
	/// </summary>
	public static EncodedBlock? EncodeBlock(ReadOnlySpan<byte> data, bool useHuffmanArrays, int compressionLevel)
	{
		return EncodeBlock(data, useHuffmanArrays, compressionLevel, allowSubLiterals: true, allowStoredHalves: true);
	}

	/// <summary>
	/// Compresses a block with explicit control over publisher half-block modes.
	/// </summary>
	public static EncodedBlock? EncodeBlock(ReadOnlySpan<byte> data, bool useHuffmanArrays, int compressionLevel, bool allowSubLiterals, bool allowStoredHalves)
	{
		if ((compressionLevel < -4 || compressionLevel > 9) ? true : false)
		{
			throw new ArgumentOutOfRangeException("compressionLevel");
		}
		int activeCompressionLevel = ActiveCompressionLevel;
		bool hasActiveCompressionLevel = HasActiveCompressionLevel;
		ActiveCompressionLevel = compressionLevel;
		HasActiveCompressionLevel = true;
		try
		{
			return EncodeBlockCore(data, useHuffmanArrays && compressionLevel >= 0, allowSubLiterals, allowStoredHalves);
		}
		finally
		{
			ActiveCompressionLevel = activeCompressionLevel;
			HasActiveCompressionLevel = hasActiveCompressionLevel;
		}
	}

	/// <summary>
	/// Compresses <paramref name="data" /> (one PFS block) into a section-7 payload of one or two
	/// newLZ chunks, or returns null if the data is too small or does not compress below its original
	/// size. When <paramref name="useHuffmanArrays" /> is true
	/// the literal/command/length arrays are Huffman-coded (entropy chunk type 2) via
	/// <see cref="T:LibProsperoPkg.PFS.Compression.Oodle.KrakenHuffmanArrayEncoder" /> when that is smaller than the raw form, shrinking actual
	/// blocks toward the sizes. The packed-offset array is always left raw because the offset
	/// reader inspects its first byte (the 0x80 bit selects two-table mode), which an entropy header
	/// would corrupt.
	/// </summary>
	public static EncodedBlock? EncodeBlock(ReadOnlySpan<byte> data, bool useHuffmanArrays)
	{
		return EncodeBlockCore(data, useHuffmanArrays, allowSubLiterals: true, allowStoredHalves: true);
	}

	private static EncodedBlock? EncodeBlockCore(ReadOnlySpan<byte> data, bool useHuffmanArrays, bool allowSubLiterals, bool allowStoredHalves)
	{
		int length = data.Length;
		if (length < 64)
		{
			return null;
		}
		int[] array = new int[131072];
		int[] prev = new int[length];
		array.AsSpan().Fill(-1);
		if (length <= 131072)
		{
			EncodedChunk? encodedChunk = EncodeChunk(data, array, prev, 0, length, withSeed: true, useHuffmanArrays, allowSubLiterals, allowOptimal: true);
			if (encodedChunk.HasValue)
			{
				EncodedChunk valueOrDefault = encodedChunk.GetValueOrDefault();
				if (valueOrDefault.Payload.Length < length)
				{
					int boundaryFlags = 6 | ((valueOrDefault.LiteralMode == 0) ? 1 : 0);
					return new EncodedBlock(valueOrDefault.Payload, multiChunk: false, valueOrDefault.Payload.Length, boundaryFlags);
				}
			}
			return null;
		}
		if (length > 262144)
		{
			return null;
		}
		EncodedChunk? encodedChunk2 = EncodeChunk(data, array, prev, 0, 131072, withSeed: true, useHuffmanArrays, allowSubLiterals);
		int num = length - 131072;
		EncodedChunk? encodedChunk3 = EncodeChunk(data, array, prev, 131072, length, withSeed: false, useHuffmanArrays, allowSubLiterals);
		int num2;
		if (encodedChunk2.HasValue)
		{
			EncodedChunk valueOrDefault2 = encodedChunk2.GetValueOrDefault();
			if (valueOrDefault2.Payload.Length <= 131071)
			{
				num2 = ((valueOrDefault2.Payload.Length < 131072) ? 1 : 0);
				goto IL_011f;
			}
		}
		num2 = 0;
		goto IL_011f;
		IL_011f:
		bool flag = (byte)num2 != 0;
		bool flag2 = encodedChunk3.HasValue && encodedChunk3.GetValueOrDefault().Payload.Length < num;
		if (!allowStoredHalves && (!flag || !flag2))
		{
			return null;
		}
		if (!flag && !flag2)
		{
			return null;
		}
		byte[] array2 = (flag ? encodedChunk2.Value.Payload : data.Slice(0, 131072).ToArray());
		byte[] array3 = (flag2 ? encodedChunk3.Value.Payload : data.Slice(131072).ToArray());
		byte[] array4 = new byte[array2.Length + array3.Length];
		Buffer.BlockCopy(array2, 0, array4, 0, array2.Length);
		Buffer.BlockCopy(array3, 0, array4, array2.Length, array3.Length);
		if (array4.Length >= length)
		{
			return null;
		}
		int num3 = 4;
		if (flag)
		{
			num3 |= 2;
			if (encodedChunk2.Value.LiteralMode == 0)
			{
				num3 |= 1;
			}
		}
		if (flag2)
		{
			num3 |= 0x20;
			if (encodedChunk3.Value.LiteralMode == 0)
			{
				num3 |= 0x10;
			}
		}
		return new EncodedBlock(array4, multiChunk: true, array2.Length, num3);
	}

	/// <summary>
	/// Encodes the output range [<paramref name="chunkStart" />, <paramref name="chunkEnd" />) of
	/// <paramref name="data" /> as one newLZ chunk. The shared <paramref name="head" />/<paramref name="prev" />
	/// hash chain lets a later chunk reference matches in an earlier one. When
	/// <paramref name="withSeed" /> is false the chunk omits the 8-byte COPY_64 seed (used for any
	/// chunk decoded at a non-zero output offset).
	/// </summary>
	private static EncodedChunk? EncodeChunk(ReadOnlySpan<byte> data, int[] head, int[] prev, int chunkStart, int chunkEnd, bool withSeed, bool useHuffmanArrays, bool allowSubLiterals, bool allowOptimal = false)
	{
		int matchLimit = chunkEnd - 8;
		int matchStartLimit = chunkEnd - 16;
		int num = (withSeed ? (chunkStart + 8) : chunkStart);
		if (withSeed)
		{
			for (int i = chunkStart; i < num; i++)
			{
				Insert(data, i, head, prev);
			}
		}
		List<Command> list;
		if (UseOptimalParse || (allowOptimal && ProductionOptimalSingleChunk && chunkEnd - chunkStart <= ProductionOptimalMaxBlock))
		{
			bool useDpMml = UseDpMml3;
			UseDpMml3 = true;
			try
			{
				list = ParseOptimal(data, head, prev, num, num, matchLimit, matchStartLimit, chunkStart, chunkEnd, withSeed, useHuffmanArrays);
			}
			finally
			{
				UseDpMml3 = useDpMml;
			}
		}
		else
		{
			list = Parse(data, head, prev, num, num, matchLimit, matchStartLimit);
		}
		if (list.Count == 0)
		{
			return null;
		}
		int num2 = ((!allowSubLiterals) ? 1 : LitModeForParse(data, list, chunkEnd, useHuffmanArrays));
		byte[] array = EmitChunkFromCommands(data, list, chunkStart, chunkEnd, withSeed, useHuffmanArrays, num2);
		if (array != null)
		{
			return new EncodedChunk(array, num2);
		}
		return null;
	}

	/// <summary>
	/// Builds the newLZ stream arrays from an already-computed parse and assembles the chunk bytes
	/// (entropy-coding the lit/cmd/length arrays when beneficial). Factored out of <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.EncodeChunk(System.ReadOnlySpan{System.Byte},System.Int32[],System.Int32[],System.Int32,System.Int32,System.Boolean,System.Boolean,System.Boolean,System.Boolean)" />
	/// so a diagnostic can measure the actual entropy-coded emit size of an externally supplied parse
	/// (the vs the DP's) — the metric the level-7 producer actually minimises in its final
	/// greedy-vs-DP selection (actual-emit-size pick). Returns null when the parse needs a store-raw
	/// fallback (a literal run over MaxLitRun or a match starting inside the no-match zone).
	/// </summary>
	private static byte[]? EmitChunkFromCommands(ReadOnlySpan<byte> data, List<Command> commands, int chunkStart, int chunkEnd, bool withSeed, bool useHuffmanArrays, int litMode = 1)
	{
		int num = chunkEnd - 16;
		foreach (Command command in commands)
		{
			if (command.MatchLen > 0 && command.LitStart + command.LitLen > num)
			{
				return null;
			}
		}
		List<byte> list = new List<byte>(chunkEnd - chunkStart);
		List<byte> list2 = new List<byte>(chunkEnd - chunkStart);
		BuildLiteralStreams(data, commands, chunkEnd, list, list2);
		List<byte> list3 = new List<byte>();
		List<byte> list4 = new List<byte>();
		List<byte> packedLitLen = new List<byte>();
		KrakenBitWriter krakenBitWriter = new KrakenBitWriter();
		KrakenBitWriter krakenBitWriter2 = new KrakenBitWriter();
		List<uint> escapeValues = new List<uint>();
		int num2 = 0;
		foreach (Command command2 in commands)
		{
			if (command2.MatchLen != 0)
			{
				int litLen = command2.LitLen;
				int num3 = ((litLen >= 3) ? 3 : litLen);
				int num4 = ((command2.MatchLen <= 16) ? (command2.MatchLen - 2) : 15);
				list3.Add((byte)((command2.OffsIndex << 6) | (num4 << 2) | num3));
				if (num3 == 3)
				{
					EmitLen(litLen - 3);
				}
				if (command2.OffsIndex == 3)
				{
					byte item = (((num2 & 1) == 0) ? krakenBitWriter.WriteDistance(command2.Distance) : krakenBitWriter2.WriteDistance(command2.Distance));
					list4.Add(item);
					num2++;
				}
				if (num4 == 15)
				{
					EmitLen(command2.MatchLen - 17);
				}
			}
		}
		if (escapeValues.Count > 512)
		{
			return null;
		}
		if (list.Count > 262143 || list3.Count > 262143 || list4.Count > 262143 || packedLitLen.Count > 262143)
		{
			return null;
		}
		List<byte> array = litMode switch
		{
			0 => list2, 
			1 => list, 
			_ => (ChooseLitMode(list, list2, useHuffmanArrays) == 0) ? list2 : list, 
		};
		byte[] collection = krakenBitWriter.ToBytes();
		byte[] array2 = krakenBitWriter2.ToBytes();
		List<byte> list5 = new List<byte>(chunkEnd - chunkStart);
		if (withSeed)
		{
			for (int i = 0; i < 8; i++)
			{
				list5.Add(data[chunkStart + i]);
			}
		}
		KrakenBitWriter krakenBitWriter3 = new KrakenBitWriter();
		KrakenBitWriter krakenBitWriter4 = new KrakenBitWriter();
		for (int j = 0; j < escapeValues.Count; j++)
		{
			if ((j & 1) == 0)
			{
				krakenBitWriter3.WriteLength(escapeValues[j]);
			}
			else
			{
				krakenBitWriter4.WriteLength(escapeValues[j]);
			}
		}
		byte[] array3 = krakenBitWriter3.ToBytes();
		byte[] array4 = krakenBitWriter4.ToBytes();
		int num5 = array3.Length + array4.Length;
		if (num5 > 8191)
		{
			return null;
		}
		if (num5 <= 31)
		{
			list5.Add((byte)(0x80 | num5));
		}
		else
		{
			list5.Add((byte)(0xA0 | (num5 & 0x1F)));
			list5.Add((byte)((num5 >> 5) - 1));
		}
		WriteArray(list5, array, useHuffmanArrays);
		WriteArray(list5, list3, useHuffmanArrays);
		WriteArray(list5, list4, useHuffmanArrays);
		WriteArray(list5, packedLitLen, useHuffmanArrays);
		list5.AddRange(collection);
		for (int num6 = array2.Length - 1; num6 >= 0; num6--)
		{
			list5.Add(array2[num6]);
		}
		list5.AddRange(array3);
		for (int num7 = array4.Length - 1; num7 >= 0; num7--)
		{
			list5.Add(array4[num7]);
		}
		return list5.ToArray();
		void EmitLen(int packedValue)
		{
			if (packedValue < 255)
			{
				packedLitLen.Add((byte)packedValue);
			}
			else
			{
				packedLitLen.Add(byte.MaxValue);
				escapeValues.Add((uint)(packedValue - 255));
			}
		}
	}

	private static void WriteRawArray(List<byte> outBuf, List<byte> array)
	{
		int count = array.Count;
		outBuf.Add((byte)((count >> 16) & 0xFF));
		outBuf.Add((byte)((count >> 8) & 0xFF));
		outBuf.Add((byte)(count & 0xFF));
		outBuf.AddRange(array);
	}

	/// <summary>
	/// Returns the byte length the array writer would emit for a candidate byte stream (the
	/// smaller of the raw 3-byte-header form and the Huffman form), without writing. Used to pick the
	/// cheaper literal model (sub vs raw) the way the array coder does.
	/// </summary>
	private static void BuildLiteralStreams(ReadOnlySpan<byte> data, List<Command> commands, int chunkEnd, List<byte> litRaw, List<byte> litSub)
	{
		int num = -8;
		foreach (Command command2 in commands)
		{
			for (int i = 0; i < command2.LitLen; i++)
			{
				int num2 = command2.LitStart + i;
				litRaw.Add(data[num2]);
				litSub.Add((byte)(data[num2] - data[num2 + num]));
			}
			if (command2.MatchLen > 0)
			{
				num = -command2.Distance;
			}
		}
		Command command = commands[commands.Count - 1];
		for (int j = command.LitStart + command.LitLen + command.MatchLen; j < chunkEnd; j++)
		{
			litRaw.Add(data[j]);
			litSub.Add((byte)(data[j] - data[j + num]));
		}
	}

	internal static int ChooseLitMode(List<byte> litRaw, List<byte> litSub, bool useHuffmanArrays)
	{
		if (litSub.Count < 32)
		{
			return 1;
		}
		int num = EncodedArraySize(litSub, useHuffmanArrays);
		return (EncodedArraySize(litRaw, useHuffmanArrays) < num) ? 1 : 0;
	}

	private static int LitModeForParse(ReadOnlySpan<byte> data, List<Command> commands, int chunkEnd, bool useHuffmanArrays)
	{
		if (commands.Count == 0)
		{
			return 1;
		}
		List<byte> litRaw = new List<byte>(chunkEnd);
		List<byte> litSub = new List<byte>(chunkEnd);
		BuildLiteralStreams(data, commands, chunkEnd, litRaw, litSub);
		return ChooseLitMode(litRaw, litSub, useHuffmanArrays);
	}

	private static int EncodedArraySize(List<byte> array, bool useHuffman)
	{
		int num = array.Count + 3;
		if (useHuffman && array.Count >= 2)
		{
			byte[] array2 = KrakenHuffmanArrayEncoder.TryEncode(CollectionsMarshal.AsSpan(array));
			if (array2 != null && array2.Length < num)
			{
				return array2.Length;
			}
		}
		return num;
	}

	/// <summary>
	/// Writes <paramref name="array" /> as an entropy (Huffman) array when <paramref name="useHuffman" />
	/// is set and the Huffman form is strictly smaller than the raw form; otherwise writes it raw.
	/// The literal/command/length streams are read by the decoder via plain <c>DecodeBytes</c>, so a
	/// type-2 entropy array is transparently accepted in their place.
	/// </summary>
	private static void WriteArray(List<byte> outBuf, List<byte> array, bool useHuffman)
	{
		if (useHuffman && array.Count >= 2)
		{
			byte[] array2 = KrakenHuffmanArrayEncoder.TryEncode(CollectionsMarshal.AsSpan(array));
			if (array2 != null && array2.Length < array.Count + 3)
			{
				outBuf.AddRange(array2);
				return;
			}
		}
		WriteRawArray(outBuf, array);
	}

	/// <summary>
	/// Diagnostic only: measure the actual entropy-coded emit size, in bytes, of an
	/// externally supplied full-chunk parse (e.g. the captured command list vs the DP's command
	/// list) under the actual newLZ back-end (the same stream-build + array entropy-coder the shipping
	/// encoder uses). This is the metric the level-7 producer minimises when it picks the final
	/// parse (greedy-vs-DP actual-emit-size selection), so it is
	/// the authoritative arbiter for why the emits an integer-"costlier" near-offset rep0 cascade.
	/// The parse is the seeded first chunk (firstMatchPos = 8). litStart is reconstructed by walking.
	/// Returns the encoded byte length, or -1 when the parse falls back to store-raw.
	/// </summary>
	internal static int DiagRealEmitSize(byte[] data, int[] litLen, int[] dist, int[] match, int[] idx, bool useHuffmanArrays = true, int litMode = 1)
	{
		List<Command> list = new List<Command>(litLen.Length);
		int num = 8;
		for (int i = 0; i < litLen.Length; i++)
		{
			list.Add(new Command(num, litLen[i], dist[i], match[i], idx[i]));
			num += litLen[i] + match[i];
		}
		return EmitChunkFromCommands(data, list, 0, data.Length, withSeed: true, useHuffmanArrays, litMode)?.Length ?? (-1);
	}

	/// <summary>
	/// Diagnostic only: same as <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.DiagRealEmitSize(System.Byte[],System.Int32[],System.Int32[],System.Int32[],System.Int32[],System.Boolean,System.Int32)" /> but returns the actual
	/// emitted chunk bytes (seed + ctrl + arrays + dual bitstream) so a diagnostic can byte-compare my
	/// emit of an externally supplied parse against the actual section-7 chunk. Returns null on a
	/// store-raw fallback.
	/// </summary>
	internal static byte[]? DiagEmitChunkBytes(byte[] data, int[] litLen, int[] dist, int[] match, int[] idx, bool useHuffmanArrays = true, int litMode = 1)
	{
		List<Command> list = new List<Command>(litLen.Length);
		int num = 8;
		for (int i = 0; i < litLen.Length; i++)
		{
			list.Add(new Command(num, litLen[i], dist[i], match[i], idx[i]));
			num += litLen[i] + match[i];
		}
		return EmitChunkFromCommands(data, list, 0, data.Length, withSeed: true, useHuffmanArrays, litMode);
	}

	/// <summary>
	/// Value-model lazy parse over the output range, a faithful managed implementation of the reference
	/// <c>greedy optimal pre-pass</c> (the implementation). Each
	/// candidate match is scored by the exact value function (<see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.FindMatch(System.ReadOnlySpan{System.Byte},System.Int32,System.Int32[],System.Int32[],System.Int32,System.Int32,System.Int32,System.Int32)" /> /
	/// <c>match value</c>):<c>len*4 - (isRep ? 0 : bitlen(dist)+2)</c> — validated from
	/// <c>algorithm</c> + <c>algorithm</c> (<c>bitlen = 32 - clz</c>). Repeat-offset matches
	/// transmit no offset, so they cost 0 there and are strongly preferred; among new offsets the
	/// finder keeps the highest value and, on a value tie, the SHORTEST distance (the hash chain is
	/// walked most-recent-first, so the nearer offset is seen first and wins the tie). Picking the
	/// nearer offset on ties seeds a reusable recent-offset that subsequent positions re-use as cheap
	/// reps — exactly the rep-reuse cascade the parser produces (e.g. introducing dist=57 then
	/// reusing it for hundreds of commands instead of walking dist=552,553,554...).
	/// <para>
	/// A 2-step lazy look-ahead defers the match start while a later position yields a strictly better
	/// match, using the exact comparator and thresholds: defer by 1 when
	/// <c>value(pos+1) - value(cur) - 4 &gt;= 1</c>, else defer by 2 when
	/// <c>value(pos+2) - value(cur) - 4 &gt;= 4</c>, else emit <c>cur</c> (the <c>+4</c> incumbent bias
	/// and the <c>&lt;1</c>/<c>&lt;4</c> gates are <c>algorithm</c>'s).
	/// </para>
	/// Matches never start past <paramref name="matchStartLimit" /> (the match_zone_end, chunkEnd-16)
	/// nor extend past <paramref name="matchLimit" /> (chunkEnd-8), preserving the trailing literal run.
	/// </summary>
	private static List<Command> Parse(ReadOnlySpan<byte> data, int[] head, int[] prev, int startPos, int litStart0, int matchLimit, int matchStartLimit)
	{
		List<Command> list = new List<Command>();
		int num = litStart0;
		int num2 = startPos;
		int r = 8;
		int r2 = 8;
		int r3 = 8;
		int inserted = startPos;
		while (num2 <= matchStartLimit)
		{
			inserted = InsertUpTo(data, head, prev, inserted, num2);
			Cand cand = (UseExactGreedy ? FindMatchExact(data, num2, head, prev, matchLimit, r, r2, r3, num2 - num) : FindMatch(data, num2, head, prev, matchLimit, r, r2, r3));
			if (!cand.Valid)
			{
				num2++;
				continue;
			}
			while (num2 + 1 <= matchStartLimit)
			{
				inserted = InsertUpTo(data, head, prev, inserted, num2 + 1);
				Cand cand2 = (UseExactGreedy ? FindMatchExact(data, num2 + 1, head, prev, matchLimit, r, r2, r3, num2 + 1 - num) : FindMatch(data, num2 + 1, head, prev, matchLimit, r, r2, r3));
				if (cand2.Value - cand.Value - 4 >= 1)
				{
					num2++;
					cand = cand2;
					continue;
				}
				if (num2 + 2 > matchStartLimit)
				{
					break;
				}
				inserted = InsertUpTo(data, head, prev, inserted, num2 + 2);
				Cand cand3 = (UseExactGreedy ? FindMatchExact(data, num2 + 2, head, prev, matchLimit, r, r2, r3, num2 + 2 - num) : FindMatch(data, num2 + 2, head, prev, matchLimit, r, r2, r3));
				if (cand3.Value - cand.Value - 4 < 4)
				{
					break;
				}
				num2 += 2;
				cand = cand3;
			}
			list.Add(new Command(num, num2 - num, cand.Dist, cand.Len, cand.Idx));
			UpdateRecent(ref r, ref r2, ref r3, cand.Idx, cand.Dist);
			int num3 = num2 + cand.Len;
			inserted = InsertUpTo(data, head, prev, inserted, Math.Min(num3, matchStartLimit + 1));
			num2 = num3;
			num = num3;
		}
		return list;
	}

	/// <summary>Bit length of <paramref name="v" /> (the <c>algorithm</c>:<c>32 - clz</c>).</summary>
	private static int BitLen(uint v)
	{
		if (v != 0)
		{
			return 32 - BitOperations.LeadingZeroCount(v);
		}
		return 0;
	}

	/// <summary>
	/// the per-position match finder (<c>algorithm</c>, value model):returns the highest-value
	/// match at <paramref name="pos" /> given the three recent offsets, preferring a repeat-offset reuse
	/// on ties (its offset is free) and otherwise the shortest-distance new offset on a value tie.
	/// </summary>
	private static Cand FindMatch(ReadOnlySpan<byte> data, int pos, int[] head, int[] prev, int matchLimit, int r0, int r1, int r2)
	{
		int num = RepMatchLength(data, pos, r0, matchLimit);
		int num2 = RepMatchLength(data, pos, r1, matchLimit);
		int num3 = RepMatchLength(data, pos, r2, matchLimit);
		int num4 = num;
		int num5 = 0;
		if (num2 > num4)
		{
			num4 = num2;
			num5 = 1;
		}
		if (num3 > num4)
		{
			num4 = num3;
			num5 = 2;
		}
		int num6 = ((GreedyMmlOverride > 0) ? GreedyMmlOverride : 4);
		int num7 = 0;
		int dist = 0;
		int num8 = -1073741824;
		uint num9 = Hash(data, pos);
		int num10 = head[num9];
		int num11 = 0;
		while (num10 >= 0 && num11 < ChainWalkLimit)
		{
			int num12 = pos - num10;
			if (num12 >= 8)
			{
				int num13 = MatchLength(data, num10, pos, matchLimit);
				if (num13 >= num6)
				{
					int num14 = num13 * 4 - (BitLen((uint)num12) + 2);
					if (num14 > num8)
					{
						num8 = num14;
						num7 = num13;
						dist = num12;
					}
				}
			}
			num10 = prev[num10];
			num11++;
		}
		bool flag = num4 >= 2;
		bool flag2 = num7 >= num6;
		int num15 = (flag ? (num4 * 4) : (-1073741824));
		int num16 = (flag2 ? num8 : (-1073741824));
		if (!flag && !flag2)
		{
			return new Cand(0, 0, -1);
		}
		if (num15 >= num16)
		{
			return new Cand(num4, num5 switch
			{
				1 => r1, 
				0 => r0, 
				_ => r2, 
			}, num5);
		}
		return new Cand(num7, dist, 3);
	}

	/// <summary>
	/// Byte-exact implementation of the per-position selector:
	/// rep search (mml2-quantized via <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.RepExtendMml2(System.ReadOnlySpan{System.Byte},System.Int32,System.Int32,System.Int32)" />, algorithm) with an early return on a
	/// length-&gt;=4 rep; otherwise the 4-pair Pareto new-match scan gated by <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.IsAllowedNormalMatch(System.Int32,System.Int32)" />
	/// and ranked by <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.IsNormalMatchBetter(System.Int32,System.Int32,System.Int32,System.Int32)" />; final rep-vs-new pick
	/// by <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.TakeNewOverRep(System.Int32,System.Int32,System.Int32)" />. <paramref name="lrl" /> is the current literal-run
	/// length (pos − litStart):only the rare <c>lrl &gt; 0x37</c> long-run adjustment consumes it.
	/// </summary>
	private static Cand FindMatchExact(ReadOnlySpan<byte> data, int pos, int[] head, int[] prev, int matchLimit, int r0, int r1, int r2, int lrl)
	{
		int num = RepExtendMml2(data, pos, r0, matchLimit);
		int num2 = RepExtendMml2(data, pos, r1, matchLimit);
		int num3 = RepExtendMml2(data, pos, r2, matchLimit);
		int num4 = -1;
		int num5 = -1;
		if (num4 < num)
		{
			num4 = num;
			num5 = 0;
		}
		if (num4 < num2)
		{
			num4 = num2;
			num5 = 1;
		}
		if (num4 < num3)
		{
			num4 = num3;
			num5 = 2;
		}
		if (num4 >= 4)
		{
			return new Cand(num4, num5 switch
			{
				1 => r1, 
				0 => r0, 
				_ => r2, 
			}, num5);
		}
		int num6 = ((GreedyMmlOverride > 0) ? GreedyMmlOverride : 4);
		if (lrl > 55)
		{
			if (num4 < 3)
			{
				num4 = 0;
			}
			num6++;
		}
		Span<int> outLen = stackalloc int[4];
		Span<int> outDist = stackalloc int[4];
		int num7 = FindParetoPairs(data, pos, head, prev, matchLimit, outLen, outDist);
		int num8 = 0;
		int num9 = 0;
		for (int i = 0; i < num7; i++)
		{
			int num10 = outLen[i];
			int num11 = outDist[i];
			if (num10 < num6)
			{
				break;
			}
			int num12 = num10;
			int num13 = num11;
			if (UseTinyOffsetRemap && num11 < 8)
			{
				int num14 = RoundUpTiny(num11);
				if (num14 == 0 || num14 > pos)
				{
					continue;
				}
				int num15 = MatchLength(data, pos - num14, pos, matchLimit);
				if (num15 < num6)
				{
					continue;
				}
				num12 = num15;
				num13 = num14;
			}
			if (IsAllowedNormalMatch(num12, num13) && IsNormalMatchBetter(num12, num13, num8, num9))
			{
				num8 = num12;
				num9 = num13;
			}
		}
		if (!TakeNewOverRep(num4, num8, num9))
		{
			return new Cand(num4, num5 switch
			{
				1 => r1, 
				0 => r0, 
				_ => r2, 
			}, num5);
		}
		if (num8 <= 0)
		{
			return new Cand(0, 0, -1);
		}
		return new Cand(num8, num9, 3);
	}

	/// <summary>
	/// Reproduces the suffix-trie match output: the per-position Pareto frontier (closest
	/// offset per achievable length), capped to the 4 longest pairs, length-descending. Walks the full-history
	/// 4-byte hash chain most-recent-first (distance ascending) and records (len,dist) only when len exceeds
	/// the running best. This is the same walk <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.FindCandidates(System.ReadOnlySpan{System.Byte},System.Int32,System.Int32[],System.Int32[],System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts,System.Span{System.Int32},System.Span{System.Int32},System.Span{System.Int32},LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Ctmf)" /> uses for the DP, minus the
	/// synthetic off-8 append the greedy selector must not see.
	/// </summary>
	private static int FindParetoPairs(ReadOnlySpan<byte> data, int pos, int[] head, int[] prev, int matchLimit, Span<int> outLen, Span<int> outDist)
	{
		Span<int> span = stackalloc int[64];
		Span<int> span2 = stackalloc int[64];
		int num = 0;
		int num2 = 0;
		int num3 = (UseTinyOffsetRemap ? 1 : 8);
		int num4 = ((GreedyMmlOverride > 0) ? GreedyMmlOverride : 4);
		uint num5 = Hash(data, pos);
		for (int num6 = head[num5]; num6 >= 0; num6 = prev[num6])
		{
			int num7 = pos - num6;
			if (num7 >= num3)
			{
				int num8 = MatchLength(data, num6, pos, matchLimit);
				if (num8 > num2)
				{
					num2 = num8;
					if (num8 >= num4 && num < span.Length)
					{
						span[num] = num8;
						span2[num] = num7;
						num++;
					}
				}
			}
		}
		int num9 = ((num > 4) ? 4 : num);
		for (int i = 0; i < num9; i++)
		{
			int index = num - 1 - i;
			outLen[i] = span[index];
			outDist[i] = span2[index];
		}
		return num9;
	}

	/// <summary>
	/// Rep-match length with the mml2 quantization:0 for fewer than 2 matching leading
	/// bytes, exactly 2 or 3 for a 2- or 3-byte lead, or the full forward length for a 4+-byte lead. The raw
	/// count already yields 2/3/full for those cases; only a raw count of 1 must be clamped to 0 (a rep is
	/// never length 1).
	/// </summary>
	private static int RepExtendMml2(ReadOnlySpan<byte> data, int pos, int dist, int limit)
	{
		int num = RepMatchLength(data, pos, dist, limit);
		if (num >= 2)
		{
			return num;
		}
		return 0;
	}

	/// <summary>
	/// algorithm (newLZ asymmetric match comparator):is the new (len,off) strictly better than the current
	/// best (len,off)? Longer always wins; equal length prefers the closer offset; one byte longer wins only if
	/// the new offset is within 128× the incumbent; two-or-more longer always wins. The first candidate
	/// (best = 0) is always accepted since any candidate length is &gt;= mml &gt;= 2.
	/// </summary>
	private static bool IsNormalMatchBetter(int newml, int newoff, int bestml, int bestoff)
	{
		if (newml < bestml)
		{
			return false;
		}
		if (newml == bestml)
		{
			return newoff < bestoff;
		}
		if (newml >= bestml + 2)
		{
			return true;
		}
		return newoff >> 7 <= bestoff;
	}

	/// <summary>
	/// The algorithm decides whether the new match be taken over the rep match? the is strongly rep-biased — a new
	/// match must be at least 2 longer than the rep, and longer still as its offset grows (3+ longer for
	/// offset &gt;= 0x400, 4+ longer for offset &gt;= 0x10000). A rep shorter than 2 always loses.
	/// </summary>
	private static bool TakeNewOverRep(int repLen, int newLen, int newOff)
	{
		if (repLen < 2)
		{
			return true;
		}
		if (newLen < repLen + 2)
		{
			return false;
		}
		if (newLen < repLen + 3 && newOff >= 1024)
		{
			return false;
		}
		if (newLen < repLen + 4 && newOff >= 65536)
		{
			return false;
		}
		return true;
	}

	/// <summary>
	/// algorithm (IsAllowedNormalMatch). Short matches (ml &lt; 6) are gated by an offset ceiling
	/// (<see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.NormalMatchOffsetThreshold" />); ml &gt;= 6 is always allowed. The far-offset gate
	/// algorithm is a proven no-op for our window: cfg[0x44] = window bits = 18, so
	/// <c>off &lt; (1&lt;&lt;18)</c> holds for every offset in a &lt;=256KB window and f2350 returns
	/// <c>off &lt; 0x100000 = true</c>.
	/// </summary>
	private static bool IsAllowedNormalMatch(int ml, int off)
	{
		if (ml < 6)
		{
			return off < NormalMatchOffsetThreshold[ml];
		}
		return true;
	}

	/// <summary>Inserts every position in <c>[inserted, target)</c> into the hash chain, in order.</summary>
	private static int InsertUpTo(ReadOnlySpan<byte> data, int[] head, int[] prev, int inserted, int target)
	{
		while (inserted < target)
		{
			Insert(data, inserted, head, prev);
			inserted++;
		}
		return inserted;
	}

	/// <summary>
	/// Applies the decoder's recent-offset move-to-front update for the chosen offset index. Index
	/// 0/1/2 promotes that recent distance to the front; index 3 pushes a brand-new distance to the
	/// front and evicts the oldest. This exactly mirrors the <c>recent[]</c> shuffle in
	/// <see cref="T:LibProsperoPkg.PFS.Compression.Oodle.KrakenDecoder" />'s LZ-run loop.
	/// </summary>
	private static void UpdateRecent(ref int r0, ref int r1, ref int r2, int idx, int dist)
	{
		switch (idx)
		{
		case 1:
		{
			int num2 = r1;
			int num3 = r0;
			r0 = num2;
			r1 = num3;
			break;
		}
		case 2:
		{
			int num = r2;
			r2 = r1;
			r1 = r0;
			r0 = num;
			break;
		}
		default:
			r2 = r1;
			r1 = r0;
			r0 = dist;
			break;
		case 0:
			break;
		}
	}

	/// <summary>
	/// Length of a repeat-offset match at <paramref name="dist" /> bytes back from <paramref name="pos" />,
	/// capped so the match ends at or before <paramref name="limit" />. Returns 0 when the source would
	/// fall before the start of the buffer.
	/// </summary>
	private static int RepMatchLength(ReadOnlySpan<byte> data, int pos, int dist, int limit)
	{
		int num = pos - dist;
		if (num < 0)
		{
			return 0;
		}
		int i = 0;
		for (int num2 = limit - pos; i < num2 && data[num + i] == data[pos + i]; i++)
		{
		}
		return i;
	}

	private static void Insert(ReadOnlySpan<byte> data, int pos, int[] head, int[] prev)
	{
		uint num = Hash(data, pos);
		prev[pos] = head[num];
		head[num] = pos;
	}

	private static uint Hash(ReadOnlySpan<byte> data, int pos)
	{
		if (GreedyHashBytesOverride == 3)
		{
			return (uint)((data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16)) * 506832829) >> 15;
		}
		return (uint)((data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24)) * -1640531535) >> 15;
	}

	private static int MatchLength(ReadOnlySpan<byte> data, int a, int b, int limit)
	{
		int i = 0;
		for (int num = limit - b; i < num && data[a + i] == data[b + i]; i++)
		{
		}
		return i;
	}

	/// <summary>
	/// The sublen-fill predicate: fill is allowed iff <c>(uint)(len − lo) &lt; range</c>
	/// (cfg <c>{lo, range}</c> from the helper-style <c>{A, B−A}</c>). Unsigned compare folds
	/// the lower bound (<c>len &lt; lo</c> underflows to a huge value ⇒ false) into a single branch.
	/// </summary>
	private static bool SublenFillAllowed(int lo, int range, int len)
	{
		return (uint)(len - lo) < (uint)range;
	}

	/// <summary>Rounds a tiny distance (1..7) up to its offset44-codeable equivalent via <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.RoundUpTinyTable" />.</summary>
	private static int RoundUpTiny(int dist)
	{
		return RoundUpTinyTable[dist];
	}

	/// <summary>
	/// Reference Optimal3 (level-7) parse selection, mirroring <c>level-7 optimal parse</c>
	/// Run up to three greedy pre-passes — <b>mml=4</b> (always), <b>mml=3</b> and <b>mml=8</b>
	/// (both active at level 7) — via <c>greedy optimal pre-pass</c>, then ONE DP pass seeded
	/// from the best greedy's histogram (with the seed decay). The FINAL parse is the
	/// candidate with the smallest <b>real entropy-coded emit size</b> — NOT the integer cost proxy. For a periodic/text block the DP wins;
	/// for many small JSON/text files the mml=8 greedy wins outright (its emit is byte-identical to the reference).
	/// Every candidate is a valid parse, so selection can never yield a non-round-trippable block.
	/// </summary>
	private static List<Command> ParseOptimal(ReadOnlySpan<byte> data, int[] head, int[] prev, int startPos, int litStart0, int matchLimit, int matchStartLimit, int chunkStart, int chunkEnd, bool withSeed, bool useHuffmanArrays)
	{
		List<Command> list = Greedy(data, 4, 0);
		if (list.Count == 0)
		{
			return list;
		}
		List<Command> list2 = list;
		int num = RealEmit(data, list);
		List<Command>[] array = new List<Command>[2]
		{
			Greedy(data, 3, 3),
			Greedy(data, 8, 0)
		};
		foreach (List<Command> list3 in array)
		{
			int num2 = RealEmit(data, list3);
			if (num2 < num)
			{
				num = num2;
				list2 = list3;
			}
		}
		List<Command> commands = list2;
		int[] dpHead = new int[131072];
		int[] dpPrev = new int[data.Length];
		int num3 = LitModeForParse(data, commands, chunkEnd, useHuffmanArrays);
		int[] array2 = Tally(commands, data, startPos);
		DecaySeedPassinfo(array2);
		KrakenOptimalCost.CodeCosts cc = KrakenOptimalCost.BuildFromPassinfo(array2, num3);
		int[] adaptPassinfo = (UseWindowedParse ? array2 : null);
		List<Command> list4 = ForwardDp(data, dpHead, dpPrev, startPos, matchLimit, matchStartLimit, cc, adaptPassinfo, num3);
		if (RealEmit(data, list4) < num)
		{
			list2 = list4;
		}
		return list2;
		List<Command> Greedy(ReadOnlySpan<byte> d, int mml, int hashBytes)
		{
			int greedyMmlOverride = GreedyMmlOverride;
			int greedyHashBytesOverride = GreedyHashBytesOverride;
			bool useExactGreedy = UseExactGreedy;
			GreedyMmlOverride = mml;
			GreedyHashBytesOverride = hashBytes;
			UseExactGreedy = true;
			try
			{
				int[] array3 = new int[131072];
				array3.AsSpan().Fill(-1);
				int[] array4 = new int[d.Length];
				array4.AsSpan().Fill(-1);
				for (int j = chunkStart; j < startPos; j++)
				{
					Insert(d, j, array3, array4);
				}
				return Parse(d, array3, array4, startPos, litStart0, matchLimit, matchStartLimit);
			}
			finally
			{
				GreedyMmlOverride = greedyMmlOverride;
				GreedyHashBytesOverride = greedyHashBytesOverride;
				UseExactGreedy = useExactGreedy;
			}
		}
		int RealEmit(ReadOnlySpan<byte> d, List<Command> cand)
		{
			if (cand.Count == 0)
			{
				return int.MaxValue;
			}
			return EmitChunkFromCommands(d, cand, chunkStart, chunkEnd, withSeed, useHuffmanArrays)?.Length ?? int.MaxValue;
		}
	}

	internal static void DiagDpFromSeed(byte[] data, int[] seedLitStart, int[] seedLitLen, int[] seedDist, int[] seedMatch, int[] seedIdx, out int[] dpLitStart, out int[] dpLitLen, out int[] dpDist, out int[] dpMatch, out int[] dpIdx, out long dpCost, out long seedCost)
	{
		int num = data.Length;
		int matchLimit = num - 8;
		int matchStartLimit = num - 16;
		int num2 = 8;
		int chunkEnd = num;
		List<Command> list = new List<Command>(seedLitStart.Length);
		for (int i = 0; i < seedLitStart.Length; i++)
		{
			list.Add(new Command(seedLitStart[i], seedLitLen[i], seedDist[i], seedMatch[i], seedIdx[i]));
		}
		int num3 = LitModeForParse(data, list, chunkEnd, useHuffmanArrays: true);
		int[] array = Tally(list, data, num2);
		if (UseSeedDecay)
		{
			DecaySeedPassinfo(array);
		}
		KrakenOptimalCost.CodeCosts cc = KrakenOptimalCost.BuildFromPassinfo(array, num3);
		int[] dpHead = new int[131072];
		int[] dpPrev = new int[num];
		DiagArrCapture = Array.Empty<(int, int, int, int, int, int, int, int)>();
		int[] adaptPassinfo = (UseWindowedParse ? array : null);
		List<Command> list2 = ForwardDp(data, dpHead, dpPrev, num2, matchLimit, matchStartLimit, cc, adaptPassinfo, num3);
		(int, int, int, int, int, int, int, int)[] diagArrCapture = DiagArrCapture;
		DiagArrCapture = null;
		if (diagArrCapture != null && diagArrCapture.Length != 0)
		{
			StringBuilder stringBuilder = new StringBuilder();
			long num4 = 0L;
			int r = 8;
			int r2 = 8;
			int r3 = 8;
			bool flag = false;
			for (int j = 0; j < list.Count; j++)
			{
				Command command = list[j];
				int lo = ((r < 8) ? 8 : r);
				num4 += KrakenOptimalCost.CostLiterals(data, command.LitStart, command.LitLen, lo, cc);
				int litField = ((command.LitLen < 3) ? command.LitLen : 3);
				if (command.LitLen > 2)
				{
					num4 += KrakenOptimalCost.CostLen(cc, command.LitLen - 3);
				}
				num4 = ((command.OffsIndex >= 3) ? (num4 + (KrakenOptimalCost.CostOffset(command.Distance, cc) + KrakenOptimalCost.CostNormalMatch(cc, litField, command.MatchLen))) : (num4 + KrakenOptimalCost.CostLoMatch(cc, litField, command.MatchLen, command.OffsIndex)));
				UpdateRecent(ref r, ref r2, ref r3, command.OffsIndex, command.Distance);
				int num5 = command.LitStart + command.LitLen + command.MatchLen;
				int num6 = ((num5 >= 0 && num5 < diagArrCapture.Length) ? diagArrCapture[num5].Item1 : int.MaxValue);
				if (num6 == int.MaxValue || num6 > num4)
				{
					StringBuilder stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder3 = stringBuilder2;
					StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(65, 3, stringBuilder2);
					handler.AppendLiteral("  REACH-GAP at seed cmd#");
					handler.AppendFormatted(j);
					handler.AppendLiteral(" (endPos=");
					handler.AppendFormatted(num5);
					handler.AppendLiteral("): refCumCost=");
					handler.AppendFormatted(num4);
					handler.AppendLiteral(" arr[endPos].cost=");
					stringBuilder3.Append(ref handler).Append((num6 == int.MaxValue) ? "UNREACHED" : num6.ToString()).Append((num6 != int.MaxValue) ? $" (delta {num6 - num4}, DP costlier)" : "")
						.Append('\n');
					(int, int, int, int, int, int, int, int) tuple = ((num5 < diagArrCapture.Length) ? diagArrCapture[num5] : default((int, int, int, int, int, int, int, int)));
					stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder4 = stringBuilder2;
					handler = new StringBuilder.AppendInterpolatedStringHandler(35, 5, stringBuilder2);
					handler.AppendLiteral("    ref cmd: ls=");
					handler.AppendFormatted(command.LitStart);
					handler.AppendLiteral(" ll=");
					handler.AppendFormatted(command.LitLen);
					handler.AppendLiteral(" ml=");
					handler.AppendFormatted(command.MatchLen);
					handler.AppendLiteral(" dist=");
					handler.AppendFormatted(command.Distance);
					handler.AppendLiteral(" oi=");
					handler.AppendFormatted(command.OffsIndex);
					handler.AppendLiteral("\n");
					stringBuilder4.Append(ref handler);
					stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder5 = stringBuilder2;
					handler = new StringBuilder.AppendInterpolatedStringHandler(61, 8, stringBuilder2);
					handler.AppendLiteral("    arr[");
					handler.AppendFormatted(num5);
					handler.AppendLiteral("] (if reached): src=");
					handler.AppendFormatted(tuple.Item2);
					handler.AppendLiteral(" ml=");
					handler.AppendFormatted(tuple.Item3);
					handler.AppendLiteral(" idx=");
					handler.AppendFormatted(tuple.Item4);
					handler.AppendLiteral(" dist=");
					handler.AppendFormatted(tuple.Item5);
					handler.AppendLiteral(" lrl=");
					handler.AppendFormatted(tuple.Item6);
					handler.AppendLiteral(" contMl=");
					handler.AppendFormatted(tuple.Item7);
					handler.AppendLiteral(" r0=");
					handler.AppendFormatted(tuple.Item8);
					handler.AppendLiteral("\n");
					stringBuilder5.Append(ref handler);
					flag = true;
					break;
				}
			}
			if (!flag)
			{
				stringBuilder.Append("  NO REACH-GAP: every reference command end-position is reached at cost <= the reference cumulative.\n  => the reference parse cost is reachable in arr[]; the divergence is backward-trace/rep-state selection, not reachability.\n");
			}
			DiagReachReport = stringBuilder.ToString();
		}
		dpLitStart = new int[list2.Count];
		dpLitLen = new int[list2.Count];
		dpDist = new int[list2.Count];
		dpMatch = new int[list2.Count];
		dpIdx = new int[list2.Count];
		for (int k = 0; k < list2.Count; k++)
		{
			dpLitStart[k] = list2[k].LitStart;
			dpLitLen[k] = list2[k].LitLen;
			dpDist[k] = list2[k].Distance;
			dpMatch[k] = list2[k].MatchLen;
			dpIdx[k] = list2[k].OffsIndex;
		}
		dpCost = CostOfParse(list2, data, num2, chunkEnd, cc);
		seedCost = CostOfParse(list, data, num2, chunkEnd, cc);
	}

	internal static void DiagDpFromExternalPassinfo(byte[] data, int[] referencePassinfo, int litMode, out int[] dpLitLen, out int[] dpDist, out int[] dpMatch, out int[] dpIdx)
	{
		int num = data.Length;
		int matchLimit = num - 8;
		int matchStartLimit = num - 16;
		int startPos = 8;
		int[] array = new int[1537];
		Array.Copy(referencePassinfo, array, Math.Min(referencePassinfo.Length, 1537));
		KrakenOptimalCost.CodeCosts cc = ((DiagExternalCodeCostBlob != null) ? KrakenOptimalCost.FromRawBlob(DiagExternalCodeCostBlob) : KrakenOptimalCost.BuildFromPassinfo(array, litMode));
		int[] dpHead = new int[131072];
		int[] dpPrev = new int[num];
		List<Command> list = ForwardDp(data, dpHead, dpPrev, startPos, matchLimit, matchStartLimit, cc);
		dpLitLen = new int[list.Count];
		dpDist = new int[list.Count];
		dpMatch = new int[list.Count];
		dpIdx = new int[list.Count];
		for (int i = 0; i < list.Count; i++)
		{
			dpLitLen[i] = list[i].LitLen;
			dpDist[i] = list[i].Distance;
			dpMatch[i] = list[i].MatchLen;
			dpIdx[i] = list[i].OffsIndex;
		}
	}

	internal static void DiagOnePassFromGreedy(byte[] data, int mml, int hashBytes, out int[] gLitLen, out int[] gDist, out int[] gMatch, out int[] gIdx, out int[] dpLitLen, out int[] dpDist, out int[] dpMatch, out int[] dpIdx)
	{
		int greedyMmlOverride = GreedyMmlOverride;
		int greedyHashBytesOverride = GreedyHashBytesOverride;
		bool useExactGreedy = UseExactGreedy;
		GreedyMmlOverride = mml;
		GreedyHashBytesOverride = hashBytes;
		UseExactGreedy = true;
		try
		{
			int num = data.Length;
			int matchLimit = num - 8;
			int matchStartLimit = num - 16;
			int num2 = 8;
			int[] array = new int[131072];
			array.AsSpan().Fill(-1);
			int[] array2 = new int[num];
			array2.AsSpan().Fill(-1);
			for (int i = 0; i < num2; i++)
			{
				Insert(data, i, array, array2);
			}
			List<Command> list = Parse(data, array, array2, num2, num2, matchLimit, matchStartLimit);
			int[] array3 = Tally(list, data, num2);
			if (UseSeedDecay)
			{
				DecaySeedPassinfo(array3);
			}
			KrakenOptimalCost.CodeCosts cc = KrakenOptimalCost.BuildFromPassinfo(array3, LitModeForParse(data, list, num, useHuffmanArrays: true));
			int[] array4 = new int[131072];
			array4.AsSpan().Fill(-1);
			int[] dpPrev = new int[num];
			List<Command> list2 = ForwardDp(data, array4, dpPrev, num2, matchLimit, matchStartLimit, cc);
			gLitLen = new int[list.Count];
			gDist = new int[list.Count];
			gMatch = new int[list.Count];
			gIdx = new int[list.Count];
			for (int j = 0; j < list.Count; j++)
			{
				gLitLen[j] = list[j].LitLen;
				gDist[j] = list[j].Distance;
				gMatch[j] = list[j].MatchLen;
				gIdx[j] = list[j].OffsIndex;
			}
			dpLitLen = new int[list2.Count];
			dpDist = new int[list2.Count];
			dpMatch = new int[list2.Count];
			dpIdx = new int[list2.Count];
			for (int k = 0; k < list2.Count; k++)
			{
				dpLitLen[k] = list2[k].LitLen;
				dpDist[k] = list2[k].Distance;
				dpMatch[k] = list2[k].MatchLen;
				dpIdx[k] = list2[k].OffsIndex;
			}
		}
		finally
		{
			GreedyMmlOverride = greedyMmlOverride;
			GreedyHashBytesOverride = greedyHashBytesOverride;
			UseExactGreedy = useExactGreedy;
		}
	}

	internal static int[] DiagSeedPassinfo(byte[] data, int mml, int hashBytes, int inc, bool includeTail)
	{
		int greedyMmlOverride = GreedyMmlOverride;
		int greedyHashBytesOverride = GreedyHashBytesOverride;
		bool useExactGreedy = UseExactGreedy;
		GreedyMmlOverride = mml;
		GreedyHashBytesOverride = hashBytes;
		UseExactGreedy = true;
		try
		{
			int num = data.Length;
			int matchLimit = num - 8;
			int matchStartLimit = num - 16;
			int num2 = 8;
			int[] array = new int[131072];
			array.AsSpan().Fill(-1);
			int[] array2 = new int[num];
			array2.AsSpan().Fill(-1);
			for (int i = 0; i < num2; i++)
			{
				Insert(data, i, array, array2);
			}
			return Tally(Parse(data, array, array2, num2, num2, matchLimit, matchStartLimit), data, num2, inc, includeTail ? num : (-1));
		}
		finally
		{
			GreedyMmlOverride = greedyMmlOverride;
			GreedyHashBytesOverride = greedyHashBytesOverride;
			UseExactGreedy = useExactGreedy;
		}
	}

	internal static void DiagGreedyCodeCost(byte[] data, int mml, int hashBytes, int litModeOverride, out int[] packet, out int[] length, out int[] lit, out int[] offbucket, out int litModeUsed, out int[] passinfoOut)
	{
		int greedyMmlOverride = GreedyMmlOverride;
		int greedyHashBytesOverride = GreedyHashBytesOverride;
		bool useExactGreedy = UseExactGreedy;
		GreedyMmlOverride = mml;
		GreedyHashBytesOverride = hashBytes;
		UseExactGreedy = true;
		try
		{
			int num = data.Length;
			int matchLimit = num - 8;
			int matchStartLimit = num - 16;
			int num2 = 8;
			int[] array = new int[131072];
			array.AsSpan().Fill(-1);
			int[] array2 = new int[num];
			array2.AsSpan().Fill(-1);
			for (int i = 0; i < num2; i++)
			{
				Insert(data, i, array, array2);
			}
			List<Command> commands = Parse(data, array, array2, num2, num2, matchLimit, matchStartLimit);
			int[] array3 = Tally(commands, data, num2);
			KrakenOptimalCost.CodeCosts codeCosts = KrakenOptimalCost.BuildFromPassinfo(litMode: litModeUsed = ((litModeOverride >= 0) ? litModeOverride : LitModeForParse(data, commands, num, useHuffmanArrays: true)), passinfo: array3);
			packet = (int[])codeCosts.Packet.Clone();
			length = (int[])codeCosts.Length.Clone();
			lit = (int[])codeCosts.Lit.Clone();
			offbucket = (int[])codeCosts.OffsetBucket.Clone();
			passinfoOut = array3;
		}
		finally
		{
			GreedyMmlOverride = greedyMmlOverride;
			GreedyHashBytesOverride = greedyHashBytesOverride;
			UseExactGreedy = useExactGreedy;
		}
	}

	/// <summary>
	/// Diagnostic: run the value-model greedy (<see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Parse(System.ReadOnlySpan{System.Byte},System.Int32[],System.Int32[],System.Int32,System.Int32,System.Int32,System.Int32)" />) as one of the pre-passes at a chosen
	/// minimum match length (<paramref name="mml" />) and hash width (<paramref name="hashBytes" />, 3 or 4),
	/// seeded exactly like a actual chunk (8 COPY_64 seed positions indexed before parsing). the level-7
	/// encoder runs this at mml ∈ {4, 3, 8}; the mml=3 pass is the only producer of length-3 NEW matches.
	/// Returns the parse so the fixpoint diagnostic can test whether a faithful mml=3 greedy
	/// reproduces the exact text parse (the make-or-break for the float emit-size selection).
	/// </summary>
	internal static void DiagGreedyParse(byte[] data, int mml, int hashBytes, out int[] litStart, out int[] litLen, out int[] dist, out int[] match, out int[] idx)
	{
		int greedyMmlOverride = GreedyMmlOverride;
		int greedyHashBytesOverride = GreedyHashBytesOverride;
		bool useExactGreedy = UseExactGreedy;
		GreedyMmlOverride = mml;
		GreedyHashBytesOverride = hashBytes;
		UseExactGreedy = true;
		try
		{
			int num = data.Length;
			int matchLimit = num - 8;
			int matchStartLimit = num - 16;
			int num2 = 8;
			int[] array = new int[131072];
			array.AsSpan().Fill(-1);
			int[] array2 = new int[num];
			array2.AsSpan().Fill(-1);
			for (int i = 0; i < num2; i++)
			{
				Insert(data, i, array, array2);
			}
			List<Command> list = Parse(data, array, array2, num2, num2, matchLimit, matchStartLimit);
			litStart = new int[list.Count];
			litLen = new int[list.Count];
			dist = new int[list.Count];
			match = new int[list.Count];
			idx = new int[list.Count];
			for (int j = 0; j < list.Count; j++)
			{
				litStart[j] = list[j].LitStart;
				litLen[j] = list[j].LitLen;
				dist[j] = list[j].Distance;
				match[j] = list[j].MatchLen;
				idx[j] = list[j].OffsIndex;
			}
		}
		finally
		{
			GreedyMmlOverride = greedyMmlOverride;
			GreedyHashBytesOverride = greedyHashBytesOverride;
			UseExactGreedy = useExactGreedy;
		}
	}

	/// <summary>
	/// Diagnostic:replays the seed parse left-to-right, building the CTMF exactly as <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.ForwardDp(System.ReadOnlySpan{System.Byte},System.Int32[],System.Int32[],System.Int32,System.Int32,System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts,System.Int32[],System.Int32)" />
	/// does (same seed-index, same scan-then-insert order), and at each command's match-start position reports
	/// the CTMF's emitted (len,dist) pairs and whether the chosen distance was among them. Pinpoints whether
	/// a far new match (e.g. cmd-34 dist-950) is a genuine 16-way eviction or a implementation bug.
	/// </summary>
	internal static void DiagFinderAlongSeed(byte[] data, int[] seedLitStart, int[] seedLitLen, int[] seedDist, int[] seedMatch, int[] seedIdx, Action<string> log)
	{
		int num = data.Length;
		int num2 = num - 8;
		int num3 = 8;
		int num4 = ((data.Length < 1) ? 1 : data.Length);
		int i;
		for (i = 0; i < 24 && 1 << i < num4; i++)
		{
		}
		int bits = ((i < 18) ? 18 : i);
		if (CtmfBitsOverride > 0)
		{
			bits = CtmfBitsOverride;
		}
		int[] array = new int[131072];
		array.AsSpan().Fill(-1);
		int[] prev = new int[num];
		Ctmf ctmf = new Ctmf(bits, data.Length);
		int num5 = ((num3 > 8) ? (num3 - 8) : 0);
		int j = num5;
		Span<int> outLen = stackalloc int[40];
		Span<int> outDist = stackalloc int[40];
		for (int k = 0; k < seedLitStart.Length && seedIdx[k] >= 0; k++)
		{
			int num6 = seedLitStart[k] + seedLitLen[k];
			if (num6 > num2)
			{
				break;
			}
			for (num5 = InsertUpTo(data, array, prev, num5, num6); j < num5; j++)
			{
				ctmf.Insert(data, j);
			}
			int num7 = ctmf.Query(data, num6, num2, outLen, outDist);
			bool flag = seedIdx[k] == 3;
			bool value = false;
			int value2 = 0;
			for (int l = 0; l < num7; l++)
			{
				if (outDist[l] == seedDist[k])
				{
					value = true;
					value2 = outLen[l];
				}
			}
			if (flag)
			{
				StringBuilder stringBuilder = new StringBuilder();
				for (int m = 0; m < num7; m++)
				{
					StringBuilder stringBuilder2 = stringBuilder;
					StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(3, 2, stringBuilder2);
					handler.AppendLiteral("(");
					handler.AppendFormatted(outLen[m]);
					handler.AppendLiteral(",");
					handler.AppendFormatted(outDist[m]);
					handler.AppendLiteral(")");
					stringBuilder2.Append(ref handler);
				}
				int num8 = num6 - seedDist[k];
				int value3 = ((num8 >= 0) ? MatchLength(data, num8, num6, num2) : (-1));
				log($"cmd{k} pos={num6} REF new dist={seedDist[k]} mat={seedMatch[k]} | CTMF[{num7}]={stringBuilder} | offered={value}({value2}) realLenAtDist={value3}");
			}
		}
	}

	private static bool SameParse(List<Command> a, List<Command> b)
	{
		if (a.Count != b.Count)
		{
			return false;
		}
		for (int i = 0; i < a.Count; i++)
		{
			if (a[i].LitStart != b[i].LitStart || a[i].LitLen != b[i].LitLen || a[i].Distance != b[i].Distance || a[i].MatchLen != b[i].MatchLen || a[i].OffsIndex != b[i].OffsIndex)
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// Total cost (bits×32) of a command list under <paramref name="cc" />, accumulated exactly as the
	/// forward DP accumulates an arrival (literal run + LRL escape + rep/new match) plus the trailing
	/// literal run to <paramref name="chunkEnd" />. Used to pick the cheapest parse across passes.
	/// </summary>
	private static long CostOfParse(List<Command> cmds, ReadOnlySpan<byte> data, int start, int chunkEnd, KrakenOptimalCost.CodeCosts cc)
	{
		long num = 0L;
		int r = 8;
		int r2 = 8;
		int r3 = 8;
		int num2 = start;
		foreach (Command cmd in cmds)
		{
			int lo = ((r < 8) ? 8 : r);
			num += KrakenOptimalCost.CostLiterals(data, cmd.LitStart, cmd.LitLen, lo, cc);
			int litField = ((cmd.LitLen < 3) ? cmd.LitLen : 3);
			if (cmd.LitLen > 2)
			{
				num += KrakenOptimalCost.CostLen(cc, cmd.LitLen - 3);
			}
			num = ((cmd.OffsIndex >= 3) ? (num + (KrakenOptimalCost.CostOffset(cmd.Distance, cc) + KrakenOptimalCost.CostNormalMatch(cc, litField, cmd.MatchLen))) : (num + KrakenOptimalCost.CostLoMatch(cc, litField, cmd.MatchLen, cmd.OffsIndex)));
			UpdateRecent(ref r, ref r2, ref r3, cmd.OffsIndex, cmd.Distance);
			num2 = cmd.LitStart + cmd.LitLen + cmd.MatchLen;
		}
		int lo2 = ((r < 8) ? 8 : r);
		return num + KrakenOptimalCost.CostLiterals(data, num2, chunkEnd - num2, lo2, cc);
	}

	/// <summary>
	/// <c>passinfo update</c>: tally a command list into a 0x601-int pass
	/// histogram (+2 per event). Tracks the three recent offsets (init distance 8) with the decoder's
	/// move-to-front so rep indices are scored correctly. litMode is raw (1); both literal histograms are
	/// filled (build picks one). Layout: [0..0xFF] lit-raw, [0x100..0x1FF] lit-sub, [0x200..0x2FF] packet,
	/// [0x300..0x3FF] length (shared LRL+ML escapes), [0x400] offset_alt_modulo (0), [0x401..0x500]
	/// offset-bucket, [0x501..0x600] offset-alt (0).
	/// </summary>
	/// <summary>
	/// <c>seed passinfo decay</c> — the per-region histogram decay applied to the seed
	/// passinfo BEFORE the single DP pass builds its codecost tables. validated against
	/// <c>algorithm</c> (the implementation), which calls <c>algorithm</c> on each
	/// 256-entry region: <c>c' = (c &gt;&gt; 4) + 1</c>. This strongly smooths the greedy's raw Tally toward
	/// uniform, flattening the cost model so the level-7 DP behaves greedy-like (it stops detouring to a
	/// nearer offset just to save a single offset bit — the cmd-20 lit=1/dist=551 vs lit=2/dist=496 flip).
	/// Applies to literals-raw (0), literals-sub (0x100), packet (0x200), length (0x300), offset-bucket
	/// (0x401), and offset-alt (0x501, only when the modulo at 0x400 &gt; 1). The scalar at int-index 0x400
	/// (offset_alt_modulo) is preserved. This is the standalone-chunk path (no carried encoder state,
	/// <c>carried state is absent</c>); the carried-merge path is the multi-chunk variant.
	/// </summary>
	internal static void DecaySeedPassinfo(int[] pi)
	{
		Decay(pi, 0);
		Decay(pi, 256);
		Decay(pi, 1025);
		if (pi[1024] > 1)
		{
			Decay(pi, 1281);
		}
		Decay(pi, 512);
		Decay(pi, 768);
		static void Decay(int[] p, int start)
		{
			for (int i = 0; i < 256; i++)
			{
				p[start + i] = (p[start + i] >> 4) + 1;
			}
		}
	}

	private static int[] Tally(List<Command> commands, ReadOnlySpan<byte> data, int start, int inc = 2, int chunkEnd = -1)
	{
		int[] array = new int[1537];
		int r = 8;
		int r2 = 8;
		int r3 = 8;
		int num = start;
		foreach (Command command in commands)
		{
			int litStart = command.LitStart;
			for (int i = 0; i < command.LitLen; i++)
			{
				int num2 = data[litStart + i];
				array[num2] += inc;
				int num3 = litStart + i - r;
				int num4 = ((num3 >= 0) ? data[num3] : 0);
				array[256 + ((num2 - num4) & 0xFF)] += inc;
			}
			if (command.LitLen >= 3)
			{
				array[768 + Math.Min(command.LitLen - 3, 255)] += inc;
			}
			int num5 = ((command.LitLen < 3) ? command.LitLen : 3);
			int matchLen = command.MatchLen;
			bool flag = command.OffsIndex < 3;
			int num6 = ((matchLen < 17) ? (matchLen - 2) : 15);
			int num7 = (flag ? (command.OffsIndex * 64) : 192);
			array[512 + num5 + num6 * 4 + num7] += inc;
			if (matchLen >= 17)
			{
				array[768 + Math.Min(matchLen - 17, 255)] += inc;
			}
			if (!flag)
			{
				array[1025 + KrakenOptimalCost.OffsetBucketByte(command.Distance, out var _)] += inc;
			}
			UpdateRecent(ref r, ref r2, ref r3, command.OffsIndex, command.Distance);
			num = command.LitStart + command.LitLen + command.MatchLen;
		}
		if (chunkEnd > num)
		{
			for (int j = num; j < chunkEnd; j++)
			{
				int num8 = data[j];
				array[num8] += inc;
				int num9 = j - r;
				int num10 = ((num9 >= 0) ? data[num9] : 0);
				array[256 + ((num8 - num10) & 0xFF)] += inc;
			}
		}
		return array;
	}

	/// <summary>
	/// One forward-DP pass (the single-TLL algorithm). Fills the arrival table left to right
	/// with the anchor/accumulated-literal bookkeeping, the lrl loop (0..3, the 4th = the full run from
	/// the anchor), rep relaxation (longest-first gating) and new-match relaxation (new-minimum-base gate,
	/// length-desc candidates, break once a candidate is no longer than the longest rep), then backtraces
	/// the cheapest reachable arrival plus its trailing literals into a command list.
	/// </summary>
	private static List<Command> ForwardDp(ReadOnlySpan<byte> data, int[] dpHead, int[] dpPrev, int startPos, int matchLimit, int matchStartLimit, KrakenOptimalCost.CodeCosts cc, int[]? adaptPassinfo = null, int adaptLitMode = 1)
	{
		int greedyHashBytesOverride = GreedyHashBytesOverride;
		if (UseDpMml3)
		{
			GreedyHashBytesOverride = 3;
		}
		try
		{
			int chunkEnd = matchLimit + 8;
			Arrival[] array = new Arrival[matchLimit + 1];
			for (int i = 0; i <= matchLimit; i++)
			{
				array[i].Cost = int.MaxValue;
			}
			array[startPos] = new Arrival
			{
				Cost = 0,
				R0 = 8,
				R1 = 8,
				R2 = 8,
				Src = startPos,
				Idx = -1
			};
			dpHead.AsSpan().Fill(-1);
			int num = ((startPos > 8) ? (startPos - 8) : 0);
			int num2 = ((data.Length < 1) ? 1 : data.Length);
			int j;
			for (j = 0; j < 24 && 1 << j < num2; j++)
			{
			}
			int bits = ((j < 18) ? 18 : j);
			if (CtmfBitsOverride > 0)
			{
				bits = CtmfBitsOverride;
			}
			Ctmf ctmf = (UseCtmfFinder ? new Ctmf(bits, data.Length) : null);
			int k = num;
			Span<int> cml = stackalloc int[8];
			Span<int> cdist = stackalloc int[8];
			Span<int> coff = stackalloc int[8];
			bool flag = UseWindowedParse && adaptPassinfo != null;
			int num3 = startPos;
			List<Command> list = (flag ? new List<Command>() : null);
			int num4 = startPos;
			long num5 = 0L;
			do
			{
				int num6 = matchStartLimit;
				if (flag)
				{
					num4 = num3;
					num5 = 0L;
					int num7 = num3 + 256;
					if (num7 > matchStartLimit)
					{
						num7 = matchStartLimit;
					}
					if (matchStartLimit - 16 <= num7)
					{
						num7 = matchStartLimit;
					}
					DpMaxReached = num7;
					num6 = num3 + 4096;
					if (num6 > matchStartLimit)
					{
						num6 = matchStartLimit;
					}
					if (matchStartLimit - 16 <= num6)
					{
						num6 = matchStartLimit;
					}
				}
				int num8 = -1;
				for (int l = num3; l <= matchStartLimit; l++)
				{
					if (flag)
					{
						if (DpMaxReached > num6)
						{
							num8 = DpMaxReached;
							break;
						}
						if (l >= matchStartLimit)
						{
							num8 = -1;
							break;
						}
					}
					if (l > num3)
					{
						int lo = ((array[num4].R0 < 8) ? 8 : array[num4].R0);
						num5 += KrakenOptimalCost.CostAddLiteral(data, l - 1, lo, cc);
						if (array[l].Cost != int.MaxValue)
						{
							int num9 = l - num4;
							long num10 = (long)array[num4].Cost + (long)((num9 > 2) ? KrakenOptimalCost.CostLen(cc, num9 - 3) : 0) + num5;
							if (array[l].Cost < num10)
							{
								num4 = l;
								num5 = 0L;
								if (flag && DpMaxReached <= l)
								{
									num8 = l;
									break;
								}
							}
						}
					}
					num = InsertUpTo(data, dpHead, dpPrev, num, l);
					if (ctmf != null)
					{
						for (; k < num; k++)
						{
							ctmf.Insert(data, k);
						}
					}
					int num11 = FindCandidates(data, l, dpHead, dpPrev, matchLimit, cc, cml, cdist, coff, ctmf);
					long num12 = long.MaxValue;
					for (int m = 0; m <= 3; m++)
					{
						int num13 = m;
						if (m == 3 && 3 < l - num4)
						{
							num13 = l - num4;
						}
						int num14 = l - num13;
						if (num14 < startPos || array[num14].Cost == int.MaxValue)
						{
							continue;
						}
						int lo2 = ((array[num14].R0 < 8) ? 8 : array[num14].R0);
						long num15 = ((num13 == l - num4) ? num5 : KrakenOptimalCost.CostLiterals(data, l - num13, num13, lo2, cc));
						long num16 = array[num14].Cost + num15;
						int litField = ((num13 < 3) ? num13 : 3);
						if (num13 > 2)
						{
							num16 += KrakenOptimalCost.CostLen(cc, num13 - 3);
						}
						int r = array[num14].R0;
						int r2 = array[num14].R1;
						int r3 = array[num14].R2;
						int num17 = 0;
						for (int n = 0; n < 3; n++)
						{
							int num18 = n switch
							{
								1 => r2, 
								0 => r, 
								_ => r3, 
							};
							if (num18 < 8)
							{
								continue;
							}
							int num19 = RepMatchLength(data, l, num18, matchLimit);
							if (num19 < 2 || num19 <= num17)
							{
								continue;
							}
							num17 = num19;
							int num20;
							int num21;
							switch (n)
							{
							case 0:
								num20 = r2;
								num21 = r3;
								break;
							case 1:
								num20 = r;
								num21 = r3;
								break;
							default:
								num20 = r;
								num21 = r2;
								break;
							}
							if (UseSublenGate)
							{
								RelaxRep(array, l, num19, n, num16, litField, num18, num20, num21, num13, num14, cc);
								if (SublenFillAllowed(3, 125, num19))
								{
									for (int num22 = 2; num22 < num19; num22++)
									{
										RelaxRep(array, l, num22, n, num16, litField, num18, num20, num21, num13, num14, cc);
									}
								}
							}
							else
							{
								int num23 = ((num19 < 8194) ? num19 : 8194);
								for (int num24 = 2; num24 <= num23; num24++)
								{
									RelaxRep(array, l, num24, n, num16, litField, num18, num20, num21, num13, num14, cc);
								}
								if (num19 > num23)
								{
									RelaxRep(array, l, num19, n, num16, litField, num18, num20, num21, num13, num14, cc);
								}
							}
							if (UseFaef0)
							{
								long costAtE = num16 + KrakenOptimalCost.CostLoMatch(cc, litField, num19, n);
								Faef0(array, data, l + num19, costAtE, num19, num13, n, num18, num14, num18, num20, num21, num18, matchLimit, cc);
							}
						}
						if (!(UseTieBreakLast ? (num16 <= num12) : (num16 < num12)))
						{
							continue;
						}
						num12 = num16;
						for (int num25 = 0; num25 < num11; num25++)
						{
							int num26 = cml[num25];
							if (num26 <= num17)
							{
								break;
							}
							int num27 = cdist[num25];
							if (num27 == r || num27 == r2 || num27 == r3)
							{
								continue;
							}
							long num28 = num16 + coff[num25];
							if (UseSublenGate)
							{
								int num29 = (UseDpMml3 ? 3 : 4);
								int num30 = num29 + 1;
								RelaxNew(array, l, num26, num28, litField, num27, r, r2, num13, num14, cc);
								if (SublenFillAllowed(num30, 128 - num30, num26))
								{
									for (int num31 = num29; num31 < num26; num31++)
									{
										RelaxNew(array, l, num31, num28, litField, num27, r, r2, num13, num14, cc);
									}
								}
							}
							else
							{
								int num32 = (UseDpMml3 ? 3 : 4);
								int num33 = ((num26 < num32 + 8192) ? num26 : (num32 + 8192));
								for (int num34 = num32; num34 <= num33; num34++)
								{
									RelaxNew(array, l, num34, num28, litField, num27, r, r2, num13, num14, cc);
								}
								if (num26 > num33)
								{
									RelaxNew(array, l, num26, num28, litField, num27, r, r2, num13, num14, cc);
								}
								else if (num26 < num32)
								{
									RelaxNew(array, l, num26, num28, litField, num27, r, r2, num13, num14, cc);
								}
							}
							if (UseFaef0)
							{
								long costAtE2 = num28 + KrakenOptimalCost.CostNormalMatch(cc, litField, num26);
								Faef0(array, data, l + num26, costAtE2, num26, num13, 3, num27, num14, num27, r, r2, num27, matchLimit, cc);
							}
						}
					}
				}
				if (!flag)
				{
					if (DiagArrCapture != null)
					{
						(int, int, int, int, int, int, int, int)[] array2 = new (int, int, int, int, int, int, int, int)[matchLimit + 1];
						for (int num35 = 0; num35 <= matchLimit; num35++)
						{
							Arrival arrival = array[num35];
							array2[num35] = (arrival.Cost, arrival.Src, arrival.Ml, arrival.Idx, arrival.Dist, arrival.Lrl, arrival.ContMl, arrival.R0);
						}
						DiagArrCapture = array2;
					}
					if (DpProbeHi > 0)
					{
						for (int num36 = DpProbeLo; num36 <= DpProbeHi && num36 < array.Length; num36++)
						{
							Arrival arrival2 = array[num36];
							if (arrival2.Cost == int.MaxValue)
							{
								Console.WriteLine($"        [dpprobe] arr[{num36}] UNREACHED");
								continue;
							}
							Console.WriteLine($"        [dpprobe] arr[{num36}] cost={arrival2.Cost} src={arrival2.Src} lrl={arrival2.Lrl} ml={arrival2.Ml} idx={arrival2.Idx} dist={arrival2.Dist} reps=({arrival2.R0},{arrival2.R1},{arrival2.R2}) cont(lrl={arrival2.ContLrl},ml={arrival2.ContMl})");
						}
					}
					int num37 = EstarSelect(array, data, startPos, matchLimit, chunkEnd, cc);
					if (num37 < 0)
					{
						return new List<Command>();
					}
					return BackTraceSeg(array, startPos, num37, matchLimit) ?? new List<Command>();
				}
				if (num8 < 0 || num8 >= matchStartLimit)
				{
					int num38 = EstarSelect(array, data, num3, matchLimit, chunkEnd, cc);
					if (num38 > num3)
					{
						List<Command> list2 = BackTraceSeg(array, num3, num38, matchLimit);
						if (list2 != null)
						{
							list.AddRange(list2);
						}
					}
					return list;
				}
				List<Command> list3 = BackTraceSeg(array, num3, num8, matchLimit);
				if (list3 == null)
				{
					return list;
				}
				list.AddRange(list3);
				int[] array3 = Tally(list3, data, num3);
				int num39 = ((adaptPassinfo.Length < array3.Length) ? adaptPassinfo.Length : array3.Length);
				for (int num40 = 0; num40 < num39; num40++)
				{
					adaptPassinfo[num40] += array3[num40];
				}
				cc = KrakenOptimalCost.BuildFromPassinfo(adaptPassinfo, adaptLitMode);
				num3 = num8;
			}
			while (num3 < matchStartLimit);
			int num41 = EstarSelect(array, data, num3, matchLimit, chunkEnd, cc);
			if (num41 > num3)
			{
				List<Command> list4 = BackTraceSeg(array, num3, num41, matchLimit);
				if (list4 != null)
				{
					list.AddRange(list4);
				}
			}
			return list;
		}
		finally
		{
			GreedyHashBytesOverride = greedyHashBytesOverride;
		}
	}

	/// <summary>
	/// Reference E* selection: the cheapest reachable arrival in <c>(from, matchLimit]</c> plus its trailing
	/// literal run to <paramref name="chunkEnd" />. Returns the winning end index, or -1 if none is reachable.
	/// </summary>
	private static int EstarSelect(Arrival[] arr, ReadOnlySpan<byte> data, int from, int matchLimit, int chunkEnd, KrakenOptimalCost.CodeCosts cc)
	{
		int result = -1;
		long num = long.MaxValue;
		for (int i = from + 1; i <= matchLimit; i++)
		{
			if (arr[i].Cost != int.MaxValue)
			{
				int lo = ((arr[i].R0 < 8) ? 8 : arr[i].R0);
				long num2 = (long)arr[i].Cost + (long)KrakenOptimalCost.CostLiterals(data, i, chunkEnd - i, lo, cc);
				if (num2 < num)
				{
					num = num2;
					result = i;
				}
			}
		}
		return result;
	}

	/// <summary>
	/// Backward-traces the arrival chain from <paramref name="to" /> to <paramref name="from" />, emitting one
	/// <see cref="T:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Command" /> per arrival (a two-step bundle emits its free rep0 continuation first). Returns the
	/// commands in forward order, or null if the chain does not cleanly reach <paramref name="from" />.
	/// </summary>
	private static List<Command>? BackTraceSeg(Arrival[] arr, int from, int to, int maxArr)
	{
		List<Command> list = new List<Command>();
		int num = to;
		int num2 = 0;
		while (num != from && num2++ <= maxArr)
		{
			Arrival arrival = arr[num];
			if (arrival.Ml <= 0 || arrival.Src < from || arrival.Src >= num)
			{
				break;
			}
			if (arrival.ContMl > 0)
			{
				int litStart = arrival.Src + arrival.Lrl + arrival.Ml;
				list.Add(new Command(litStart, arrival.ContLrl, arrival.R0, arrival.ContMl, 0));
			}
			list.Add(new Command(arrival.Src, arrival.Lrl, arrival.Dist, arrival.Ml, arrival.Idx));
			num = arrival.Src;
		}
		if (num != from)
		{
			return null;
		}
		list.Reverse();
		return list;
	}

	private static void RelaxRep(Arrival[] arr, int pos, int ml, int loi, long baseCost, int litField, int r0, int r1, int r2, int run, int src, KrakenOptimalCost.CodeCosts cc)
	{
		long num = baseCost + KrakenOptimalCost.CostLoMatch(cc, litField, ml, loi);
		int num2 = pos + ml;
		if (UseWindowedParse && num2 > DpMaxReached)
		{
			DpMaxReached = num2;
		}
		if (DiagWatchDst != 0 && num2 == DiagWatchDst)
		{
			Console.WriteLine($"        [watch REP] dst={num2} pos={pos} src={src} run={run} ml={ml} loi={loi} litField={litField} base={baseCost} mc={KrakenOptimalCost.CostLoMatch(cc, litField, ml, loi)} total={num} cur={((arr[num2].Cost == int.MaxValue) ? (-1) : arr[num2].Cost)} won={(UseTieBreakLast ? (num <= arr[num2].Cost) : (num < arr[num2].Cost))}");
		}
		if (UseTieBreakLast ? (num <= arr[num2].Cost) : (num < arr[num2].Cost))
		{
			arr[num2].Cost = (int)num;
			arr[num2].R0 = r0;
			arr[num2].R1 = r1;
			arr[num2].R2 = r2;
			arr[num2].Ml = ml;
			arr[num2].Lrl = run;
			arr[num2].Src = src;
			arr[num2].Idx = loi;
			arr[num2].Dist = r0;
			arr[num2].ContLrl = 0;
			arr[num2].ContMl = 0;
		}
	}

	private static void RelaxNew(Arrival[] arr, int pos, int ml, long baseWithOff, int litField, int dist, int sr0, int sr1, int run, int src, KrakenOptimalCost.CodeCosts cc)
	{
		long num = baseWithOff + KrakenOptimalCost.CostNormalMatch(cc, litField, ml);
		int num2 = pos + ml;
		if (UseWindowedParse && num2 > DpMaxReached)
		{
			DpMaxReached = num2;
		}
		if (DiagWatchDst != 0 && num2 == DiagWatchDst)
		{
			Console.WriteLine($"        [watch NEW] dst={num2} pos={pos} src={src} run={run} ml={ml} dist={dist} litField={litField} baseWithOff={baseWithOff} mc={KrakenOptimalCost.CostNormalMatch(cc, litField, ml)} total={num} cur={((arr[num2].Cost == int.MaxValue) ? (-1) : arr[num2].Cost)} won={(UseTieBreakLast ? (num <= arr[num2].Cost) : (num < arr[num2].Cost))}");
		}
		if (UseTieBreakLast ? (num <= arr[num2].Cost) : (num < arr[num2].Cost))
		{
			arr[num2].Cost = (int)num;
			arr[num2].R0 = dist;
			arr[num2].R1 = sr0;
			arr[num2].R2 = sr1;
			arr[num2].Ml = ml;
			arr[num2].Lrl = run;
			arr[num2].Src = src;
			arr[num2].Idx = 3;
			arr[num2].Dist = dist;
			arr[num2].ContLrl = 0;
			arr[num2].ContMl = 0;
		}
	}

	/// <summary>
	/// the rep0-continuation pre-relax. Given a primary match ending at <paramref name="s" />
	/// whose post-match front offset is <paramref name="contDist" />, this values the immediate rep0 continuation
	/// — a 3-byte literal/back-extend transition followed by a forward rep0 match at <paramref name="contDist" /> —
	/// and, when the bundled (primary + free continuation) arrival is cheaper, writes it into <c>arr[e2]</c> with
	/// the continuation packet recorded in <see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Arrival.ContLrl" />/<see cref="F:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Arrival.ContMl" />. The
	/// continuation is always rep0, so the post-continuation recents equal the post-primary recents
	/// (<paramref name="pr0" />,<paramref name="pr1" />,<paramref name="pr2" />). This is the "two-step arrival":
	/// it lets a locally non-minimal match still propagate its free rep cascade. validated against
	/// the forward-extend end cap and recent offsets.
	/// </summary>
	private static void Faef0(Arrival[] arr, ReadOnlySpan<byte> data, int s, long costAtE, int primaryMl, int primaryLrl, int primaryIdx, int primaryDist, int src, int pr0, int pr1, int pr2, int contDist, int matchLimit, KrakenOptimalCost.CodeCosts cc)
	{
		if (s + 3 > matchLimit || contDist < 8 || s + 1 - contDist < 0)
		{
			return;
		}
		int num = ((data[s + 2] == data[s + 2 - contDist]) ? ((data[s + 1] != data[s + 1 - contDist]) ? 1 : 2) : 0);
		int num2 = RepMatchLength(data, s + 3, contDist, matchLimit);
		int num3 = 3 - num;
		int num4 = num2 + num;
		if (num4 >= 2)
		{
			long num5 = costAtE + KrakenOptimalCost.CostLiterals(data, s, num3, contDist, cc);
			int litField = num3;
			if (num3 > 2)
			{
				litField = 3;
				num5 += KrakenOptimalCost.CostLen(cc, 0);
			}
			num5 += KrakenOptimalCost.CostLoMatch(cc, litField, num4, 0);
			int num6 = s + num3 + num4;
			if (UseWindowedParse && num6 > DpMaxReached)
			{
				DpMaxReached = num6;
			}
			if (DiagWatchDst != 0 && num6 == DiagWatchDst)
			{
				Console.WriteLine($"        [watch FAEF] e2={num6} s={s} src={src} d={contDist} costAtE={costAtE} backExt={num} fwd={num2} lrl2={num3} ml2={num4} lits={KrakenOptimalCost.CostLiterals(data, s, num3, contDist, cc)} lomatch={KrakenOptimalCost.CostLoMatch(cc, litField, num4, 0)} total={num5} cur={((arr[num6].Cost == int.MaxValue) ? (-1) : arr[num6].Cost)} won={(UseTieBreakLast ? (num5 <= arr[num6].Cost) : (num5 < arr[num6].Cost))}");
			}
			if (UseTieBreakLast ? (num5 <= arr[num6].Cost) : (num5 < arr[num6].Cost))
			{
				arr[num6].Cost = (int)num5;
				arr[num6].R0 = pr0;
				arr[num6].R1 = pr1;
				arr[num6].R2 = pr2;
				arr[num6].Ml = primaryMl;
				arr[num6].Lrl = primaryLrl;
				arr[num6].Src = src;
				arr[num6].Idx = primaryIdx;
				arr[num6].Dist = primaryDist;
				arr[num6].ContLrl = num3;
				arr[num6].ContMl = num4;
			}
		}
	}

	/// <summary>
	/// Collects up to four longest distinct-distance new-offset candidates from the causal hash chain plus
	/// a forced distance-8 (init offset) candidate, sorted by length descending, caching each one's
	/// <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CostOffset(System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts)" />. Approximates the find_all_matches 4-pair table.
	/// </summary>
	private static int FindCandidates(ReadOnlySpan<byte> data, int pos, int[] dpHead, int[] dpPrev, int matchLimit, KrakenOptimalCost.CodeCosts cc, Span<int> cml, Span<int> cdist, Span<int> coff, Ctmf? ctmf)
	{
		int num = 0;
		if (DiagExternalFinder != null)
		{
			int num2 = pos * 8;
			if (num2 + 8 <= DiagExternalFinder.Length)
			{
				int num3 = (UseDpMml3 ? 3 : 4);
				for (int i = 0; i < 4; i++)
				{
					int num4 = DiagExternalFinder[num2 + i * 2];
					int num5 = DiagExternalFinder[num2 + i * 2 + 1];
					if (num4 <= 0)
					{
						break;
					}
					if (num5 < 8)
					{
						if (!UseTinyOffsetRemap)
						{
							continue;
						}
						int num6 = RoundUpTiny(num5);
						if (num6 == 0 || num6 > pos)
						{
							continue;
						}
						int num7 = MatchLength(data, pos - num6, pos, matchLimit);
						if (num7 < num3)
						{
							continue;
						}
						num4 = num7;
						num5 = num6;
					}
					if (num4 >= num3)
					{
						if (pos + num4 > matchLimit)
						{
							num4 = matchLimit - pos;
						}
						if (num4 >= num3)
						{
							num = InsertCandidate(cml, cdist, num, num4, num5, 4);
						}
					}
				}
			}
			int num8 = RepMatchLength(data, pos, 8, matchLimit);
			if (num8 >= 4)
			{
				num = InsertCandidate(cml, cdist, num, num8, 8, cml.Length);
			}
			for (int j = 0; j < num; j++)
			{
				coff[j] = KrakenOptimalCost.CostOffset(cdist[j], cc);
			}
			return num;
		}
		if (UseSuffixTrieFinder)
		{
			Span<int> span = stackalloc int[64];
			Span<int> span2 = stackalloc int[64];
			int num9 = 0;
			int num10 = 0;
			int num11 = (UseDpMml3 ? 3 : 4);
			int num12 = (UseTinyOffsetRemap ? 1 : 8);
			uint num13 = Hash(data, pos);
			for (int num14 = dpHead[num13]; num14 >= 0; num14 = dpPrev[num14])
			{
				int num15 = pos - num14;
				if (num15 >= num12)
				{
					int num16 = MatchLength(data, num14, pos, matchLimit);
					if (num16 > num10)
					{
						num10 = num16;
						if (num16 >= num11 && num9 < span.Length)
						{
							span[num9] = num16;
							span2[num9] = num15;
							num9++;
						}
					}
				}
			}
			int num17 = ((num9 > 4) ? 4 : num9);
			for (int k = 0; k < num17; k++)
			{
				int index = num9 - 1 - k;
				int len = span[index];
				int num18 = span2[index];
				if (UseTinyOffsetRemap && num18 < 8)
				{
					int num19 = RoundUpTiny(num18);
					if (num19 == 0 || num19 > pos)
					{
						continue;
					}
					int num20 = MatchLength(data, pos - num19, pos, matchLimit);
					if (num20 < num11)
					{
						continue;
					}
					len = num20;
					num18 = num19;
				}
				num = InsertCandidate(cml, cdist, num, len, num18, 4);
			}
			int num21 = RepMatchLength(data, pos, 8, matchLimit);
			if (num21 >= 4)
			{
				num = InsertCandidate(cml, cdist, num, num21, 8, cml.Length);
			}
			for (int l = 0; l < num; l++)
			{
				coff[l] = KrakenOptimalCost.CostOffset(cdist[l], cc);
			}
			return num;
		}
		int num22 = 0;
		if (ctmf != null)
		{
			Span<int> outLen = stackalloc int[40];
			Span<int> outDist = stackalloc int[40];
			for (int num23 = ctmf.Query(data, pos, matchLimit, outLen, outDist) - 1; num23 >= 0; num23--)
			{
				num = InsertCandidate(cml, cdist, num, outLen[num23], outDist[num23], 4);
				if (outLen[num23] > num22)
				{
					num22 = outLen[num23];
				}
			}
		}
		if (ctmf == null || UseChainUnion || UseLrmSupplement)
		{
			bool flag = ctmf != null && !UseChainUnion;
			uint num24 = Hash(data, pos);
			int num25 = dpHead[num24];
			int num26 = 0;
			while (num25 >= 0 && num26 < ChainWalkLimit)
			{
				int num27 = pos - num25;
				if (num27 >= 8)
				{
					int num28 = MatchLength(data, num25, pos, matchLimit);
					if (num28 >= 4 && (!flag || num28 > num22))
					{
						num = InsertCandidate(cml, cdist, num, num28, num27, 4);
					}
				}
				num25 = dpPrev[num25];
				num26++;
			}
		}
		int num29 = RepMatchLength(data, pos, 8, matchLimit);
		if (num29 >= 4)
		{
			num = InsertCandidate(cml, cdist, num, num29, 8, cml.Length);
		}
		for (int m = 0; m < num; m++)
		{
			coff[m] = KrakenOptimalCost.CostOffset(cdist[m], cc);
		}
		return num;
	}

	/// <summary>
	/// DIAGNOSTIC (default-unused, TEST-ONLY): dumps the per-position level-7 finder Pareto table so it can be
	/// diffed against the <c>match finder</c> (find_all_matches) output captured
	/// by the findump diagnostic. For each position it walks the SAME 4-byte hash chain <see cref="M:LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.FindCandidates(System.ReadOnlySpan{System.Byte},System.Int32,System.Int32[],System.Int32[],System.Int32,LibProsperoPkg.PFS.Compression.Oodle.KrakenOptimalCost.CodeCosts,System.Span{System.Int32},System.Span{System.Int32},System.Span{System.Int32},LibProsperoPkg.PFS.Compression.Oodle.OodleKrakenEncoder.Ctmf)" />
	/// uses (most-recent-first ⇒ distance ascending) and records the closest-offset-per-length frontier,
	/// length-descending, up to 4 pairs — the pure find_all_matches analog (NO rep0/off-8 supplement, which the
	/// DP adds separately). Layout = <c>data.Length * 8</c> ints = {len0,dist0,len1,dist1,len2,dist2,len3,dist3}
	/// per position (0 = unused slot). <paramref name="recMin" /> sets the minimum recorded length (4 = production
	/// mml; the num_firstbytes=2 finder also reports len 2/3 which a 4-byte hash cannot surface).
	/// </summary>
	internal static int[] DiagDumpFinderTable(ReadOnlySpan<byte> data, int recMin = 4)
	{
		int length = data.Length;
		int[] array = new int[131072];
		int[] array2 = new int[length];
		array.AsSpan().Fill(-1);
		int inserted = 0;
		int[] array3 = new int[length * 8];
		Span<int> span = stackalloc int[128];
		Span<int> span2 = stackalloc int[128];
		int limit = length;
		int num = length - 3;
		for (int i = 0; i < num; i++)
		{
			inserted = InsertUpTo(data, array, array2, inserted, i);
			int num2 = 0;
			int num3 = 0;
			uint num4 = Hash(data, i);
			for (int num5 = array[num4]; num5 >= 0; num5 = array2[num5])
			{
				int num6 = i - num5;
				if (num6 >= 8)
				{
					int num7 = MatchLength(data, num5, i, limit);
					if (num7 > num3)
					{
						num3 = num7;
						if (num7 >= recMin && num2 < span.Length)
						{
							span[num2] = num7;
							span2[num2] = num6;
							num2++;
						}
					}
				}
			}
			int num8 = ((num2 > 4) ? 4 : num2);
			int num9 = i * 8;
			for (int j = 0; j < num8; j++)
			{
				int index = num2 - 1 - j;
				array3[num9 + j * 2] = span[index];
				array3[num9 + j * 2 + 1] = span2[index];
			}
		}
		return array3;
	}

	/// <summary>Inserts (len,dist) into the length-descending candidate arrays, deduping by distance, capped at <paramref name="cap" />.</summary>
	private static int InsertCandidate(Span<int> cml, Span<int> cdist, int n, int len, int dist, int cap)
	{
		for (int i = 0; i < n; i++)
		{
			if (cdist[i] == dist)
			{
				return n;
			}
		}
		int j;
		for (j = 0; j < n && cml[j] >= len; j++)
		{
		}
		if (j >= cap)
		{
			return n;
		}
		for (int num = ((n < cap) ? n : (cap - 1)); num > j; num--)
		{
			cml[num] = cml[num - 1];
			cdist[num] = cdist[num - 1];
		}
		cml[j] = len;
		cdist[j] = dist;
		if (n >= cap)
		{
			return n;
		}
		return n + 1;
	}
}
