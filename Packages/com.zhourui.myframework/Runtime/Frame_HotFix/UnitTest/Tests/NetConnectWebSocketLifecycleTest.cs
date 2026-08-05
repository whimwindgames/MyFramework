using static FrameUtility;
using static TestAssert;

// WebSocket连接生命周期和分片数据回归测试
internal static class NetConnectWebSocketLifecycleTest
{
	public static void Run()
	{
		testJsonParserUsesAccumulatedBuffer();
		testDisconnectClearsPendingData();
	}

	private static void testJsonParserUsesAccumulatedBuffer()
	{
		TestWebSocketJson connect = new();
		byte[] source = { 1, 2, 3, 4 };
		byte[] packetData = connect.copyPayload(source, out int packetSize);
		try
		{
			assertEqual(source.Length, packetSize, "JSON消息长度应保持不变");
			for (int i = 0; i < source.Length; ++i)
			{
				assertEqual(source[i], packetData[i], "JSON消息必须从累计输入缓冲区复制");
			}
		}
		finally
		{
			UN_ARRAY_BYTE(ref packetData);
		}
	}

	private static void testDisconnectClearsPendingData()
	{
		TestWebSocketJson connect = new();
		connect.addPendingData();
		assertEqual(1, connect.getReceiveCount(), "测试前应存在待处理接收包");
		assertEqual(1, connect.getSendCount(), "测试前应存在待发送包");
		assertTrue(connect.getInputLength() > 0, "测试前输入缓冲区应有数据");

		connect.disconnect();

		assertEqual(0, connect.getReceiveCount(), "断开连接应释放全部待处理接收包");
		assertEqual(0, connect.getSendCount(), "断开连接应释放全部待发送包");
		assertEqual(0, connect.getInputLength(), "断开连接应清空累计输入缓冲区");
	}

	private sealed class TestWebSocketJson : NetConnectWebSocketJson
	{
		public byte[] copyPayload(byte[] source, out int packetSize)
		{
			mRecvBuff[0] = 99;
			PARSE_RESULT result = preParsePacket(source, source.Length, out int index,
				out byte[] packetData, out _, out packetSize, out _, out _, out _);
			assertEqual(PARSE_RESULT.SUCCESS, result, "JSON消息预解析应成功");
			assertEqual(source.Length, index, "JSON消息预解析应消费完整消息");
			return packetData;
		}

		public void addPendingData()
		{
			byte[] receiveData = { 1 };
			byte[] sendData = { 2 };
			mReceiveBuffer.Enqueue(new PacketReceiveInfo(receiveData, 0, 1, 0, 0, false));
			mOutputBuffer.Enqueue(new PacketSendInfo(sendData, 1, false, 0));
			mInputBuffer.addData(receiveData, receiveData.Length);
		}

		public int getReceiveCount() { return mReceiveBuffer.Count; }
		public int getSendCount() { return mOutputBuffer.Count; }
		public int getInputLength() { return mInputBuffer.getDataLength(); }
	}
}
