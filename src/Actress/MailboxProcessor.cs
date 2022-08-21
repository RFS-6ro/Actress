using R6Tasks.Utils;

namespace Actress
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public static class MailboxProcessor
    {
        public static MailboxProcessor<T> Start<T>(Func<MailboxProcessor<T>, Task> body, CancellationTokenSource? cancellationTokenSource = null)
            where T : class
        {
            var mailboxProcessor = new MailboxProcessor<T>(body, cancellationTokenSource);
            mailboxProcessor.Start();
            return mailboxProcessor;
        }
    }

    public class MailboxProcessor<TMsg> : IDisposable
    {
        private readonly Func<MailboxProcessor<TMsg>, Task> _body;
        private readonly CancellationTokenSource _cts;
        private readonly Mailbox<TMsg> _mailbox;
        private bool _started;
        private readonly Observable<Exception> _errorEvent;

        public MailboxProcessor(Func<MailboxProcessor<TMsg>, Task> body, CancellationTokenSource cancellationTokenSource = null)
        {
            _body = body;
            _cts = cancellationTokenSource;
            if (_cts == null)
            {
                CancellationUtils.RefreshToken(ref _cts);
            }
            _mailbox = new Mailbox<TMsg>(_cts);
            DefaultTimeout = Timeout.Infinite;
            _started = false;
            _errorEvent = new Observable<Exception>();
        }

        public IObservable<Exception> Errors => _errorEvent;

        public int CurrentQueueLength => _mailbox.CurrentQueueLength;

        public int DefaultTimeout { get; set; }

        public void Start()
        {
            if (_started)
            {
                throw new InvalidOperationException("MailboxProcessor already started");
            }

            _started = true;

            // Protect the execution and send errors to the event.
            // Note that exception stack traces are lost in this design - in an extended design
            // the event could propagate an ExceptionDispatchInfo instead of an Exception.

            async Task StartAsync()
            {
                try
                {
                    await _body(this);
                }
                catch (Exception exception)
                {
                    _errorEvent.OnNext(exception);
                    throw;
                }
            }

            Task.Run(StartAsync);
        }

        public void Post(TMsg message)
        {
            if (_cts.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }

            _mailbox.Post(message);
        }

        public TReply TryPostAndReply<TReply>(Func<IReplyChannel<TReply>, TMsg> msgf, int? timeout = null)
        {
            if (_cts.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }

            var tcs = new TaskCompletionSource<TReply>();
            var msg = msgf(new ReplyChannel<TReply>(reply =>
            {
                tcs.SetResult(reply);
            }));

            _mailbox.Post(msg);

            var task = tcs.Task;

            if (task.Wait(timeout ?? DefaultTimeout))
            {
                return task.Result;
            }

            return default(TReply);
        }

        public TReply PostAndReply<TReply>(Func<IReplyChannel<TReply>, TMsg> buildMessage, int? timeout = null)
        {
            if (_cts.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }

            var res = TryPostAndReply(buildMessage, timeout);
            if (!Equals(res, default(TReply)))
            {
                return res;
            }

            throw new TimeoutException("MailboxProcessor PostAndReply timed out");
        }

        public Task<TReply> PostAndTryAsyncReply<TReply>(Func<IReplyChannel<TReply>, TMsg> msgf, int? timeout = null)
        {
            if (_cts.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }
            
            timeout = timeout ?? DefaultTimeout;
            var tcs = new TaskCompletionSource<TReply>();
            var msg = msgf(new ReplyChannel<TReply>(reply =>
            {
                tcs.SetResult(reply);
            }));

            _mailbox.Post(msg);

            var task = tcs.Task;

            if (timeout == Timeout.Infinite)
            {
                return tcs.Task;
            }

            if (task.Wait(timeout.Value))
            {
                return task;
            }

            return Task.FromResult<TReply>(default(TReply));
        }

        public async Task<TReply> PostAndAsyncReply<TReply>(Func<IReplyChannel<TReply>, TMsg> msgf, int? timeout = null)
        {
            if (_cts.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }
            
            var res = await PostAndTryAsyncReply(msgf, timeout);
            if (!Equals(res, default(TReply)))
            {
                return res;
            }

            throw new TimeoutException("MailboxProcessor PostAndAsyncReply timed out");
        }

        public Task<TMsg> Receive(int? timeout = null)
        {
            if (_cts.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }

            return _mailbox.Receive(timeout ?? DefaultTimeout);
        }

        public Task<TMsg> TryReceive(int? timeout = null)
        {
            if (_cts.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }

            return _mailbox.TryReceive(timeout ?? DefaultTimeout);
        }

        public Task<T> Scan<T>(Func<TMsg, Task<T>> f, int? timeout = null) where T : class
        {
            if (_cts.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }

            return _mailbox.Scan(f, timeout ?? DefaultTimeout);
        }

        public Task<T> TryScan<T>(Func<TMsg, Task<T>> f, int? timeout = null) where T : class
        {
            if (_cts.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }

            return _mailbox.TryScan(f, timeout ?? DefaultTimeout);
        }

        public void Dispose()
        {
            _mailbox?.Dispose();
        }
    }
}
