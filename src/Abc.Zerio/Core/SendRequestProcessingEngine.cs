using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Disruptor;
using Disruptor.Dsl;

namespace Abc.Zerio.Core
{
    public unsafe ref struct AcquiredRequestEntry
    {
        public readonly RequestEntry* Value;

        private readonly ISequenced _sequencer;
        private readonly long _sequence;

        public AcquiredRequestEntry(ISequenced sequencer, long sequence, RequestEntry* value)
        {
            _sequencer = sequencer;
            _sequence = sequence;
            Value = value;
        }

        public void Dispose()
        {
            _sequencer.Publish(_sequence);
        }
    }

    internal class SendRequestProcessingEngine : IDisposable
    {
        private readonly InternalZerioConfiguration _configuration;
        private readonly UnmanagedRioBuffer<RequestEntry> _unmanagedRioBuffer;

        private readonly UnmanagedRingBuffer<RequestEntry> _ringBuffer;
        private readonly UnmanagedDisruptor<RequestEntry> _disruptor;

        public SendRequestProcessingEngine(InternalZerioConfiguration configuration, RioCompletionQueue sendingCompletionQueue, ISessionManager sessionManager)
        {
            _configuration = configuration;

            var ringBufferSize = configuration.SendRequestProcessingEngineRingBufferSize;
            _unmanagedRioBuffer = new UnmanagedRioBuffer<RequestEntry>(ringBufferSize, _configuration.SendingBufferLength);

            _disruptor = CreateDisruptor(sendingCompletionQueue, sessionManager);
            _ringBuffer = _disruptor.RingBuffer;
        }

        private unsafe UnmanagedDisruptor<RequestEntry> CreateDisruptor(RioCompletionQueue sendingCompletionQueue, ISessionManager sessionManager)
        {
            var waitStrategy = CreateWaitStrategy();

            var disruptor = new UnmanagedDisruptor<RequestEntry>((IntPtr)_unmanagedRioBuffer.FirstEntry,
                                                                 _unmanagedRioBuffer.EntryReservedSpaceSize,
                                                                 _unmanagedRioBuffer.Length,
                                                                 new ThreadPerTaskScheduler(),
                                                                 ProducerType.Multi,
                                                                 waitStrategy);

            var sendRequestProcessor = new SendRequestProcessor(_configuration, sessionManager);

            var sendCompletionProcessor = new SendCompletionProcessor(_configuration, sendingCompletionQueue);

            disruptor.HandleEventsWith(sendRequestProcessor).Then(sendCompletionProcessor);

            ConfigureWaitStrategy(waitStrategy, disruptor, sendCompletionProcessor);

            return disruptor;
        }

        private static void ConfigureWaitStrategy(IWaitStrategy waitStrategy, UnmanagedDisruptor<RequestEntry> disruptor, SendCompletionProcessor sendCompletionProcessor)
        {
            switch (waitStrategy)
            {
                case HybridWaitStrategy hybridWaitStrategy:
                    hybridWaitStrategy.SequenceBarrierForSendCompletionProcessor = disruptor.GetBarrierFor(sendCompletionProcessor);
                    return;
            }
        }

        private IWaitStrategy CreateWaitStrategy()
        {
            return _configuration.RequestEngineWaitStrategyType switch
            {
                RequestEngineWaitStrategyType.HybridWaitStrategy   => (IWaitStrategy)new HybridWaitStrategy(),
                RequestEngineWaitStrategyType.BlockingWaitStrategy => new BlockingWaitStrategy(),
                RequestEngineWaitStrategyType.SleepingWaitStrategy => new SleepingWaitStrategy(),
                RequestEngineWaitStrategyType.YieldingWaitStrategy => new YieldingWaitStrategy(),
                RequestEngineWaitStrategyType.BusySpinWaitStrategy => new BusySpinWaitStrategy(),
                RequestEngineWaitStrategyType.SpinWaitWaitStrategy => new SpinWaitWaitStrategy(),
                _                                                  => throw new ArgumentOutOfRangeException()
            };
        }

        public void RequestSend(int sessionId, ReadOnlySpan<byte> message)
        {
            var sequence = _ringBuffer.Next();
            try
            {
                ref var sendingEntry = ref _ringBuffer[sequence];
                sendingEntry.SetWriteRequest(sessionId, message);
            }
            finally
            {
                _ringBuffer.Publish(sequence);
            }
        }

        public void Start()
        {
            _disruptor.Start();
        }

        public void Stop()
        {
            _disruptor.Shutdown();
        }

        public void Dispose()
        {
            Stop();

            _unmanagedRioBuffer?.Dispose();
        }

        private class ThreadPerTaskScheduler : TaskScheduler
        {
            protected override IEnumerable<Task> GetScheduledTasks()
            {
                return Enumerable.Empty<Task>();
            }

            protected override void QueueTask(Task task)
            {
                new Thread(() => TryExecuteTask(task)) { IsBackground = true }.Start();
            }

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            {
                return TryExecuteTask(task);
            }
        }

        public unsafe AcquiredRequestEntry AcquireRequestEntry()
        {
            var sequence = _ringBuffer.Next();
            return new AcquiredRequestEntry(_ringBuffer, sequence, (RequestEntry*)Unsafe.AsPointer(ref _ringBuffer[sequence]));
        }
    }
}
