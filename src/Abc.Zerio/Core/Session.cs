using System;
using System.Threading;
using Abc.Zerio.Configuration;
using Abc.Zerio.Interop;

namespace Abc.Zerio.Core
{
    internal class Session : IDisposable
    {
        public int Id { get; }
        public string PeerId { get; set; }
        public RioRequestQueue RequestQueue => _requestQueue;

        private readonly IZerioConfiguration _configuration;
        private readonly CompletionQueues _completionQueues;
        private readonly UnmanagedRioBuffer<RioBufferSegment> _receivingBuffer;

        private RioRequestQueue _requestQueue;
        private IntPtr _socket;

        public Session(int sessionId, IZerioConfiguration configuration, CompletionQueues completionQueues)
        {
            Id = sessionId;
            _configuration = configuration;
            _completionQueues = completionQueues;
            _receivingBuffer = new UnmanagedRioBuffer<RioBufferSegment>(configuration.ReceivingBufferCount, _configuration.ReceivingBufferLength);
        }

        public void Open(IntPtr socket)
        {
            Close();

            _socket = socket;

            var maxOutstandingReceives = (uint)_configuration.ReceivingBufferCount;
            var maxOutstandingSends = (uint)_configuration.SendingBufferCount;

            _requestQueue = RioRequestQueue.Create(Id, socket, _completionQueues.SendingQueue, maxOutstandingSends, _completionQueues.ReceivingQueue, maxOutstandingReceives);
        }

        private void Close()
        {
            if (Interlocked.Exchange(ref _requestQueue, null) == null)
                return;

            WinSock.closesocket(_socket);
            _socket = IntPtr.Zero;

            Closed(this);
        }

        public event Action<Session> Closed;

        public unsafe RioBufferSegment* ReadBuffer(int bufferSegmentId)
        {
            return _receivingBuffer[bufferSegmentId];
        }

        public void InitiateReceiving(RequestProcessingEngine requestProcessingEngine)
        {
            for (var bufferSegmentId = 0; bufferSegmentId < _configuration.ReceivingBufferCount; bufferSegmentId++)
            {
                requestProcessingEngine.RequestReceive(Id, bufferSegmentId);
            }
        }

        public void Dispose()
        {
            _receivingBuffer?.Dispose();
        }
    }
}
