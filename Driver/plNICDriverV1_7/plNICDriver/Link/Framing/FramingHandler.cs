﻿using Microsoft.Extensions.Logging;
using Pastel;
using plNICDriver.Link.ARQ;
using plNICDriver.Link.IDAllocation;
using plNICDriver.Phy;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static plNICDriver.Link.Framing.Frame;

namespace plNICDriver.Link.Framing
{

	//` ![](ED6D459F5E8088B10469C782A55F30FB.png;;;0.03705,0.02610)

	// TODO:
	// 1- To synchronize packet framing and parsing constant pattern should be available in frames to 
	// specify different boundaries of the frame, so that if misalignment happened can be recovered

	// Generic Link Frame is consisted of Header + Payload

	// Two way to synchronize packet parsing 1- len & timeout 2- byte/bit stuffing => makes packet twice less in worst case
	
	/*
	 * درآغاز این جوری بود که فریم های بسیار بزرگ باعث انقضای زمانی میشدند که این مشکل با 
	 * داده اولویت بالاتر به اندازه های کوچک تر بهتر شد ولی چون فرستادن پاک ناهمگام بود سبب قحطی برای بسته 
	 * های بزرگ میشد
	 */
	public class FramingHandler
	{
		private static readonly int TURN_AROUND = 10 + 100 + 10 + 100;
		public static readonly int BIT_DELAY = 20 + 100 + 5;
		private static readonly int TIME_DIV = 10;

		public delegate void OnRxFrame(FrameType type, int txId, int rxId, byte[] dat, int wid);

		private PHYSerial _serial;
		private Thread _rxThread;
		private Thread _txThread;
		
		private OnRxFrame _onRxFrame;
		private bool _terminate;

		private object _busOccupiedlock = new object();
		private long _BusLastRxTick = 0;

		int frameCounter = 0;
		byte[] buffer = new byte[FRAME_MAX_LEN*3];

		private PriorityQueue<Frame, int> _txQueue = new PriorityQueue<Frame, int>();
		private IList<byte[]> _txedFrames = new List<byte[]>();
		ILogger<FramingHandler> _lg;

		public FramingHandler(ILoggerFactory loggerFactory, OnRxFrame rxFrame, string portName)
		{
			_lg = loggerFactory.CreateLogger<FramingHandler>();

			_serial = new PHYSerial(portName, OnBytesRx);
			_rxThread = new Thread(FrameReceiveTask);
			_txThread = new Thread(FramePriorityTransmissionTask);
			_terminate = false;
			_onRxFrame = rxFrame;

			_BusLastRxTick = DateTime.UtcNow.Ticks;
		}

		public void Begin()
		{
			_rxThread.Start();
			_txThread.Start();
		}

		private void OnBytesRx(byte[] bytes)
		{
			lock (_busOccupiedlock)
			{
				_BusLastRxTick = DateTime.UtcNow.Ticks;
				Array.Copy(bytes, 0, buffer, frameCounter, bytes.Length);
				frameCounter += bytes.Length;
				//if (frameCounter >= FRAME_MAX_LEN)
				//{
				//	_lg.LDebug($"falls back to zero");
				//	frameCounter = 0;
				//}
			}
			//_lg.LDebug($"bytes recv {buffer.ToStr(bytes.Length)}");
		}

		// Hello This is a test with len
		private async Task<bool> ReceiveBytes(byte[] bytes, int offset, int numBytes)
		{
			double timeout = numBytes * BIT_DELAY;
			var counter = TIME_DIV;
			while (0 < numBytes)
			{
				var delayTime = (int)(Math.Ceiling(timeout / TIME_DIV));
				//Console.WriteLine($"this is delay time {delayTime}");
				await Task.Delay(delayTime);
				counter--;

				lock (_busOccupiedlock)
					if (frameCounter >= numBytes)
					{
						Array.Copy(buffer, 0, bytes, offset, numBytes);
						frameCounter = frameCounter - numBytes;
						Array.Copy(buffer, numBytes, buffer, 0, frameCounter);
						return true;
					}

				if (counter == 0)
				{
					if (!IsTxGoingOn() && frameCounter > 0) 
						_lg.LWarning($"{frameCounter} bytes discarded".Pastel(Color.Violet) + " due to timing");
					lock (_busOccupiedlock)
						frameCounter = 0;
					return false;
				}

					//else
					//{
					//	timeout = (numBytes - frameCounter) * BIT_DELAY;
					//	counter = TIME_DIV;
					//}
			}

			return false;
		}

		private bool IsTxGoingOn()
		{
			var now = DateTime.UtcNow.Ticks;
			lock (_busOccupiedlock)
				return ((now - _BusLastRxTick) / TimeSpan.TicksPerMillisecond < BIT_DELAY);
		}

