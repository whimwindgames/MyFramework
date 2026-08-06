using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

public sealed class FrameAssetBridgeTests
{
	private sealed class CollectingLogSink : IFrameLogSink
	{
		public readonly List<FrameLogRecord> Records = new();
		public void Write(FrameLogRecord record) { Records.Add(record); }
	}

	private sealed class FakeAssetProvider : IFrameAssetProvider, IFrameAssetCatalog
	{
		public int ReleaseCount;
		public string MissingAddress;

		public Task<bool> ExistsAsync<T>(string address,
			CancellationToken cancellationToken = default) where T : UnityEngine.Object
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(!string.Equals(address, MissingAddress, StringComparison.Ordinal));
		}

		public Task<FrameAssetLease<T>> LoadAsync<T>(string address,
			CancellationToken cancellationToken = default) where T : UnityEngine.Object
		{
			if (typeof(T) != typeof(GameObject))
			{
				throw new NotSupportedException(typeof(T).FullName);
			}
			GameObject asset = new($"asset:{address}");
			FrameAssetLease<T> lease = new(address, (T)(UnityEngine.Object)asset,
				FrameAssetKind.ASSET, value =>
				{
					++ReleaseCount;
					UnityEngine.Object.DestroyImmediate(value);
				});
			return Task.FromResult(lease);
		}

		public Task<FrameAssetLease<GameObject>> InstantiateAsync(string address,
			Transform parent = null, CancellationToken cancellationToken = default)
		{
			GameObject instance = new($"instance:{address}");
			if (parent != null)
			{
				instance.transform.SetParent(parent, false);
			}
			return Task.FromResult(new FrameAssetLease<GameObject>(address, instance,
				FrameAssetKind.INSTANCE, value =>
				{
					++ReleaseCount;
					UnityEngine.Object.DestroyImmediate(value);
				}));
		}
	}

	[Test]
	public void LeaseReleaseIsIdempotent()
	{
		GameObject value = new("leased");
		int releases = 0;
		FrameAssetLease<GameObject> lease = new("ui/test", value,
			FrameAssetKind.INSTANCE, item =>
			{
				++releases;
				UnityEngine.Object.DestroyImmediate(item);
			});

		lease.Dispose();
		lease.Dispose();

		Assert.That(releases, Is.EqualTo(1));
		Assert.That(lease.IsDisposed, Is.True);
	}

	[Test]
	public async Task GatewayUsesHostProviderAndObservesLeaseLifetime()
	{
		using FrameRuntimeContext context = new("Test", new CollectingLogSink());
		FakeAssetProvider provider = new();
		List<FrameAssetOperationChanged> changes = new();
		context.Events.Subscribe<FrameAssetOperationChanged>(changes.Add);
		context.Assets.UseProvider(provider);

		FrameAssetLease<GameObject> lease = await context.Assets.InstantiateAsync("ui/game-hud");

		Assert.That(lease.Value.name, Is.EqualTo("instance:ui/game-hud"));
		Assert.That(context.Assets.ActiveLeaseCount, Is.EqualTo(1));
		Assert.That(changes.ConvertAll(change => change.State), Is.EqualTo(new[]
		{
			FrameAssetOperationState.LOADING,
			FrameAssetOperationState.SUCCEEDED,
		}));

		lease.Dispose();
		Assert.That(provider.ReleaseCount, Is.EqualTo(1));
		Assert.That(context.Assets.ActiveLeaseCount, Is.Zero);
		Assert.That(changes[^1].State, Is.EqualTo(FrameAssetOperationState.RELEASED));
	}

	[Test]
	public void GatewayRequiresAnExplicitHostProvider()
	{
		using FrameRuntimeContext context = new("Test", new CollectingLogSink());
		Assert.ThrowsAsync<InvalidOperationException>(async () =>
			await context.Assets.LoadAsync<GameObject>("missing"));
	}

	[Test]
	public async Task GatewayUsesOptionalProviderCatalogWithoutLoadingAnAsset()
	{
		using FrameRuntimeContext context = new("Test", new CollectingLogSink());
		FakeAssetProvider provider = new() { MissingAddress = "missing" };
		context.Assets.UseProvider(provider);

		Assert.That(await context.Assets.ExistsAsync<GameObject>("present"), Is.True);
		Assert.That(await context.Assets.ExistsAsync<GameObject>("missing"), Is.False);
		Assert.That(await context.Assets.ExistsAsync<GameObject>(string.Empty), Is.False);
		Assert.That(context.Assets.ActiveLeaseCount, Is.Zero);
	}

	[Test]
	public async Task LeaseCanReleaseAfterRuntimeContextIsDisposed()
	{
		FrameRuntimeContext context = new("Test", new CollectingLogSink());
		FakeAssetProvider provider = new();
		context.Assets.UseProvider(provider);
		FrameAssetLease<GameObject> lease = await context.Assets.InstantiateAsync("ui/late-release");

		context.Dispose();
		Assert.DoesNotThrow(lease.Dispose);

		Assert.That(provider.ReleaseCount, Is.EqualTo(1));
		Assert.That(context.Assets.ActiveLeaseCount, Is.Zero);
		Assert.ThrowsAsync<ObjectDisposedException>(async () =>
			await context.Assets.LoadAsync<GameObject>("after-shutdown"));
	}
}
