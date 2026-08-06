using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UObject = UnityEngine.Object;

/// <summary>
/// Adapts the framework's reference-counted <see cref="ResourceManager"/> to the
/// host-facing <see cref="FrameAssetGateway"/> contract. Logical host addresses may be
/// translated to GameResources-relative file names by the optional address resolver.
/// </summary>
public sealed class FrameResourceManagerAssetProvider : IFrameAssetProvider, IFrameAssetCatalog,
	IFrameSynchronousAssetProvider, IFrameSynchronousAssetCatalog
{
	private readonly ResourceManager mResources;
	private readonly Func<string, Type, string> mAddressResolver;

	public FrameResourceManagerAssetProvider(ResourceManager resources,
		Func<string, Type, string> addressResolver = null)
	{
		mResources = resources ?? throw new ArgumentNullException(nameof(resources));
		mAddressResolver = addressResolver;
	}

	public FrameAssetLease<T> Load<T>(string address) where T : UObject
	{
		string resourceAddress = resolve<T>(address);
		ResourceRef<T> resource = mResources.loadGameResource<T>(resourceAddress, false);
		if (resource == null || !resource.isValid())
		{
			release(ref resource);
			throw missing<T>(address, resourceAddress);
		}

		return createLease(address, resource);
	}

	public FrameAssetCollectionLease<T> LoadAll<T>(string address) where T : UObject
	{
		string resourceAddress = resolve<T>(address);
		UObject[] subAssets = mResources.loadSubGameResource<T>(resourceAddress,
			out ResourceRef<UObject> mainAsset, false);
		if (subAssets == null || mainAsset == null || !mainAsset.isValid())
		{
			release(ref mainAsset);
			throw missing<T>(address, resourceAddress);
		}

		List<T> values = new(subAssets.Length);
		foreach (UObject subAsset in subAssets)
		{
			if (subAsset is T value)
			{
				values.Add(value);
			}
		}
		return new FrameAssetCollectionLease<T>(address, values, _ => release(ref mainAsset));
	}

	public Task<FrameAssetLease<T>> LoadAsync<T>(string address,
		CancellationToken cancellationToken = default) where T : UObject
	{
		cancellationToken.ThrowIfCancellationRequested();
		string resourceAddress = resolve<T>(address);
		TaskCompletionSource<FrameAssetLease<T>> completion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		CancellationTokenRegistration registration = cancellationToken.Register(
			() => completion.TrySetCanceled(cancellationToken));

		try
		{
			mResources.loadGameResourceAsync<T>(resourceAddress, resource =>
			{
				try
				{
					if (resource == null || !resource.isValid())
					{
						release(ref resource);
						completion.TrySetException(missing<T>(address, resourceAddress));
						return;
					}

					FrameAssetLease<T> lease = createLease(address, resource);
					if (!completion.TrySetResult(lease))
					{
						// The host canceled after the backend request started. The old loader cannot
						// cancel that request, so release its completed reference immediately.
						lease.Dispose();
					}
				}
				catch (Exception exception)
				{
					release(ref resource);
					completion.TrySetException(exception);
				}
				finally
				{
					registration.Dispose();
				}
			}, false);
		}
		catch (Exception exception)
		{
			registration.Dispose();
			completion.TrySetException(exception);
		}
		return completion.Task;
	}

	public async Task<FrameAssetLease<GameObject>> InstantiateAsync(string address,
		Transform parent = null, CancellationToken cancellationToken = default)
	{
		FrameAssetLease<GameObject> prefabLease =
			await LoadAsync<GameObject>(address, cancellationToken);
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			GameObject instance = UObject.Instantiate(prefabLease.Value, parent, false);
			return new FrameAssetLease<GameObject>(address, instance, FrameAssetKind.INSTANCE,
				value =>
				{
					try
					{
						if (value != null)
						{
							UObject.Destroy(value);
						}
					}
					finally
					{
						prefabLease.Dispose();
					}
				});
		}
		catch
		{
			prefabLease.Dispose();
			throw;
		}
	}

	public bool Exists<T>(string address) where T : UObject
	{
		return !string.IsNullOrWhiteSpace(address) && mResources.hasKey(resolve<T>(address));
	}

	public Task<bool> ExistsAsync<T>(string address,
		CancellationToken cancellationToken = default) where T : UObject
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult(Exists<T>(address));
	}

	private FrameAssetLease<T> createLease<T>(string logicalAddress,
		ResourceRef<T> resource) where T : UObject
	{
		return new FrameAssetLease<T>(logicalAddress, resource.get(), FrameAssetKind.ASSET,
			_ => release(ref resource));
	}

	private string resolve<T>(string address) where T : UObject
	{
		if (string.IsNullOrWhiteSpace(address))
		{
			throw new ArgumentException("Asset address cannot be empty.", nameof(address));
		}
		string resolved = mAddressResolver?.Invoke(address, typeof(T)) ?? address;
		if (string.IsNullOrWhiteSpace(resolved))
		{
			throw new InvalidOperationException(
				$"The resource address resolver returned an empty path for '{address}'.");
		}
		return resolved;
	}

	private void release<T>(ref ResourceRef<T> resource) where T : UObject
	{
		if (resource != null)
		{
			mResources.unload(ref resource);
		}
	}

	private static FileNotFoundException missing<T>(string logicalAddress,
		string resourceAddress) where T : UObject
	{
		return new FileNotFoundException(
			$"Asset '{logicalAddress}' ({typeof(T).Name}) was not found at GameResources path " +
			$"'{resourceAddress}'.", resourceAddress);
	}
}
