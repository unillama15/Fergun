﻿using Discord;
using Discord.Commands;
using Discord.Net;
using Discord.WebSocket;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fergun.Services
{
    /// <summary>
    /// A thread-safe class used to automatically modify or delete response messages when the command message is modified or deleted.
    /// </summary>
    public class CommandCacheService : IDisposable
    {
        private readonly ConcurrentDictionary<ulong, ulong> _cache = new ConcurrentDictionary<ulong, ulong>();
        private readonly int _max;
        private Timer _autoClear;
        private readonly Func<LogMessage, Task> _logger;
        private int _count;
        private bool _disposed;
        private readonly DiscordSocketClient _client;
        private readonly Func<SocketMessage, Task> _cmdHandler;
        private readonly double _maxMessageTime;

        /// <summary>
        /// Initialises the cache with a maximum capacity, tracking the client's message deleted event, and optionally the client's message modified event.
        /// </summary>
        /// <param name="client">The client that the MessageDeleted handler should be hooked up to.</param>
        /// <param name="capacity">The maximum capacity of the cache.</param>
        /// <param name="cmdHandler">An optional method that gets called when the modified message event is fired.</param>
        /// <param name="logger">An optional method to use for logging.</param>
        /// <param name="period">The interval between invocations of the cache clearing, in milliseconds.</param>
        /// <param name="maxMessageTime">The max. message longevity, in hours.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if capacity is less than 1.</exception>
        public CommandCacheService(DiscordSocketClient client, int capacity = 200, Func<SocketMessage, Task> cmdHandler = null,
            Func<LogMessage, Task> logger = null, int period = 1800000, double maxMessageTime = 2.0)
        {
            _client = client;

            _client.MessageDeleted += OnMessageDeleted;
            _client.MessageUpdated += OnMessageModified;

            // If a method is supplied, use it, otherwise use a method that does nothing.
            _cmdHandler = cmdHandler ?? (_ => Task.CompletedTask);
            _logger = logger ?? (_ => Task.CompletedTask);

            // Make sure the max capacity is within an acceptable range, use it if it is.
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity can not be lower than 1.");
            }
            else
            {
                _max = capacity;
            }
            _maxMessageTime = maxMessageTime;

            // Create a timer that will clear out cached messages.
            _autoClear = new Timer(OnTimerFired, null, period, period);

            _logger(new LogMessage(LogSeverity.Info, "CmdCache", $"Service initialised, MessageDeleted and OnMessageModified event handlers registered."));
        }

        /// <summary>
        /// Gets all the keys in the cache. Will claim all locks until the operation is complete.
        /// </summary>
        public IEnumerable<ulong> Keys => _cache.Keys;

        /// <summary>
        /// Gets all the values in the cache. Will claim all locks until the operation is complete.
        /// </summary>
        public IEnumerable<ulong> Values => _cache.Values;

        /// <summary>
        /// Gets the number of command/response sets in the cache.
        /// </summary>
        public int Count => _count;

        /// <summary>
        /// Adds a key and a value to the cache, or update the value if the key already exists.
        /// </summary>
        /// <param name="key">The id of the command message.</param>
        /// <param name="value">The ids of the response messages.</param>
        /// <exception cref="ArgumentNullException">Thrown if values is null.</exception>
        public void Add(ulong key, ulong value)
        {
            if (_count >= _max)
            {
                int removeCount = _count - _max + 1;
                // The left 42 bits represent the timestamp.
                var orderedKeys = _cache.Keys.OrderBy(k => k >> 22).ToList();
                // Remove items until we're under the maximum.
                int successfulRemovals = 0;
                foreach (var orderedKey in orderedKeys)
                {
                    if (successfulRemovals >= removeCount) break;

                    var success = TryRemove(orderedKey);
                    if (success) successfulRemovals++;
                }

                // Reset _count to _cache.Count.
                UpdateCount();
            }

            // TryAdd will return false if the key already exists, in which case we don't want to increment the count.
            if (!_cache.ContainsKey(value))
            {
                Interlocked.Increment(ref _count);
            }
            _cache[key] = value;
        }

        /// <summary>
        /// Adds a new set to the cache, or extends the existing values if the key already exists.
        /// </summary>
        /// <param name="pair">The key, and its values.</param>
        public void Add(KeyValuePair<ulong, ulong> pair) => Add(pair.Key, pair.Value);

        /// <summary>
        /// Adds a command message and response to the cache using the message objects.
        /// </summary>
        /// <param name="command">The message that invoked the command.</param>
        /// <param name="response">The response to the command message.</param>
        public void Add(IUserMessage command, IUserMessage response) => Add(command.Id, response.Id);

        /// <summary>
        /// Clears all items from the cache. Will claim all locks until the operation is complete.
        /// </summary>
        public void Clear()
        {
            _cache.Clear();
            Interlocked.Exchange(ref _count, 0);
        }

        /// <summary>
        /// Checks whether the cache contains a set with a certain key.
        /// </summary>
        /// <param name="key">The key to search for.</param>
        /// <returns>Whether or not the key was found.</returns>
        public bool ContainsKey(ulong key) => _cache.ContainsKey(key);

        /// <summary>
        /// Returns an enumerator that iterates through the cache.
        /// </summary>
        /// <returns>An enumerator for the cache.</returns>
        public IEnumerator<KeyValuePair<ulong, ulong>> GetEnumerator() => _cache.GetEnumerator();

        /// <summary>
        /// Tries to remove a value from the cache by key.
        /// </summary>
        /// <param name="key">The key to search for.</param>
        /// <returns>Whether or not the removal operation was successful.</returns>
        public bool TryRemove(ulong key)
        {
            var success = _cache.TryRemove(key, out _);
            if (success) Interlocked.Decrement(ref _count);
            return success;
        }

        /// <summary>
        /// Tries to get a value from the cache by key.
        /// </summary>
        /// <param name="key">The key to search for.</param>
        /// <param name="value">The value if found.</param>
        /// <returns>Whether or not key was found in the cache.</returns>
        public bool TryGetValue(ulong key, out ulong value) => _cache.TryGetValue(key, out value);

        /// <summary>
        /// Safely disposes of the auto-clear timer
        /// and unsubscribes from the <see cref="BaseSocketClient.MessageDeleted"/> and <see cref="BaseSocketClient.MessageUpdated"/> events.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CommandCacheService), "Service has been disposed.");
            }
            else if (disposing)
            {
                _autoClear.Dispose();
                _autoClear = null;

                _client.MessageDeleted -= OnMessageDeleted;
                _client.MessageUpdated -= OnMessageModified;
                _disposed = true;

                _logger(new LogMessage(LogSeverity.Info, "CmdCache", "Cache disposed successfully."));
            }
        }

        private void UpdateCount() => Interlocked.Exchange(ref _count, _cache.Count);

        private void OnTimerFired(object state)
        {
            // Get all messages where the timestamp is older than 2 hours, then convert it to a list. The result of where merely contains references to the original
            // collection, so iterating and removing will throw an exception. Converting it to a list first avoids this.
            var purge = _cache.Where(p =>
            {
                TimeSpan difference = DateTimeOffset.UtcNow - SnowflakeUtils.FromSnowflake(p.Key);

                return difference.TotalHours >= _maxMessageTime;
            }).ToList();

            var removed = purge.Where(p => TryRemove(p.Key));

            UpdateCount();

            _logger(new LogMessage(LogSeverity.Verbose, "CmdCache", $"Cleaned {removed.Count()} item(s) from the cache."));
        }

        private Task OnMessageDeleted(Cacheable<IMessage, ulong> cacheable, ISocketMessageChannel channel)
        {
            _ = Task.Run(async () =>
            {
                if (TryGetValue(cacheable.Id, out ulong messageId))
                {
                    var message = await channel.GetMessageAsync(messageId);
                    if (message != null)
                    {
                        await message.DeleteAsync();
                    }
                    else
                    {
                        await _logger(new LogMessage(LogSeverity.Warning, "CmdCache", $"{cacheable.Id} deleted but {messageId} does not exist."));
                    }
                    TryRemove(cacheable.Id);
                }
            });

            return Task.CompletedTask;
        }

        private Task OnMessageModified(Cacheable<IMessage, ulong> cacheable, SocketMessage after, ISocketMessageChannel channel)
        {
            _ = Task.Run(async () =>
            {
                // Prevent the double reply that happens when the message is "updated" with an embed or image/video preview.
                if (after?.Content == null || after.Author.IsBot) return;
                IMessage before = null;
                try
                {
                    before = await cacheable.GetOrDownloadAsync();
                }
                catch (HttpException)
                {
                }
                if (before?.Content == null || before.Content == after.Content) return;

                if (TryGetValue(cacheable.Id, out ulong responseId))
                {
                    var response = await channel.GetMessageAsync(responseId);
                    if (response == null)
                    {
                        await _logger(new LogMessage(LogSeverity.Warning, "CmdCache", $"A message ({cacheable.Id}) associated to a response was found but the response ({responseId}) was already deleted."));
                        TryRemove(cacheable.Id);
                    }
                    else
                    {
                        if (response.Attachments.Count > 0)
                        {
                            await _logger(new LogMessage(LogSeverity.Warning, "CmdCache", $"Attachment found on response ({responseId}). Deleting the message..."));
                            _ = response.DeleteAsync();
                            TryRemove(cacheable.Id);
                        }
                        else
                        {
                            await _logger(new LogMessage(LogSeverity.Verbose, "CmdCache", $"Found a response associated to message {cacheable.Id} in cache."));
                            if (response.Reactions.Count > 0)
                            {
                                bool manageMessages = response.Author is IGuildUser guildUser && guildUser.GetPermissions((IGuildChannel)response.Channel).ManageMessages;

                                // RemoveReactionsAsync() is slow...
                                if (manageMessages)
                                    await response.RemoveAllReactionsAsync();
                                else
                                    await (response as IUserMessage).RemoveReactionsAsync(response.Author, response.Reactions.Where(x => x.Value.IsMe).Select(x => x.Key).ToArray());
                            }
                        }
                    }
                }
                _ = _cmdHandler(after);
            });

            return Task.CompletedTask;
        }
    }

    public abstract class CommandCacheModuleBase<TCommandContext> : ModuleBase<TCommandContext>
        where TCommandContext : class, ICommandContext
    {

        public CommandCacheService Cache { get; set; }

        /// <summary>
        /// Sends or edits a message to the source channel, and adds the response to the cache if the message is new.
        /// </summary>
        /// <param name="message">The message to be sent or edited.</param>
        /// <param name="isTTS">Whether the message should be read aloud by Discord or not.</param>
        /// <param name="embed">The <see cref="EmbedType.Rich"/> <see cref="Embed"/> to be sent or edited.</param>
        /// <param name="options">The options to be used when sending the request.</param>
        /// <param name="allowedMentions">
        /// Specifies if notifications are sent for mentioned users and roles in the message <paramref name="text"/>. If <c>null</c>, all mentioned roles and users will be notified.
        /// </param>
        /// <returns>A task that represents an asynchronous operation for sending or editing the message. The task contains the sent or edited message.</returns>
        protected override async Task<IUserMessage> ReplyAsync(string message = null, bool isTTS = false, Embed embed = null, RequestOptions options = null, AllowedMentions allowedMentions = null)
        {
            IUserMessage response;
            bool found = Cache.TryGetValue(Context.Message.Id, out ulong messageId);
            if (found && (response = (IUserMessage)await Context.Channel.GetMessageAsync(messageId)) != null)
            {
                await response.ModifyAsync(x =>
                {
                    x.Content = message;
                    x.Embed = embed;
                }).ConfigureAwait(false);

                response = (IUserMessage)await Context.Channel.GetMessageAsync(messageId).ConfigureAwait(false);
            }
            else
            {
                response = await Context.Channel.SendMessageAsync(message, isTTS, embed, options, allowedMentions).ConfigureAwait(false);
                Cache.Add(Context.Message, response);
            }
            return response;
        }
    }

    public static class CommandCacheExtensions
    {
        /// <summary>
        /// Sends a file to this message channel with an optional caption, then adds it to the command cache.
        /// </summary>
        /// <param name="cache">The command cache that the messages should be added to.</param>
        /// <param name="commandId">The ID of the command message.</param>
        /// <param name="stream">The <see cref="Stream" /> of the file to be sent.</param>
        /// <param name="text">The message to be sent.</param>
        /// <param name="isTTS">Whether the message should be read aloud by Discord or not.</param>
        /// <param name="embed">The <see cref="EmbedType.Rich"/> <see cref="Embed"/> to be sent.</param>
        /// <param name="options">The options to be used when sending the request.</param>
        /// <param name="isSpoiler">Whether the message attachment should be hidden as a spoiler.</param>
        /// <param name="allowedMentions">
        /// Specifies if notifications are sent for mentioned users and roles in the message <paramref name="text"/>. If <c>null</c>, all mentioned roles and users will be notified.
        /// </param>
        /// <returns>A task that represents an asynchronous send operation for delivering the message. The task result contains the sent message.</returns>
        public static async Task<IUserMessage> SendCachedFileAsync(this IMessageChannel channel, CommandCacheService cache, ulong commandId, Stream stream, string filename,
            string text = null, bool isTTS = false, Embed embed = null, RequestOptions options = null, bool isSpoiler = false, AllowedMentions allowedMentions = null)
        {
            var response = await channel.SendFileAsync(stream, filename, text, isTTS, embed, options, isSpoiler, allowedMentions);

            if (cache.ContainsKey(commandId))
            {
                cache.TryRemove(commandId);
            }
            cache.Add(commandId, response.Id);

            return response;
        }
    }
}