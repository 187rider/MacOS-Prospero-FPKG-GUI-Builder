using System.Text;

namespace LibProsperoPkg.Util;

public static class ByteArrayExtensions
{
	public static string ToHexCompact(this byte[] b)
	{
		StringBuilder stringBuilder = new StringBuilder();
		foreach (byte b2 in b)
		{
			stringBuilder.AppendFormat("{0:X2}", b2);
		}
		return stringBuilder.ToString();
	}
}
