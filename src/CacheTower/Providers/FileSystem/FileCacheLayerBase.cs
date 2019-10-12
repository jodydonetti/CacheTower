﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Nito.AsyncEx;

namespace CacheTower.Providers.FileSystem
{
	public abstract class FileCacheLayerBase : ICacheLayer, IDisposable
	{
		private bool Disposed = false;
		private string DirectoryPath { get; }
		private string ManifestPath { get; }
		private string FileExtension { get; }

		private AsyncLock ManifestLock { get; } = new AsyncLock();
		private bool? IsManifestAvailable { get; set; }

		private HashAlgorithm FileNameHashAlgorithm { get; } = MD5.Create();

		private ConcurrentDictionary<string, ManifestEntry> CacheManifest { get; set; }
		private ConcurrentDictionary<string, AsyncReaderWriterLock> FileLock { get; } = new ConcurrentDictionary<string, AsyncReaderWriterLock>();

		protected FileCacheLayerBase(string directoryPath, string fileExtension)
		{
			DirectoryPath = directoryPath;
			FileExtension = fileExtension;
			ManifestPath = Path.Combine(directoryPath, "manifest" + fileExtension);
		}

		protected abstract Task<T> Deserialize<T>(Stream stream);

		protected abstract Task Serialize<T>(Stream stream, T value);

		private class ManifestEntry
		{
			public string FileName { get; set; }
			public DateTime CachedAt { get; set; }
			public TimeSpan TimeToLive { get; set; }
		}

		private async Task TryLoadManifest()
		{
			//Avoid unnecessary lock contention way after manifest is loaded by checking before lock
			if (CacheManifest == null)
			{
				using (await ManifestLock.LockAsync())
				{
					//Check that once we have lock (due to a race condition on the outer check) that we still need to load the manifest
					if (CacheManifest == null)
					{
						if (File.Exists(ManifestPath))
						{
							using (var stream = new FileStream(ManifestPath, FileMode.Open, FileAccess.Read))
							{

								CacheManifest = await Deserialize<ConcurrentDictionary<string, ManifestEntry>>(stream);
							}
						}
						else
						{
							if (!Directory.Exists(DirectoryPath))
							{
								Directory.CreateDirectory(DirectoryPath);
							}

							CacheManifest = new ConcurrentDictionary<string, ManifestEntry>();
							using (var stream = new FileStream(ManifestPath, FileMode.OpenOrCreate, FileAccess.Write))
							{
								await Serialize(stream, CacheManifest);
							}
						}
					}
				}
			}
		}

		private async Task SaveManifest()
		{
			using (await ManifestLock.LockAsync())
			{
				using (var stream = new FileStream(ManifestPath, FileMode.Open, FileAccess.Write))
				{
					await Serialize(stream, CacheManifest);
				}
			}
		}

		private string GetFileName(string cacheKey)
		{
			var bytes = Encoding.UTF8.GetBytes(cacheKey);
			var hashBytes = FileNameHashAlgorithm.ComputeHash(bytes);
			var builder = new StringBuilder(32 + FileExtension?.Length ?? 0);

			//MD5 bytes = 16
			builder.Append(hashBytes[0].ToString("X2"));
			builder.Append(hashBytes[1].ToString("X2"));
			builder.Append(hashBytes[2].ToString("X2"));
			builder.Append(hashBytes[3].ToString("X2"));
			builder.Append(hashBytes[4].ToString("X2"));
			builder.Append(hashBytes[5].ToString("X2"));
			builder.Append(hashBytes[6].ToString("X2"));
			builder.Append(hashBytes[7].ToString("X2"));
			builder.Append(hashBytes[8].ToString("X2"));
			builder.Append(hashBytes[9].ToString("X2"));
			builder.Append(hashBytes[10].ToString("X2"));
			builder.Append(hashBytes[11].ToString("X2"));
			builder.Append(hashBytes[12].ToString("X2"));
			builder.Append(hashBytes[13].ToString("X2"));
			builder.Append(hashBytes[14].ToString("X2"));
			builder.Append(hashBytes[15].ToString("X2"));

			if (FileExtension != null)
			{
				builder.Append(FileExtension);
			}

			return builder.ToString();
		}

		public async Task Cleanup()
		{
			await TryLoadManifest();

			foreach (var cachePair in CacheManifest)
			{
				var manifestEntry = cachePair.Value;
				var expiryDate = manifestEntry.CachedAt.Add(manifestEntry.TimeToLive);
				if (expiryDate < DateTime.UtcNow && CacheManifest.TryRemove(cachePair.Key, out var _))
				{
					if (FileLock.TryRemove(manifestEntry.FileName, out var lockObj))
					{
						using (await lockObj.WriterLockAsync())
						{
							var path = Path.Combine(DirectoryPath, manifestEntry.FileName);
							if (File.Exists(path))
							{
								File.Delete(path);
							}
						}
					}
				}
			}
		}

		public async Task Evict(string cacheKey)
		{
			await TryLoadManifest();

			if (CacheManifest.TryRemove(cacheKey, out var manifestEntry))
			{
				if (FileLock.TryRemove(manifestEntry.FileName, out var lockObj))
				{
					using (await lockObj.WriterLockAsync())
					{
						var path = Path.Combine(DirectoryPath, manifestEntry.FileName);
						if (File.Exists(path))
						{
							File.Delete(path);
						}
					}
				}
			}
		}

		public async Task<CacheEntry<T>> Get<T>(string cacheKey)
		{
			await TryLoadManifest();

			if (CacheManifest.TryGetValue(cacheKey, out var manifestEntry))
			{
				var lockObj = FileLock.GetOrAdd(manifestEntry.FileName, (name) => new AsyncReaderWriterLock());
				using (await lockObj.ReaderLockAsync())
				{
					//By the time we have the lock, confirm we still have a cache
					if (CacheManifest.ContainsKey(cacheKey))
					{
						var path = Path.Combine(DirectoryPath, manifestEntry.FileName);
						using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read))
						{
							var value = await Deserialize<T>(stream);
							return new CacheEntry<T>(value, manifestEntry.CachedAt, manifestEntry.TimeToLive);
						}
					}
				}
			}

			return default;
		}

		public async Task<bool> IsAvailable(string cacheKey)
		{
			if (IsManifestAvailable == null)
			{
				try
				{
					await TryLoadManifest();
					IsManifestAvailable = true;
				}
				catch
				{
					IsManifestAvailable = false;
				}
			}

			return IsManifestAvailable.Value;
		}

		public async Task Set<T>(string cacheKey, CacheEntry<T> cacheEntry)
		{
			await TryLoadManifest();

			var manifestEntry = CacheManifest.GetOrAdd(cacheKey, (key) => new ManifestEntry
			{
				FileName = GetFileName(cacheKey)
			});

			//Update the manifest entry with the new cache entry date/times
			manifestEntry.CachedAt = cacheEntry.CachedAt;
			manifestEntry.TimeToLive = cacheEntry.TimeToLive;

			var lockObj = FileLock.GetOrAdd(manifestEntry.FileName, (name) => new AsyncReaderWriterLock());

			using (await lockObj.WriterLockAsync())
			{
				var path = Path.Combine(DirectoryPath, manifestEntry.FileName);
				using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
				{
					await Serialize(stream, cacheEntry.Value);
				}
			}
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (Disposed)
			{
				return;
			}

			if (disposing)
			{
				//TODO: Async disposing
				_ = SaveManifest();
				FileNameHashAlgorithm.Dispose();
			}

			Disposed = true;
		}
	}
}