using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Catalog-routed migration provider. An address registered by the primary backend is always
/// handled by that backend; only an address absent from the primary catalog uses the fallback.
/// A failed primary load is intentionally not retried through the fallback, so broken migrations
/// remain visible instead of silently serving stale content.
/// </summary>
public sealed class FrameFallbackAssetProvider : IFrameAssetProvider, IFrameAssetCatalog,
	IFrameSynchronousAssetProvider, IFrameSynchronousAssetCatalog
{
	private readonly IFrameAssetProvider mPrimary;
	private readonly IFrameAssetCatalog mPrimaryCatalog;
	private readonly IFrameAssetProvider mFallback;
	private readonly IFrameAssetCatalog mFallbackCatalog;

	public FrameFallbackAssetProvider(IFrameAssetProvider primary,
		IFrameAssetProvider fallback)
	{
		mPrimary = primary ?? throw new ArgumentNullException(nameof(primary));
		mPrimaryCatalog = primary as IFrameAssetCatalog ?? throw new ArgumentException(
			"The primary asset provider must expose IFrameAssetCatalog.", nameof(primary));
		mFallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
		mFallbackCatalog = fallback as IFrameAssetCatalog ?? throw new ArgumentException(
			"The fallback asset provider must expose IFrameAssetCatalog.", nameof(fallback));
	}

	public async Task<FrameAssetLease<T>> LoadAsync<T>(string address,
		CancellationToken cancellationToken = default) where T : UnityEngine.Object
	{
		IFrameAssetProvider selected = await selectAsync<T>(address, cancellationToken);
		return await selected.LoadAsync<T>(address, cancellationToken);
	}

	public async Task<FrameAssetLease<GameObject>> InstantiateAsync(string address,
		Transform parent = null, CancellationToken cancellationToken = default)
	{
		IFrameAssetProvider selected = await selectAsync<GameObject>(address, cancellationToken);
		return await selected.InstantiateAsync(address, parent, cancellationToken);
	}

	public async Task<bool> ExistsAsync<T>(string address,
		CancellationToken cancellationToken = default) where T : UnityEngine.Object
	{
		if (string.IsNullOrWhiteSpace(address))
		{
			return false;
		}
		if (await mPrimaryCatalog.ExistsAsync<T>(address, cancellationToken))
		{
			return true;
		}
		return await mFallbackCatalog.ExistsAsync<T>(address, cancellationToken);
	}

	public FrameAssetLease<T> Load<T>(string address) where T : UnityEngine.Object
	{
		return selectSynchronous<T>(address).Load<T>(address);
	}

	public FrameAssetCollectionLease<T> LoadAll<T>(string address)
		where T : UnityEngine.Object
	{
		return selectSynchronous<T>(address).LoadAll<T>(address);
	}

	public bool Exists<T>(string address) where T : UnityEngine.Object
	{
		if (string.IsNullOrWhiteSpace(address))
		{
			return false;
		}
		if (mPrimary is IFrameSynchronousAssetCatalog primaryCatalog &&
			primaryCatalog.Exists<T>(address))
		{
			return true;
		}
		return mFallback is IFrameSynchronousAssetCatalog fallbackCatalog &&
			fallbackCatalog.Exists<T>(address);
	}

	private async Task<IFrameAssetProvider> selectAsync<T>(string address,
		CancellationToken cancellationToken) where T : UnityEngine.Object
	{
		if (string.IsNullOrWhiteSpace(address))
		{
			throw new ArgumentException("Asset address cannot be empty.", nameof(address));
		}
		return await mPrimaryCatalog.ExistsAsync<T>(address, cancellationToken)
			? mPrimary
			: mFallback;
	}

	private IFrameSynchronousAssetProvider selectSynchronous<T>(string address)
		where T : UnityEngine.Object
	{
		if (string.IsNullOrWhiteSpace(address))
		{
			throw new ArgumentException("Asset address cannot be empty.", nameof(address));
		}
		if (mPrimary is IFrameSynchronousAssetCatalog primaryCatalog &&
			primaryCatalog.Exists<T>(address))
		{
			return mPrimary as IFrameSynchronousAssetProvider ?? throw new NotSupportedException(
				$"Primary provider {mPrimary.GetType().FullName} cannot load synchronously.");
		}
		if (mFallback is IFrameSynchronousAssetCatalog fallbackCatalog &&
			fallbackCatalog.Exists<T>(address))
		{
			return mFallback as IFrameSynchronousAssetProvider ?? throw new NotSupportedException(
				$"Fallback provider {mFallback.GetType().FullName} cannot load synchronously.");
		}
		throw new FileNotFoundException($"No synchronous asset provider contains '{address}'.", address);
	}
}