		//`

		//`![](2F6EA1EA363F933B2713EFFB670F3DD5.png;https://www.geeksforgeeks.org/difference-between-1-persistent-and-non-persistent-csma/ ;;0.04488,0.04759)
		private async Task<bool> BackOff(int tries)
		{
			var retries = tries - 1;
			// Non persistent CSMA
			while (true)
			{
				// Fallback for a random time
				Random rand = new Random((int)DateTime.UtcNow.Ticks);
				var backOffTime = (int)(rand.NextInt64((int)Math.Pow(2, retries+3)) * TURN_AROUND);
				_lg.LWarning($"Back-off for " + $"{backOffTime} millis".PastelBg(Color.Maroon) + " No TX till then!");
				await Task.Delay(backOffTime);

				// Check if possible to send
				if (!IsTxGoingOn())
					return true;
				else
				{
					_lg.LWarning("Waiting, bus is " + "occupied".PastelBg(Color.Maroon));
					retries++;
					if (retries > 15)
						return false;
				}
				
			}
		}

		private async void FramePriorityTransmissionTask()
		{
			bool shouldTryBackOff = false;
			Frame nextFrame;
			while (!_terminate)
			{
				lock (_txQueue)
					if (_txQueue.Count > 0)
						shouldTryBackOff = true;
				
				if (shouldTryBackOff)
				{
					lock (_txQueue)
						nextFrame = new Frame(_txQueue.Dequeue());

					if (!await BackOff(nextFrame.Tries)) // This way we give chance to late arriving lower priorities to be first
						_lg.LCritical("Back off failed for 15 times");

					nextFrame.GetSerialize(out byte[] payload);
					// TODO: Here we can use contant pattern boundaries and stuffing
					byte[] frameTotal = new byte[payload.Length + 1];
					frameTotal[0] = ((byte)payload.Length);
					Array.Copy(payload, 0, frameTotal, 1, payload.Length);
					_lg.LDebug($"Frame header transmitting: {nextFrame.GetHeader()}, bytes: {frameTotal.ToStr()}");
					await _serial.SendBytes(frameTotal, 0, frameTotal.Length);
					
					lock (_txedFrames)
						_txedFrames.Add(payload);
				}

				shouldTryBackOff = false;
				Thread.Sleep(1);
			}
		}

		private async void FrameReceiveTask()
		{
			Frame frm = new Frame();
			while (!_terminate)
			{
				await Task.Delay(2);
				//_lg.LWarning($"trying");

				if (! await ReceiveBytes(frm.txFrame, 0, 1))
				{
					//_lg.LWarning($"time out");
					continue;
				}
				if (! await ReceiveBytes(frm.txFrame, 0, Frame.HEADER_LEN))
				{
					//_lg.LWarning($"time out");
					continue;
				}

				_lg.LDebug($"Frame header received: {frm.GetHeader()}");

				if (!frm.IsValid(out byte calcHash))
				{
					_lg.LError($"Invalid header, calculated Hsh {calcHash}");
					continue;
				} else
					_lg.LDebug("valid header");

				if (frm.PLen > 0)
					// Get remaining
					if (! await ReceiveBytes(frm.txFrame, Frame.HEADER_LEN, frm.PLen))
					{
						_lg.LWarning($"time out");
						continue;
					}

				//Random random = new Random((int)DateTime.Now.Ticks);
				//if (random.NextDouble() > 0.7)
				//{
				//	_lg.LogError("Delibrately manipulating received frame");
				//	frm.ManiPulateData();
				//}

				var payload = frm.GetPayload();
				_onRxFrame(frm.FrTp, frm.TxId, frm.RxId, payload, frm.Wid);
			}
		}

		public async Task<bool> SendFrame(FrameType type, int txId, int rxId, int wid, byte[]? dat, int numTries = 1)
		{
			Frame frame = new Frame(type, (byte)txId, (byte)rxId, (byte)wid, dat, numTries);
			lock(_txQueue)
				_txQueue.Enqueue(frame, frame.PLen);

			while (true)
			{
				lock (_txedFrames)
				{
					for (int i = _txedFrames.Count - 1; i >= 0; i--)
					{
						if (_txedFrames[i].Length == frame.PLen+HEADER_LEN)
							if (!_txedFrames[i].Where((t, i) => t != frame.txFrame[i]).Any())
							{
								_txedFrames.RemoveAt(i);
								return true;
							}
					}
				}

				await Task.Delay(1);
			}
		}

		public void Dispose()
		{
			_serial.Dispose();
			_rxThread?.Join();
			_terminate = true;
		}
	}
}
