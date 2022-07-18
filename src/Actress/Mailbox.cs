namespace Actress
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    public class Mailbox<TMsg> : IDisposable
    {
        private readonly Queue<TMsg> _arrivals;
        private List<TMsg> _inboxStore;
        private TaskCompletionSource<bool> _savedCont;
        private AutoResetEvent _autoResetEvent;

        public Mailbox()
        {
            _arrivals = new Queue<TMsg>();
        }

        public int CurrentQueueLength
        {
            get
            {
                lock (_arrivals)
                {
                    return Inbox.Count + _arrivals.Count;
                }
            }
        }

        public List<TMsg> Inbox
        {
            get
            {
                if (_inboxStore == null)
                {
                    _inboxStore = new List<TMsg>(1);
                }

                return _inboxStore;
            }
        }

        public AutoResetEvent AutoResetEvent
        {
            get
            {
                if (_autoResetEvent == null)
                {
                    _autoResetEvent = new AutoResetEvent(false);
                }

                return _autoResetEvent;
            }
        }

        public void Dispose()
        {
            _autoResetEvent?.Dispose();
        }

        internal TMsg ReceiveFromArrivalsUnsafe()
        {
            if (_arrivals.Count == 0)
            {
                return default(TMsg);
            }

            return _arrivals.Dequeue();
        }

        internal TMsg ReceiveFromArrivals()
        {
            lock (_arrivals)
            {
                return ReceiveFromArrivalsUnsafe();
            }
        }

        internal TMsg ReceiveFromInbox()
        {
            var list = _inboxStore;
            if (list == null)
            {
                return default(TMsg);
            }

            if (list.Count == 0)
            {
                return default(TMsg);
            }

            var value = list[0];
            list.RemoveAt(0);
            return value;
        }

        internal void Post(TMsg msg)
        {
            lock (_arrivals)
            {
                _arrivals.Enqueue(msg);

                // This is called when we enqueue a message, within a lock
                // We cooperatively unblock any waiting reader. If there is no waiting
                // reader we just leave the message in the incoming queue
                if (_savedCont == null)
                {
                    /* either no one is waiting(pulse is null) and leaving the message in the queue is sufficient....
                     * OR pulse is not null and someone is waiting on the wait handle
                    */
                    _autoResetEvent?.Set();

                    return;
                }

                var sc = _savedCont;
                _savedCont = null;
                sc.SetResult(true);
            }
        }

        internal async Task<TMsg> Receive(int timeout)
        {
            async Task<TMsg> ProcessFirstArrival()
            {
                while (true)
                {
                    var res = ReceiveFromArrivals();
                    if (res != null)
                    {
                        return res;
                    }

                    var ok = await WaitOne(timeout);
                    if (ok)
                    {
                        continue;
                    }

                    throw new TimeoutException("Mailbox Receive Timed Out");
                }
            }

            var resFromInbox = ReceiveFromInbox();
            if (resFromInbox == null)
            {
                return await ProcessFirstArrival();
            }

            return resFromInbox;
        }

        internal async Task<T> TryScan<T>(Func<TMsg, Task<T>> f, int timeout)
            where T : class
        {
            async Task<T> Func(Task timeoutTask1, CancellationTokenSource timeoutCts1)
            {
                while (true)
                {
                    var resP1 = ScanArrivals(f);
                    if (resP1 != null)
                    {
                        timeoutCts1.Cancel();
                        return await resP1;
                    }

                    var waitTask = WaitOneNoTimeout();
                    var t = await Task.WhenAny(waitTask, timeoutTask1);
                    if (t == timeoutTask1)
                    {
                        lock (_arrivals)
                        {
                            // Cancel the outstanding wait for messages installed by waitOneNoTimeout
                            //
                            // HERE BE DRAGONS. This is bestowed on us because we only support
                            // a single mailbox reader at any one time.
                            // If awaitEither returned control because timeoutAsync has terminated, waitOneNoTimeout
                            // might still be in-flight. In practical terms, it means that the push-to-async-result-cell
                            // continuation that awaitEither registered on it is still pending, i.e. it is still in savedCont.
                            // That continuation is a no-op now, but it is still a registered reader for arriving messages.
                            // Therefore we just abandon it - a brutal way of canceling.
                            // This ugly non-compositionality is only needed because we only support a single mailbox reader
                            // (i.e. the user is not allowed to run several Recieve/TryRecieve/Scan/TryScan in parallel) - otherwise
                            // we would just have an extra no-op reader in the queue.
                            _savedCont = null;
                        }

                        return null;
                    }

                    if (!waitTask.Result)
                    {
                        throw new InvalidProgramException("should not happen - WaitOneNoTimeout always returns true");
                    }
                }
            }

            async Task<T> ScanNoTimeout()
            {
                while (true)
                {
                    var resP1 = ScanArrivals(f);
                    if (resP1 != null)
                    {
                        return await resP1;
                    }

                    var ok = await WaitOneNoTimeout();
                    if (ok)
                    {
                        continue;
                    }

                    throw new TimeoutException("Timed out with infinite timeout??");
                }
            }

            var resP = ScanInbox(f, 0);
            if (resP != null)
            {
                return await resP;
            }

            if (timeout < 0)
            {
                return await ScanNoTimeout();
            }

            var ct = Task.Factory.CancellationToken;
            var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken.None);
            var timeoutTask = Task.Delay(timeout, timeoutCts.Token);

            return await Func(timeoutTask, timeoutCts);
        }

        internal async Task<T> Scan<T>(Func<TMsg, Task<T>> f, int timeout)
            where T : class
        {
            var resOpt = await TryScan(f, timeout);

            if (resOpt == null)
            {
                throw new TimeoutException("Mailbox scan timed out");
            }

            return resOpt;
        }

        internal async Task<TMsg> TryReceive(int timeout)
        {
            async Task<TMsg> ProcessFirstArrival()
            {
                while (true)
                {
                    var res = ReceiveFromArrivals();
                    if (res != null)
                    {
                        return res;
                    }

                    var ok = await WaitOne(timeout);
                    if (ok)
                    {
                        continue;
                    }

                    return default(TMsg);
                }
            }

            var resFromInbox = ReceiveFromInbox();
            if (resFromInbox == null)
            {
                return await ProcessFirstArrival();
            }

            return resFromInbox;
        }

        private async Task<bool> WaitOneNoTimeout()
        {
            if (_savedCont != null)
            {
                throw new Exception("multiple waiting reader continuations for mailbox");
            }

            bool descheduled;

            // An arrival may have happened while we're preparing to deschedule
            lock (_arrivals)
            {
                if (_arrivals.Count == 0)
                {
                    _savedCont = new TaskCompletionSource<bool>();
                    descheduled = true;
                }
                else
                {
                    descheduled = false;
                }
            }

            if (descheduled)
            {
                return await _savedCont.Task;
            }

            // If we didn't deschedule then run the continuation immediately
            return true;
        }

        private Task<bool> WaitOne(int timeout)
        {
            if (timeout < 0)
            {
                return WaitOneNoTimeout();
            }

            return AutoResetEvent.ToTask(TimeSpan.FromMilliseconds(timeout));
        }

        private T ScanArrivalsUnsafe<T>(Func<TMsg, T> f)
            where T : class
        {
            while (_arrivals.Count != 0)
            {
                var msg = _arrivals.Dequeue();
                var res = f(msg);
                if (res != null)
                {
                    return res;
                }

                Inbox.Add(msg);
            }

            return null;
        }

        private T ScanArrivals<T>(Func<TMsg, T> f)
            where T : class
        {
            lock (_arrivals)
            {
                return ScanArrivalsUnsafe(f);
            }
        }

        private T ScanInbox<T>(Func<TMsg, T> f, int n)
            where T : class
        {
            while (true)
            {
                if (_inboxStore == null)
                {
                    return null;
                }

                if (n >= Inbox.Count)
                {
                    return null;
                }

                var msg = Inbox[n];
                var res = f(msg);
                if (res == null)
                {
                    n = n + 1;
                    continue;
                }

                Inbox.RemoveAt(n);
                return res;
            }
        }
    }
}
