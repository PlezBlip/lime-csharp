﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lime.Protocol.Network.Modules
{
    /// <summary>
    /// Defines a module that resend messages that
    /// </summary>
    public sealed class ResendMessagesChannelModule : IChannelModule<Message>, IChannelModule<Notification>, IDisposable
    {
        private readonly IChannel _channel;
        private readonly int _resendMessageTryCount;
        private readonly TimeSpan _resendMessageInterval;
        private readonly ConcurrentDictionary<Guid, SentMessage> _sentMessageDictionary;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly object _syncRoot = new object();

        private Task _resendMessagesTask;

        internal ResendMessagesChannelModule(IChannel channel, int resendMessageTryCount, TimeSpan resendMessageInterval)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            if (resendMessageTryCount <= 0) throw new ArgumentOutOfRangeException(nameof(resendMessageTryCount));
            _channel = channel;
            _resendMessageTryCount = resendMessageTryCount;
            _resendMessageInterval = resendMessageInterval;

            _sentMessageDictionary = new ConcurrentDictionary<Guid, SentMessage>();
            _cancellationTokenSource = new CancellationTokenSource();
        }

        public void OnStateChanged(SessionState state)
        {
            lock (_syncRoot)
            {
                if (state == SessionState.Established &&
                    _resendMessagesTask == null)
                {
                    _resendMessagesTask =
                        Task.Factory.StartNew(ResendMessagesAsync)
                            .Unwrap();
                }
                else if (state > SessionState.Established &&
                         !_cancellationTokenSource.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel();
                }
            }
        }

        public static void Register(IChannel channel, int resendMessageTryCount, TimeSpan resendMessageInterval)
        {
            var resendMessagesChannelModule = new ResendMessagesChannelModule(channel, resendMessageTryCount, resendMessageInterval);
            channel.MessageModules.Add(resendMessagesChannelModule);
            channel.NotificationModules.Add(resendMessagesChannelModule);
        }

        Task<Notification> IChannelModule<Notification>.OnReceiving(Notification envelope, CancellationToken cancellationToken)
        {
            if (envelope.Event == Event.Received ||
                envelope.Event == Event.Failed)
            {                
                SentMessage sentMessage;
                _sentMessageDictionary.TryRemove(envelope.Id, out sentMessage);                
            }

            return envelope.AsCompletedTask();
        }

        Task<Notification> IChannelModule<Notification>.OnSending(Notification envelope, CancellationToken cancellationToken)
        {
            return envelope.AsCompletedTask();
        }

        Task<Message> IChannelModule<Message>.OnReceiving(Message envelope, CancellationToken cancellationToken)
        {
            return envelope.AsCompletedTask();
        }

        Task<Message> IChannelModule<Message>.OnSending(Message envelope, CancellationToken cancellationToken)
        {
            if (envelope.Id != Guid.Empty && 
                _resendMessagesTask?.Status == TaskStatus.Running)
            {
                _sentMessageDictionary.TryAdd(envelope.Id, new SentMessage(envelope));
            }

            return envelope.AsCompletedTask();
        }

        private async Task ResendMessagesAsync()
        {
            while (_cancellationTokenSource.IsCancellationRequested &&
                _channel.State == SessionState.Established && 
                _channel.Transport.IsConnected)
            {
                try
                {
                    await Task.Delay(_resendMessageInterval, _cancellationTokenSource.Token);

                    if (_channel.State != SessionState.Established || !_channel.Transport.IsConnected) continue;

                    var referenceDate = DateTimeOffset.UtcNow - _resendMessageInterval;
                    var messageIds = _sentMessageDictionary
                        .Where(s => s.Value.SentDate <= referenceDate)
                        .Select(s => s.Key)
                        .ToArray();

                    foreach (var messageId in messageIds)
                    {
                        _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                        SentMessage sentMessage;
                        if (_sentMessageDictionary.TryGetValue(messageId, out sentMessage))
                        {
                            await _channel.SendMessageAsync(sentMessage.Message);
                            if (sentMessage.SentCount > _resendMessageTryCount)
                            {
                                _sentMessageDictionary.TryRemove(messageId, out sentMessage);
                            }
                            else
                            {
                                sentMessage.IncrementSentCount();
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!_cancellationTokenSource.IsCancellationRequested) throw;
                    break;
                }
            }
        }

        private class SentMessage
        {
            private readonly Message _message;
            private const string SENT_COUNT_KEY = "#sendCount";

            public SentMessage(Message message)
                : this(message, DateTimeOffset.UtcNow, 1)
            {

            }

            private SentMessage(Message message, DateTimeOffset sentDate, int sentCount)
            {
                if (message == null) throw new ArgumentNullException(nameof(Message));
                _message = message;
                SentDate = sentDate;
                SentCount = sentCount;
            }

            public Message Message
            {
                get
                {
                    if (_message.Metadata == null) _message.Metadata = new Dictionary<string, string>();
                    _message.Metadata.Remove(SENT_COUNT_KEY);
                    _message.Metadata.Add(SENT_COUNT_KEY, SentCount.ToString());
                    return _message;
                }
            }

            public DateTimeOffset SentDate { get; private set; }

            public int SentCount { get; private set; }

            public void IncrementSentCount()
            {
                SentCount++;
                SentDate = DateTimeOffset.UtcNow;
            }
        }


        public void Dispose()
        {
            _cancellationTokenSource.Dispose();
            _resendMessagesTask?.Dispose();
        }
    }
}