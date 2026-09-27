using System;
using System.Collections.Concurrent;
using System.IO;

namespace LibProsperoPkg.Util;

/// <summary>
/// Centralized cleaner for temporary build artifacts. Ensures temporary files and directories
/// are deleted when builds complete, fail, are cancelled, or when the process exits.
/// </summary>
public static class BuildCleaner
{
	private static readonly ConcurrentBag<string> _activeTempPaths = new ConcurrentBag<string>();

	static BuildCleaner()
	{
		AppDomain.CurrentDomain.ProcessExit += delegate
		{
			CleanupAll();
		};
		Console.CancelKeyPress += delegate
		{
			CleanupAll();
		};
	}

	/// <summary>
	/// Registers a temporary file or folder to be deleted if the process exits or a build is cancelled.
	/// </summary>
	public static void RegisterTempPath(string path)
	{
		if (!string.IsNullOrWhiteSpace(path))
		{
			_activeTempPaths.Add(path);
		}
	}

	/// <summary>
	/// Cleans up all registered temporary paths and sweeps any orphaned publisher temp files.
	/// </summary>
	public static void CleanupAll()
	{
		while (_activeTempPaths.TryTake(out var result))
		{
			TryDelete(result);
		}
		SweepOrphanedTempFiles();
	}

	/// <summary>
	/// Deletes a file or directory safely without throwing.
	/// </summary>
	public static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			else if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch
		{
		}
	}

	/// <summary>
	/// Sweeps orphaned temporary files matching LibProspero build patterns in the system temp directory.
	/// </summary>
	public static void SweepOrphanedTempFiles()
	{
		try
		{
			string tempPath = Path.GetTempPath();
			if (!Directory.Exists(tempPath))
			{
				return;
			}
			string[] array = new string[3] { "libprospero-publisher-", "libprospero-p2d-", "psmt_pfs_" };
			foreach (string item in Directory.EnumerateFiles(tempPath, "*.*", SearchOption.TopDirectoryOnly))
			{
				string fileName = Path.GetFileName(item);
				string[] array2 = array;
				foreach (string value in array2)
				{
					if (fileName.StartsWith(value, StringComparison.OrdinalIgnoreCase))
					{
						TryDelete(item);
						break;
					}
				}
			}
			foreach (string item2 in Directory.EnumerateDirectories(tempPath, "*.*", SearchOption.TopDirectoryOnly))
			{
				string fileName2 = Path.GetFileName(item2);
				string[] array3 = array;
				foreach (string value2 in array3)
				{
					if (fileName2.StartsWith(value2, StringComparison.OrdinalIgnoreCase))
					{
						TryDelete(item2);
						break;
					}
				}
			}
		}
		catch
		{
		}
	}
}
