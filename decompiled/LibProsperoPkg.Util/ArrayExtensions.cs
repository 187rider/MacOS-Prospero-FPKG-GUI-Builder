namespace LibProsperoPkg.Util;

public static class ArrayExtensions
{
	public static T[] Fill<T>(this T[] arr, T val)
	{
		for (int i = 0; i < arr.Length; i++)
		{
			arr[i] = val;
		}
		return arr;
	}
}
