﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace RedCapped.Core
{
    public class QueueOf<T> : IQueueOf<T>
    {
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokenList;
        private readonly IMongoCollection<RedCappedMessage<T>> _collection;
        private readonly IMongoCollection<BsonDocument> _errorCollection;

        protected internal QueueOf(IMongoCollection<RedCappedMessage<T>> collection, IMongoCollection<BsonDocument> errorCollection)
        {
            _collection = collection;
            _errorCollection = errorCollection;
            _cancellationTokenList = new ConcurrentDictionary<string, CancellationTokenSource>();
            CreateIndex();
        }

        public void Subscribe(string topic, Func<T, bool> handler)
        {
            if (string.IsNullOrWhiteSpace(topic))
            {
                throw new ArgumentNullException("topic");
            }

            Task.Run(() => SubscribeInternal(topic, handler));
        }

        private async Task SubscribeInternal(string topic, Func<T, bool> handler)
        {
            var cancellationToken = new CancellationTokenSource();
            _cancellationTokenList[topic] = cancellationToken;

            try
            {
                var findOptions = new FindOptions<RedCappedMessage<T>>
                {
                    CursorType = CursorType.TailableAwait,
                    NoCursorTimeout = true
                };

                while (!cancellationToken.IsCancellationRequested)
                {
                    using (var cursor = await
                        _collection.FindAsync(x => x.Header.Type == typeof(T).ToString()
                                                   & x.Header.AcknowledgedAt == DateTime.MinValue
                                                   & x.Topic == topic, findOptions, cancellationToken.Token))
                    {
                        await cursor.ForEachAsync(async item =>
                        {
                            if (await AckAsync(item.MessageId))
                            {
                                if (!handler(item.Message))
                                {
                                    if (item.Header.RetryCount < 5)
                                    {
                                        await PublishAsync(item.Topic, item.Message);
                                    }
                                    else
                                    {
                                        await _errorCollection.InsertOneAsync(item.ToBsonDocument(), cancellationToken.Token);
                                    }
                                }
                            }
                            else
                            {
                                await _errorCollection.InsertOneAsync(item.ToBsonDocument(), cancellationToken.Token);
                            }
                        }, cancellationToken.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Cancellation Requested");
            }
        }

        public void Unsubscribe(string topic)
        {
            if (string.IsNullOrWhiteSpace(topic))
            {
                throw new ArgumentNullException("topic");
            }

            CancellationTokenSource t;
            if (_cancellationTokenList.TryRemove(topic, out t))
            {
                t.Cancel();
            }
        }

        public async Task<string> PublishAsync(string topic, T message)
        {
            if (string.IsNullOrWhiteSpace(topic))
            {
                throw new ArgumentNullException("topic");
            }

            var msg = new RedCappedMessage<T>(message)
            {
                MessageId = ObjectId.GenerateNewId().ToString(),
                Header = new MessageHeader<T>
                {
                    SentAt = DateTime.Now
                },
                Topic = topic
            };

            await _collection.InsertOneAsync(msg);

            return msg.MessageId;
        }

        private async void CreateIndex()
        {
            var options = new CreateIndexOptions
            {
                Background = true
            };

            await _collection.Indexes.CreateOneAsync(Builders<RedCappedMessage<T>>.IndexKeys
                .Ascending(x => x.Header.Type)
                .Ascending(x => x.Header.AcknowledgedAt)
                .Ascending(x => x.Topic), options);
        }

        private async Task<bool> AckAsync(string messageId)
        {
            var result = await _collection.UpdateOneAsync(
                x => x.MessageId == messageId
                     & x.Header.AcknowledgedAt == DateTime.MinValue,
                Builders<RedCappedMessage<T>>.Update
                    .Set(x => x.Header.AcknowledgedAt, DateTime.Now)
                    .Inc(x => x.Header.RetryCount, 1)
                );

            return result.MatchedCount == 1 && result.ModifiedCount == 1;
        }
    }
}