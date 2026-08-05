using System;
using System.Threading;

// AOT只依赖稳定入口契约，不需要知道项目热更入口的具体类型名。
public interface IHotEnt
{
	void start(Action<Exception> done, CancellationToken ct);
}
