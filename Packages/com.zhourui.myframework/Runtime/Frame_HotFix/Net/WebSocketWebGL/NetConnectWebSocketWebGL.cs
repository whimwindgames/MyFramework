using System;
using System.Collections.Generic;
using UnityEngine;
using NativeWebSocket;
using static UnityUtility;
using static FrameUtility;
using static StringUtility;
using static FrameBaseHotFix;
using static FrameDefine;
using static FrameBaseUtility;

// 当前程序作为客户端时使用,表示一个与WebSocket服务器的连接,用于webgl平台
// 使用NativeWebSocket库实现,提供消息队列收发、输入缓冲和网络状态回调
public abstract class NetConnectWebSocketWebGL : NetConnect
{
	protected Queue<PacketReceiveInfo> mReceiveBuffer = new();			// 在主线程中执行的消息列表
	protected Queue<PacketSendInfo> mOutputBuffer = new();				// 待发送列表
	protected StreamBuffer mInputBuffer = new(TCP_INPUT_BUFFER);		// 接收消息的缓冲区
	protected Dictionary<string, string> mHeader = new();				// 建立连接时需要传的header
	protected NetStateCallback mNetStateCallback;						// 网络状态改变的回调
	protected WebSocket mWebSocket;										// 套接字实例
	protected DateTime mPingStartTime;									// ping开始的时间
	protected MyTimer mPingTimer = new();								// ping计时器
	protected Action mPingCallback;										// 外部设置的用于发送ping包的函数
	protected string mURL;												// WebSocket地址
	protected byte[] mRecvBuff = new byte[WEB_SOCKET_RECEIVE_BUFFER];	// 从Socket接收时使用的缓冲区
	protected bool mManualDisconnect;									// 是否正在主动断开连接
	protected NET_STATE mNetState;										// 网络连接状态
	private int mSocketGeneration;
	private int mConnectedGeneration = -1;
	private int mConnectCallbackGeneration = -1;
	private BoolCallback mPendingConnectCallback;
	public virtual void init(string url, float pingTime)
	{
		mURL = url;
		// 每隔一定时间发出一个ping包
		mPingTimer.init(-1.0f, pingTime);
		mPingTimer.setEnsureInterval(true);
	}
	public void setURL(string url) { mURL = url; }
	public override void resetProperty()
	{
		base.resetProperty();
		clearReceiveQueue();
		clearSendQueue();
		clearSocket();
		// reset检查器要求字段在本方法中显式复位,队列内容已由上面的释放函数回收
		mReceiveBuffer.Clear();
		mOutputBuffer.Clear();
		mInputBuffer.clear();
		mHeader.Clear();
		mNetStateCallback = null;
		mWebSocket = null;
		mPingStartTime = default;
		mPingTimer.stop();
		mPingCallback = null;
		mURL = null;
		mRecvBuff.setAllDefault();
		mManualDisconnect = false;
		mNetState = NET_STATE.NONE;
		mSocketGeneration = 0;
		mConnectedGeneration = -1;
		mConnectCallbackGeneration = -1;
		mPendingConnectCallback = null;
	}
	public void addHeader(string name, string value)			{ mHeader.addOrSet(name, value); }
	public void setNetStateCallback(NetStateCallback callback)	{ mNetStateCallback = callback; }
	public bool isConnected()									{ return mNetState == NET_STATE.CONNECTED; }
	public bool isConnecting()									{ return mNetState == NET_STATE.CONNECTING; }
	public bool isDisconnected()								{ return mNetState != NET_STATE.CONNECTED && mNetState != NET_STATE.CONNECTING; }
	public NetStateCallback getNetStateCallback()				{ return mNetStateCallback; }
	public async void startConnect(string url, BoolCallback callback)
	{
		if (isConnected() || isConnecting())
		{
			invokeConnectCallback(callback, false);
			return;
		}
		mURL = url;
		if (isDevOrEditor())
		{
			log("开始连接服务器:" + mURL);
		}
		mManualDisconnect = false;
		if (mWebSocket != null)
		{
			invokeConnectCallback(callback, false);
			logError("当前Socket不为空");
			return;
		}
		notifyNetState(NET_STATE.CONNECTING);
		int generation = ++mSocketGeneration;
		mConnectCallbackGeneration = -1;
		mPendingConnectCallback = callback;
		WebSocket socket;
		try
		{
			socket = new(mURL, mHeader);
			mWebSocket = socket;
		}
		catch (Exception e)
		{
			logException(e, "创建WebGL WebSocket失败");
			notifyNetState(NET_STATE.NET_CLOSE);
			completeConnect(generation, callback, false);
			return;
		}
		socket.OnOpen += () =>
		{
			if (!isCurrent(socket, generation))
			{
				return;
			}
			log("连接服务器成功");
			notifyNetState(NET_STATE.CONNECTED);
			completeConnect(generation, callback, true);
		};
		socket.OnError += (string errorMsg) =>
		{
			if (isCurrent(socket, generation))
			{
				logWarning("websocket error:" + errorMsg);
			}
		};
		socket.OnClose += (WebSocketCloseCode closeCode) =>
		{
			if (!isCurrent(socket, generation))
			{
				return;
			}
			bool connecting = isConnecting();
			notifyNetState(NET_STATE.SERVER_CLOSE, closeCode);
			if (connecting)
			{
				completeConnect(generation, callback, false);
			}
		};
		socket.OnMessage += (byte[] data) =>
		{
			if (isCurrent(socket, generation))
			{
				parseReceivedData(data);
			}
		};
		try
		{
			await socket.Connect();
		}
		catch (Exception e)
		{
			if (!isCurrent(socket, generation))
			{
				return;
			}
			logException(e, "WebGL WebSocket连接失败");
			notifyNetState(NET_STATE.NET_CLOSE);
			completeConnect(generation, callback, false);
		}
	}
	public void disconnect()
	{
		mManualDisconnect = true;
		clearSocket();
		mPingTimer.stop(false);
		clearReceiveQueue();
		clearSendQueue();
		// 主动关闭时,网络状态应该是无状态
		notifyNetState(NET_STATE.NONE);
	}
	public virtual void update(float elapsedTime)
	{
		if (mNetState == NET_STATE.CONNECTED && mPingCallback != null && mPingTimer.tickTimer(elapsedTime))
		{
			mPingStartTime = DateTime.Now;
			mPingCallback.Invoke();
		}

		if (mWebSocket != null && mWebSocket.State == WebSocketState.Open)
		{
			// 获取输出数据的读缓冲区
			while (mOutputBuffer.Count > 0)
			{
				PacketSendInfo item = mOutputBuffer.Dequeue();
				if (item.mData == null || item.mDataSize == 0)
				{
					releaseSend(item);
					continue;
				}
				doSend(item);
			}
		}
#if !UNITY_WEBGL || UNITY_EDITOR
		mWebSocket?.DispatchMessageQueue();
#endif
		// 解析所有已经收到的消息包,单包异常不能丢弃后续消息
		while (mReceiveBuffer.Count > 0)
		{
			PacketReceiveInfo info = mReceiveBuffer.Dequeue();
			NetPacket packet = null;
			try
			{
				packet = parsePacket(info.mType, info.mPacketData, info.mPacketSize, info.mSequence, info.mFieldFlag);
				if (packet == null)
				{
					continue;
				}
				using var a = new ProfilerScope(packet.GetType().ToString());
				packet.execute();
			}
			catch (Exception e)
			{
				logException(e, "socket packet error");
			}
			finally
			{
				UN_ARRAY_BYTE(ref info.mPacketData);
				if (packet != null)
				{
					mNetPacketFactory.destroyPacket(packet);
				}
			}
		}
	}
	public override void destroy()
	{
		base.destroy();
		mManualDisconnect = true;
		clearSocket();
		clearSendQueue();
		clearReceiveQueue();
	}
	public void setPingAction(Action callback) { mPingCallback = callback; }
	public abstract void sendNetPacket(NetPacket packet);
	public NET_STATE getNetState() { return mNetState; }
	public async virtual void clearSocket()
	{
		WebSocket socket = mWebSocket;
		if (socket == null)
		{
			return;
		}
		int generation = mSocketGeneration;
		bool connecting = isConnecting();
		mWebSocket = null;
		++mSocketGeneration;
		mConnectedGeneration = -1;
		if (connecting)
		{
			completeConnect(generation, mPendingConnectCallback, false);
			mPendingConnectCallback = null;
		}
		try
		{
			socket.CancelConnection();
			if (socket.State == WebSocketState.Open)
			{
				await socket.Close();
			}
		}
		catch (Exception e)
		{
			log("关闭连接时异常:" + e.Message);
		}
	}
	// 由于连接成功操作可能不在主线程,所以只能是外部在主线程通知网络管理器连接成功
	public void notifyConnected()
	{
		if (mConnectedGeneration == mSocketGeneration)
		{
			return;
		}
		mConnectedGeneration = mSocketGeneration;
		// 建立连接后将消息列表中残留的消息清空,双缓冲中的读写列表都要清空
		clearSendQueue();
		// 开始心跳计时
		mPingTimer.start();
		mInputBuffer.clear();
	}
	//------------------------------------------------------------------------------------------------------------------------------
	protected abstract NetPacket parsePacket(ushort packetType, byte[] buffer, int size, uint sequence, ulong fieldFlag);
	protected abstract PARSE_RESULT preParsePacket(byte[] buffer, int size, out int index, out byte[] outPacketData,
													out ushort packetType, out int packetSize, out uint sequence, out ulong fieldFlag, out bool hasSign);
	protected async void doSend(PacketSendInfo info)
	{
		WebSocket socket = mWebSocket;
		int generation = mSocketGeneration;
		try
		{
			if (socket == null)
			{
				return;
			}
			await socket.Send(info.mData, info.mDataSize);
		}
		catch (ObjectDisposedException) { }
		catch (WebSocketException e)
		{
			if (isCurrent(socket, generation))
			{
				socketException(e);
			}
		}
		catch (Exception e)
		{
			logException(e, "WebGL WebSocket发送失败");
			if (isCurrent(socket, generation))
			{
				notifyNetState(NET_STATE.NET_CLOSE);
			}
		}
		finally
		{
			releaseSend(info);
		}
	}
	protected void socketException(WebSocketException e)
	{
		// 本地网络异常
		notifyNetState(NET_STATE.NET_CLOSE);
	}
	protected void notifyNetState(NET_STATE state, WebSocketCloseCode errorCode = WebSocketCloseCode.Normal)
	{
		if (!Application.isPlaying)
		{
			return;
		}
		NET_STATE lastState = mNetState;
		mNetState = state;
		if (!isConnected() && !isConnecting())
		{
			clearSocket();
		}
		if (!mManualDisconnect)
		{
			CMD(out CmdNetConnectWebSocketState cmd);
			if (cmd != null)
			{
				cmd.mWebGLErrorCode = errorCode;
				cmd.mNetState = mNetState;
				cmd.mLastNetState = lastState;
				cmd.mIsWebGL = true;
				pushCommand(cmd, this);
			}
		}
	}
	private void parseReceivedData(byte[] data)
	{
		try
		{
			if (data == null || data.Length == 0)
			{
				return;
			}
			if (!mInputBuffer.addData(data, data.Length))
			{
				logError("WebGL WebSocket消息超过接收上限:" + mInputBuffer.getBufferSize());
				mInputBuffer.clear();
				notifyNetState(NET_STATE.NET_CLOSE, WebSocketCloseCode.TooBig);
				return;
			}
			while (mInputBuffer.getDataLength() > 0)
			{
				PARSE_RESULT result = preParsePacket(mInputBuffer.getData(), mInputBuffer.getDataLength(), out int index, out byte[] packetData,
											out ushort packetType, out int packetSize, out uint sequence, out ulong fieldFlag, out bool hasSign);
				if (result != PARSE_RESULT.SUCCESS)
				{
					if (packetData != null)
					{
						UN_ARRAY_BYTE(ref packetData);
					}
					if (result == PARSE_RESULT.ERROR)
					{
						mInputBuffer.clear();
					}
					return;
				}
				if (index <= 0 || index > mInputBuffer.getDataLength())
				{
					if (packetData != null)
					{
						UN_ARRAY_BYTE(ref packetData);
					}
					mInputBuffer.clear();
					throw new InvalidOperationException("WebGL WebSocket解析器返回了无效长度");
				}
				mReceiveBuffer.Enqueue(new(packetData, fieldFlag, packetSize, sequence, packetType, hasSign));
				if (!mInputBuffer.removeData(0, index))
				{
					throw new InvalidOperationException("移除WebGL WebSocket接收数据失败");
				}
				if (isDevOrEditor())
				{
					log("已接收 : " + packetType.IToS() + ", 字节数:" + index.IToS(), LOG_LEVEL.LOW);
				}
			}
		}
		catch (Exception e)
		{
			logException(e, "WebGL WebSocket接收失败");
			mInputBuffer.clear();
			notifyNetState(NET_STATE.NET_CLOSE);
		}
	}
	private bool isCurrent(WebSocket socket, int generation)
	{
		return ReferenceEquals(socket, mWebSocket) && generation == mSocketGeneration;
	}
	private void completeConnect(int generation, BoolCallback callback, bool success)
	{
		if (mConnectCallbackGeneration == generation)
		{
			return;
		}
		mConnectCallbackGeneration = generation;
		mPendingConnectCallback = null;
		invokeConnectCallback(callback, success);
	}
	private static void invokeConnectCallback(BoolCallback callback, bool success)
	{
		try
		{
			callback?.Invoke(success);
		}
		catch (Exception e)
		{
			logException(e, "WebGL WebSocket连接回调异常");
		}
	}
	private void clearReceiveQueue()
	{
		while (mReceiveBuffer.Count > 0)
		{
			PacketReceiveInfo item = mReceiveBuffer.Dequeue();
			UN_ARRAY_BYTE(ref item.mPacketData);
		}
		mInputBuffer.clear();
	}
	private void clearSendQueue()
	{
		while (mOutputBuffer.Count > 0)
		{
			releaseSend(mOutputBuffer.Dequeue());
		}
	}
	private static void releaseSend(PacketSendInfo info)
	{
		if (info.mDataNeedDestroy)
		{
			UN_ARRAY_BYTE(ref info.mData);
		}
	}
}
