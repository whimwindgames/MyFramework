using System;
using System.Net.WebSockets;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static UnityUtility;
using static FrameUtility;
using static StringUtility;
using static FrameBaseHotFix;
using static FrameDefine;
using static FrameBaseUtility;

// 当前程序作为客户端时使用,表示一个与WebSocket服务器的连接,用于非webgl平台
public abstract class NetConnectWebSocket : NetConnect
{
	protected Queue<PacketReceiveInfo> mReceiveBuffer = new();					// 在主线程中执行的消息列表
	protected Queue<PacketSendInfo> mOutputBuffer = new();						// 待发送列表
	protected StreamBuffer mInputBuffer = new(TCP_INPUT_BUFFER);				// 接收消息的缓冲区
	protected Dictionary<string, string> mHeader = new();						// 建立连接时需要传的header
	protected NetStateCallback mNetStateCallback;								// 网络状态改变的回调
	protected ClientWebSocket mWebSocket;										// 套接字实例
	protected DateTime mPingStartTime;											// ping开始的时间
	protected MyTimer mPingTimer = new();										// ping计时器
	protected Action mPingCallback;												// 外部设置的用于发送ping包的函数
	protected string mURL;														// WebSocket地址
	protected byte[] mRecvBuff = new byte[WEB_SOCKET_RECEIVE_BUFFER];           // 从Socket接收时使用的缓冲区
	protected bool mManualDisconnect;                                           // 是否正在主动断开连接
	protected bool mSending;													// 是否正在发送一条消息
	protected NET_STATE mNetState;												// 网络连接状态
	protected WebSocketMessageType mMessageType = WebSocketMessageType.Text;	// 数据类型,文本还是二进制
	private CancellationTokenSource mSocketCts;
	private int mSocketGeneration;
	private int mConnectedGeneration = -1;
	private int mSendingGeneration = -1;
	public virtual void init(float pingTime)
	{
		// 每隔一定时间发出一个ping包
		mPingTimer.init(-1.0f, pingTime);
		mPingTimer.setEnsureInterval(true);
	}
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
		mSending = false;
		mNetState = NET_STATE.NONE;
		mMessageType = WebSocketMessageType.Text;
		mSocketCts = null;
		mSocketGeneration = 0;
		mConnectedGeneration = -1;
		mSendingGeneration = -1;
	}
	public void addHeader(string name, string value)			{ mHeader.addOrSet(name, value); }
	public void setNetStateCallback(NetStateCallback callback)	{ mNetStateCallback = callback; }
	public void setMessageType(WebSocketMessageType type)		{ mMessageType = type; }
	public bool isConnected()									{ return mNetState == NET_STATE.CONNECTED; }
	public bool isConnecting()									{ return mNetState == NET_STATE.CONNECTING; }
	public bool isDisconnected()								{ return mNetState != NET_STATE.CONNECTED && mNetState != NET_STATE.CONNECTING; }
	public NetStateCallback getNetStateCallback()				{ return mNetStateCallback; }
	public WebSocketMessageType getMessageType()				{ return mMessageType; }
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
		await connectAsync(mURL, callback);
	}
	private async Task connectAsync(string url, BoolCallback callback)
	{
		int generation = ++mSocketGeneration;
		ClientWebSocket socket = new();
		CancellationTokenSource socketCts = new();
		mWebSocket = socket;
		mSocketCts = socketCts;
		try
		{
			foreach (var item in mHeader)
			{
				socket.Options.SetRequestHeader(item.Key, item.Value);
			}
			using CancellationTokenSource timeout =
				CancellationTokenSource.CreateLinkedTokenSource(socketCts.Token);
			timeout.CancelAfter(TimeSpan.FromSeconds(10));
			await socket.ConnectAsync(new Uri(url), timeout.Token);
			if (!isCurrent(socket, generation))
			{
				invokeConnectCallback(callback, false);
				return;
			}
			if (socket.State == WebSocketState.Open)
			{
				log("连接服务器成功");
				notifyNetState(NET_STATE.CONNECTED);
				invokeConnectCallback(callback, true);
			}
			else
			{
				log("连接服务器失败");
				notifyNetState(NET_STATE.NET_CLOSE);
				invokeConnectCallback(callback, false);
			}
		}
		catch (OperationCanceledException)
		{
			if (!isCurrent(socket, generation))
			{
				invokeConnectCallback(callback, false);
				return;
			}
			log("连接服务器失败:连接超时或已取消");
			notifyNetState(NET_STATE.NET_CLOSE);
			invokeConnectCallback(callback, false);
		}
		catch (Exception e)
		{
			if (!isCurrent(socket, generation))
			{
				invokeConnectCallback(callback, false);
				return;
			}
			log("连接服务器失败:" + e.Message);
			notifyNetState(NET_STATE.NET_CLOSE);
			invokeConnectCallback(callback, false);
		}
	}
	public void disconnect()
	{
		mManualDisconnect = true;
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

		sendThread();

		// 解析所有已经收到的消息包
		while (mReceiveBuffer.Count > 0)
		{
			PacketReceiveInfo info = mReceiveBuffer.Dequeue();
			NetPacket packet = null;
			try
			{
				packet = parsePacket(info.mType, info.mPacketData, info.mPacketSize,
					info.mSequence, info.mFieldFlag);
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
				byte[] temp = info.mPacketData;
				UN_ARRAY_BYTE(ref temp);
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
	public virtual async void clearSocket()
	{
		ClientWebSocket socket = mWebSocket;
		CancellationTokenSource cts = mSocketCts;
		if (socket == null && cts == null)
		{
			return;
		}
		mWebSocket = null;
		mSocketCts = null;
		++mSocketGeneration;
		mConnectedGeneration = -1;
		mSending = false;
		mSendingGeneration = -1;
		cts?.Cancel();
		await closeSocketAsync(socket, cts);
	}
	private static async Task closeSocketAsync(ClientWebSocket socket,
		CancellationTokenSource socketCts)
	{
		try
		{
			if (socket != null && (socket.State == WebSocketState.Open ||
				socket.State == WebSocketState.CloseReceived))
			{
				using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
				await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,
					"Client Close", timeout.Token);
			}
		}
		catch (Exception) { }
		finally
		{
			socket?.Dispose();
			socketCts?.Dispose();
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
	// 发送Socket消息
	protected void sendThread()
	{
		if (mSending || 
			mWebSocket == null || 
			mWebSocket.State != WebSocketState.Open ||
			mOutputBuffer.Count == 0)
		{
			return;
		}
		PacketSendInfo item = mOutputBuffer.Dequeue();
		if (item.mData == null || item.mDataSize == 0)
		{
			releaseSend(item);
			return;
		}
		mSending = true;
		mSendingGeneration = mSocketGeneration;
		doSend(item);
	}
	protected async void doSend(PacketSendInfo info)
	{
		ClientWebSocket socket = mWebSocket;
		int generation = mSocketGeneration;
		CancellationToken cancellationToken = mSocketCts?.Token ?? default;
		await sendAsync(socket, generation, cancellationToken, info);
	}
	private async Task sendAsync(ClientWebSocket socket, int generation,
		CancellationToken ct, PacketSendInfo info)
	{
		try
		{
			await socket.SendAsync(new ArraySegment<byte>(info.mData, 0, info.mDataSize),
				mMessageType, true, ct);
		}
		catch (OperationCanceledException) { }
		catch (ObjectDisposedException) { }
		catch (WebSocketException e)
		{
			if (isCurrent(socket, generation)) socketException(e);
		}
		catch (Exception e)
		{
			logException(e, "WebSocket发送失败");
			if (isCurrent(socket, generation)) notifyNetState(NET_STATE.NET_CLOSE);
		}
		finally
		{
			releaseSend(info);
			if (mSendingGeneration == generation)
			{
				mSending = false;
				mSendingGeneration = -1;
			}
		}
	}
	// 接收Socket消息
	protected async void receiveThread()
	{
		ClientWebSocket socket = mWebSocket;
		if (socket == null)
		{
			return;
		}
		int generation = mSocketGeneration;
		CancellationToken cancellationToken = mSocketCts?.Token ?? default;
		await receiveAsync(socket, generation, cancellationToken);
	}
	private async Task receiveAsync(ClientWebSocket socket, int generation,
		CancellationToken ct)
	{
		while (isCurrent(socket, generation) && socket.State == WebSocketState.Open &&
			Application.isPlaying)
		{
			try
			{
				WebSocketReceiveResult result = await socket.ReceiveAsync(mRecvBuff, ct);
				if (!isCurrent(socket, generation)) return;
				if (result.MessageType == WebSocketMessageType.Close ||
					result.CloseStatus != null)
				{
					// 服务器异常
					notifyNetState(NET_STATE.SERVER_CLOSE, WebSocketError.Faulted);
					return;
				}
				if (result.MessageType != mMessageType)
				{
					logError("WebSocket消息类型不匹配:" + result.MessageType);
					notifyNetState(NET_STATE.NET_CLOSE, WebSocketError.Faulted);
					return;
				}
				if (result.Count > 0 && !mInputBuffer.addData(mRecvBuff, result.Count))
				{
					logError("WebSocket消息超过接收上限:" +
						mInputBuffer.getBufferSize());
					mInputBuffer.clear();
					notifyNetState(NET_STATE.NET_CLOSE, WebSocketError.Faulted);
					return;
				}
				if (!result.EndOfMessage) continue;
				parseReceiveBuffer();
			}
			catch (OperationCanceledException) { return; }
			catch (ObjectDisposedException) { }
			catch (WebSocketException e)
			{
				if (isCurrent(socket, generation)) socketException(e);
				return;
			}
			catch (Exception e)
			{
				logException(e, "WebSocket接收失败");
				if (isCurrent(socket, generation)) notifyNetState(NET_STATE.NET_CLOSE);
				return;
			}
		}
	}
	private void parseReceiveBuffer()
	{
		while (mInputBuffer.getDataLength() > 0)
		{
			PARSE_RESULT result = preParsePacket(mInputBuffer.getData(),
				mInputBuffer.getDataLength(), out int index, out byte[] packetData,
				out ushort packetType, out int packetSize, out uint sequence,
				out ulong fieldFlag, out bool hasSign);
			if (result != PARSE_RESULT.SUCCESS)
			{
				if (packetData != null) UN_ARRAY_BYTE(ref packetData);
				if (result == PARSE_RESULT.ERROR) mInputBuffer.clear();
				return;
			}
			if (index <= 0 || index > mInputBuffer.getDataLength())
			{
				if (packetData != null) UN_ARRAY_BYTE(ref packetData);
				mInputBuffer.clear();
				throw new InvalidOperationException("WebSocket解析器返回了无效长度");
			}
			mReceiveBuffer.Enqueue(new(packetData, fieldFlag, packetSize, sequence,
				packetType, hasSign));
			if (!mInputBuffer.removeData(0, index))
			{
				throw new InvalidOperationException("移除WebSocket接收数据失败");
			}
			if (isDevOrEditor())
			{
				log("已接收 : " + packetType.IToS() + ", 字节数:" + index.IToS(),
					LOG_LEVEL.LOW);
			}
		}
	}
	protected void socketException(WebSocketException e)
	{
		// 本地网络异常
		notifyNetState(NET_STATE.NET_CLOSE, e.WebSocketErrorCode);
	}
	protected void notifyNetState(NET_STATE state, WebSocketError errorCode = WebSocketError.Success)
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
		if (isConnected())
		{
			notifyConnected();
			receiveThread();
		}
		if (!mManualDisconnect)
		{
			CMD(out CmdNetConnectWebSocketState cmd);
			if (cmd != null)
			{
				cmd.mErrorCode = errorCode;
				cmd.mNetState = mNetState;
				cmd.mLastNetState = lastState;
				pushCommand(cmd, this);
			}
		}
	}
	private bool isCurrent(ClientWebSocket socket, int generation)
	{
		return ReferenceEquals(socket, mWebSocket) && generation == mSocketGeneration;
	}
	private static void invokeConnectCallback(BoolCallback callback, bool success)
	{
		try
		{
			callback?.Invoke(success);
		}
		catch (Exception e)
		{
			logException(e, "WebSocket连接回调异常");
		}
	}
	private void clearReceiveQueue()
	{
		while (mReceiveBuffer.Count > 0)
		{
			PacketReceiveInfo item = mReceiveBuffer.Dequeue();
			byte[] data = item.mPacketData;
			UN_ARRAY_BYTE(ref data);
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
