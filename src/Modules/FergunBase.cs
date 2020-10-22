﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Fergun.Interactive;
using Fergun.Services;
using Fergun.Utils;

namespace Fergun.Modules
{
    // TODO: Cleanup
    public abstract class FergunBase : FergunBase<SocketCommandContext>
    {
    }

    public abstract class FergunBase<T> : CommandCacheModuleBase<T>//ModuleBase<T>
        where T : SocketCommandContext
    {
        /// <summary>
        /// Gets or sets the interactive service.
        /// </summary>
        public InteractiveService Interactive { get; set; }

        public Task<SocketMessage> NextMessageAsync(ICriterion<SocketMessage> criterion, TimeSpan? timeout = null)
            => Interactive.NextMessageAsync(Context, criterion, timeout);

        public Task<SocketMessage> NextMessageAsync(bool fromSourceUser = true, bool inSourceChannel = true, TimeSpan? timeout = null)
            => Interactive.NextMessageAsync(Context, fromSourceUser, inSourceChannel, timeout);

        public Task<IUserMessage> ReplyAndDeleteAsync(string content, bool isTTS = false, Embed embed = null, TimeSpan? timeout = null, RequestOptions options = null)
            => Interactive.ReplyAndDeleteAsync(Context, content, isTTS, embed, timeout, options);

        public async Task<IUserMessage> InlineReactionReplyAsync(ReactionCallbackData data, bool fromSourceUser = true)
        {
            var response = await Interactive.SendMessageWithReactionCallbacksAsync(Context, data, fromSourceUser);

            Cache.Add(Context.Message, response);
            return response;
        }

        public Task<IUserMessage> PagedReplyAsync(IEnumerable<object> list, bool fromSourceUser = true)
        {
            PaginatedMessage pager = new PaginatedMessage();
            List<PaginatedMessage.Page> pages = new List<PaginatedMessage.Page>();
            foreach (var obj in list)
            {
                pages.Add(new PaginatedMessage.Page { Description = obj.ToString() });
            }
            pager.Pages = pages;

            var criterion = new Criteria<SocketReaction>();
            if (fromSourceUser)
                criterion.AddCriterion(new EnsureReactionFromSourceUserCriterion());
            return PagedReplyAsync(pager, criterion, new ReactionList());
        }

        public Task<IUserMessage> PagedReplyAsync(PaginatedMessage pager, ReactionList Reactions, bool fromSourceUser = true)
        {
            var criterion = new Criteria<SocketReaction>();
            if (fromSourceUser)
                criterion.AddCriterion(new EnsureReactionFromSourceUserCriterion());
            return PagedReplyAsync(pager, criterion, Reactions);
        }

        public async Task<IUserMessage> PagedReplyAsync(PaginatedMessage pager, ICriterion<SocketReaction> criterion, ReactionList Reactions)
        {
            IUserMessage response;
            if (Cache.TryGetValue(Context.Message.Id, out ulong messageId))
            {
                response = (IUserMessage)await Context.Channel.GetMessageAsync(messageId).ConfigureAwait(false);

                response = await Interactive.SendPaginatedMessageAsync(Context, pager, Reactions, criterion, response).ConfigureAwait(false);
            }
            else
            {
                response = await Interactive.SendPaginatedMessageAsync(Context, pager, Reactions, criterion).ConfigureAwait(false);
                Cache.Add(Context.Message, response);
            }

            return response;

            //var response = await Interactive.SendPaginatedMessageAsync(Context, pager, Reactions, criterion);

            //Cache.Add(Context.Message, response);
            //return response;
        }

        public string GetPrefix() => GuildUtils.GetPrefix(Context.Channel);

        public GuildConfig GetGuildConfig() => GuildUtils.GetGuildConfig(Context.Channel);

        public string GetLanguage() => GuildUtils.GetLanguage(Context.Channel);

        public string Locate(string key) => GuildUtils.Locate(key, Context.Channel);

        public string Locate(bool boolean) => GuildUtils.Locate(boolean ? "Yes" : "No", Context.Channel);

        public string Locate(string key, string language) => GuildUtils.Locate(key, language);

        protected override async Task<IUserMessage> ReplyAsync(string message = null, bool isTTS = false, Embed embed = null, RequestOptions options = null, AllowedMentions allowedMentions = null)
        {
            var response = await base.ReplyAsync(message, isTTS, embed, options, allowedMentions);
            Interactive.RemoveReactionCallback(response.Id);
            return response;
        }

        /// <summary>
        /// Sends or edits an embed to the channel the command was invoked in, and adds the response to the cache if the message is new.
        /// </summary>
        /// <param name="text">The message's rich embed.</param>
        /// <returns>A task that represents the send or edit operation. The task contains the sent or edited message.</returns>
        public async Task<IUserMessage> SendEmbedAsync(string text)
        {
            var builder = new EmbedBuilder()
                .WithDescription(text)
                .WithColor(FergunConfig.EmbedColor);

            return await ReplyAsync(embed: builder.Build());
        }

        private class EnsureReactionFromSourceUserCriterion : ICriterion<SocketReaction>
        {
            public Task<bool> JudgeAsync(SocketCommandContext sourceContext, SocketReaction parameter)
            {
                return Task.FromResult(parameter.UserId == sourceContext.User.Id);
            }
        }
    }
}