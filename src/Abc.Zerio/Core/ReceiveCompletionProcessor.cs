using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Abc.Zerio.Configuration;
using Abc.Zerio.Interop;

namespace Abc.Zerio.Core
{
    internal class ReceiveCompletionProcessor : IDisposable
    {
        private readonly IZerioConfiguration _configuration;
        private readonly RioCompletionQueue _receivingCompletionQueue;
        private readonly ISessionManager _sessionManager;

        private Thread _completionWorkerThread;
        public event Action<int, ArraySegment<byte>> MessageReceived;
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private ReadState _readState = ReadState.AccumulatingLength;
        private int _readBytes;
        private int _messageLength;
        private readonly byte[] _buffer = new byte[64 * 1024];

        private readonly RequestProcessingEngine _requestProcessingEngine;

        public ReceiveCompletionProcessor(IZerioConfiguration configuration, RioCompletionQueue receivingCompletionQueue, ISessionManager sessionManager, RequestProcessingEngine requestProcessingEngine)
        {
            _configuration = configuration;
            _receivingCompletionQueue = receivingCompletionQueue;
            _sessionManager = sessionManager;
            _requestProcessingEngine = requestProcessingEngine;
        }

        public void Start()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _completionWorkerThread = new Thread(ProcessCompletions) { IsBackground = true };
            _completionWorkerThread.Start(_receivingCompletionQueue);
        }

        private unsafe void ProcessCompletions(object state)
        {
            Thread.CurrentThread.Name = "Receive completion processing thread";

            var completionQueue = (RioCompletionQueue)state;
            var maxCompletionResults = _configuration.MaxReceiveCompletionResults;
            var results = stackalloc RIO_RESULT[maxCompletionResults];

            int resultCount;
            while ((resultCount = completionQueue.TryGetCompletionResults(_cancellationTokenSource.Token, results, maxCompletionResults)) > 0)
            {
                for (var i = 0; i < resultCount; i++)
                {
                    var result = results[i];
                    var sessionId = (int)result.ConnectionCorrelation;
                    var requestContextKey = new RioRequestContextKey(result.RequestCorrelation);

                    OnRequestCompletion(sessionId, requestContextKey, (int)result.BytesTransferred);
                }
            }
        }

        private void OnRequestCompletion(int sessionId, RioRequestContextKey requestContextKey, int bytesTransferred)
        {
            if (bytesTransferred == 0)
            {
                Stop();
                return;
            }

            if (!_sessionManager.TryGetSession(sessionId, out var session))
                return;

            var buffer = session.ReadBuffer(requestContextKey.BufferId);

            try
            {
                if (buffer.Length < bytesTransferred)
                    throw new InvalidOperationException("Received more bytes than expected");

                buffer.DataLength = bytesTransferred;

                OnReceiveComplete(sessionId, buffer);
            }
            finally
            {
                _requestProcessingEngine.RequestReceive(session.Id, buffer.Id);
            }
        }

        private unsafe void OnReceiveComplete(int sessionId, RioBuffer buffer)
        {
            var offset = 0;

            while (buffer.DataLength - offset > 0)
            {
                switch (_readState)
                {
                    case ReadState.AccumulatingLength:
                    {
                        var bytesToCopy = Math.Min(sizeof(int) - _readBytes, buffer.DataLength - offset);
                        Unsafe.CopyBlockUnaligned(ref _buffer[_readBytes], ref buffer.Data[offset], (uint)bytesToCopy);
                        _readBytes += bytesToCopy;

                        if (_readBytes != sizeof(int))
                            return;

                        _messageLength = Unsafe.ReadUnaligned<int>(ref _buffer[0]);

                        offset += bytesToCopy;

                        _readState = ReadState.AccumulatingMessage;
                        _readBytes = 0;
                        continue;
                    }

                    case ReadState.AccumulatingMessage:
                    {
                        var bytesToCopy = Math.Min(_messageLength - _readBytes, buffer.DataLength - offset);
                        Unsafe.CopyBlockUnaligned(ref _buffer[_readBytes], ref buffer.Data[offset], (uint)bytesToCopy);
                        _readBytes += bytesToCopy;

                        if (_readBytes != _messageLength)
                            return;

                        MessageReceived(sessionId, new ArraySegment<byte>(_buffer, 0, _messageLength));

                        offset += bytesToCopy;

                        _messageLength = 0;
                        _readState = ReadState.AccumulatingLength;
                        _readBytes = 0;
                        continue;
                    }
                }
            }
        }

        private enum ReadState
        {
            AccumulatingLength,
            AccumulatingMessage,
        }

        public void Stop()
        {
            _cancellationTokenSource.Cancel();
            _completionWorkerThread.Join(TimeSpan.FromSeconds(10));
        }

        public void Dispose()
        {
            Stop();

            _cancellationTokenSource?.Dispose();
        }
    }
}
