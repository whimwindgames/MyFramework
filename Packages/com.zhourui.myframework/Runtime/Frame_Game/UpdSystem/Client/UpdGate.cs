using System.Threading;
using Cysharp.Threading.Tasks;

public delegate UniTask UpdGate(long size, CancellationToken ct);
