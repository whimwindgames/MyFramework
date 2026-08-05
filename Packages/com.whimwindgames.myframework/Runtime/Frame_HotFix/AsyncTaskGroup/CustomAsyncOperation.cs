using UnityEngine;

public enum ASYNC_OPERATION_STATUS : byte
{
	PENDING,
	SUCCEEDED,
	FAILED,
	CANCELED,
}

// 自定义异步操作,在协程中等待一个异步任务完成时使用
public class CustomAsyncOperation : CustomYieldInstruction
{
	protected bool mFinish;
	protected ASYNC_OPERATION_STATUS mStatus = ASYNC_OPERATION_STATUS.PENDING;
	protected string mError;
	public override bool keepWaiting { get { return !mFinish; } }
	public override void Reset()
	{
		base.Reset();
		mFinish = false;
		mStatus = ASYNC_OPERATION_STATUS.PENDING;
		mError = null;
	}
	public CustomAsyncOperation setFinish()
	{
		tryComplete(ASYNC_OPERATION_STATUS.SUCCEEDED);
		return this;
	}
	public CustomAsyncOperation setFailed(string error = null)
	{
		tryComplete(ASYNC_OPERATION_STATUS.FAILED, error);
		return this;
	}
	public CustomAsyncOperation setCanceled(string error = null)
	{
		tryComplete(ASYNC_OPERATION_STATUS.CANCELED, error);
		return this;
	}
	public bool tryComplete(ASYNC_OPERATION_STATUS status, string error = null)
	{
		if (mFinish || status == ASYNC_OPERATION_STATUS.PENDING)
		{
			return false;
		}
		mStatus = status;
		mError = error;
		mFinish = true;
		return true;
	}
	public bool isDone()						{ return mFinish; }
	public bool isSuccess()					{ return mStatus == ASYNC_OPERATION_STATUS.SUCCEEDED; }
	public ASYNC_OPERATION_STATUS getStatus()	{ return mStatus; }
	public string getError()					{ return mError; }
}
