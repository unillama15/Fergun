﻿using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Discord;
using Discord.Interactions;
using Fergun.Extensions;
using Fergun.Interactive;
using Fergun.Interactive.Pagination;
using Fergun.Modules.Handlers;
using Fergun.Utils;
using GScraper;
using GScraper.Brave;
using GScraper.DuckDuckGo;
using GScraper.Google;
using GTranslate;
using GTranslate.Results;
using GTranslate.Translators;
using Humanizer;
using Microsoft.Extensions.Logging;
using YoutubeExplode.Common;
using YoutubeExplode.Search;

namespace Fergun.Modules;

public class UtilityModule : InteractionModuleBase<ShardedInteractionContext>
{
    private readonly ILogger<UtilityModule> _logger;
    private readonly InteractiveService _interactive;
    private readonly AggregateTranslator _translator;
    private readonly GoogleScraper _googleScraper;
    private readonly DuckDuckGoScraper _duckDuckGoScraper;
    private readonly BraveScraper _braveScraper;
    private readonly SearchClient _searchClient;

    public UtilityModule(ILogger<UtilityModule> logger, InteractiveService interactive, AggregateTranslator translator, GoogleScraper googleScraper,
        DuckDuckGoScraper duckDuckGoScraper, BraveScraper braveScraper, SearchClient searchClient)
    {
        _logger = logger;
        _interactive = interactive;
        _translator = translator;
        _googleScraper = googleScraper;
        _duckDuckGoScraper = duckDuckGoScraper;
        _braveScraper = braveScraper;
        _searchClient = searchClient;
    }

    [RequireOwner]
    [SlashCommand("cmd", "(Owner only) Executes a command.")]
    public async Task Cmd([Summary(description: "The command to execute")] string command, [Summary("noembed", "No embed.")] bool noEmbed = false)
    {
        await DeferAsync();

        var result = CommandUtils.RunCommand(command);

        if (string.IsNullOrWhiteSpace(result))
        {
            await FollowupAsync("No output.");
        }
        else
        {
            int limit = noEmbed ? DiscordConfig.MaxMessageSize : EmbedBuilder.MaxDescriptionLength;
            string sanitized = Format.Code(result.Replace('`', '´').Truncate(limit - 12), "ansi");
            string? text = null;
            Embed? embed = null;

            if (noEmbed)
            {
                text = sanitized;
            }
            else
            {
                embed = new EmbedBuilder()
                    .WithTitle("Command output")
                    .WithDescription(sanitized)
                    .WithColor(Color.Orange)
                    .Build();
            }

            await FollowupAsync(text, embed: embed);
        }
    }

    [SlashCommand("help", "Information about Fergun 2")]
    public async Task Help()
    {
        var embed = new EmbedBuilder()
            .WithTitle("Fergun 2")
            .WithDescription("Hey, it seems that you found some slash commands in Fergun.\n\n" +
                             "This is Fergun 2, a complete rewrite of Fergun 1.x, using only slash commands.\n" +
                             "Fergun 2 is still in very alpha stages and only some commands are present, but more commands will be added soon.\n" +
                             "Fergun 2 will be finished in early 2022 and it will include new features and commands.\n\n" +
                             "Some modules and commands are currently in maintenance mode in Fergun 1.x and they won't be migrated to Fergun 2. These modules are:\n" +
                             "- **AI Dungeon** module\n" +
                             "- **Music** module\n" +
                             "- **Snipe** commands\n\n" +
                             $"You can find more info about the removals of these modules/commands {Format.Url("here", "https://github.com/d4n3436/Fergun/wiki/Command-removal-notice")}.")
            .WithColor(Color.Orange)
            .Build();

        await RespondAsync(embed: embed);
    }

    [SlashCommand("ping", "Sends the response time of the bot.")]
    public async Task Ping()
    {
        var embed = new EmbedBuilder()
            .WithDescription("Pong!")
            .WithColor(Color.Orange)
            .Build();

        var sw = Stopwatch.StartNew();
        await RespondAsync(embed: embed);
        sw.Stop();

        embed = new EmbedBuilder()
            .WithDescription($"Pong! {sw.ElapsedMilliseconds}ms")
            .WithColor(Color.Orange)
            .Build();

        await Context.Interaction.ModifyOriginalResponseAsync(x => x.Embed = embed);
    }

    [SlashCommand("img", "Searches for images from Google Images and displays them in a paginator.")]
    public async Task Img([Autocomplete(typeof(GoogleAutocompleteHandler))] [Summary(description: "The query to search.")] string query,
        [Summary(description: "Whether to display multiple images in a single page.")] bool multiImages = false)
    {
        await DeferAsync();

        bool isNsfw = Context.Channel.IsNsfw();
        _logger.LogInformation(new EventId(0, "img"), "Query: \"{query}\", is NSFW: {isNsfw}", query, isNsfw);

        var images = await _googleScraper.GetImagesAsync(query, isNsfw ? SafeSearchLevel.Off : SafeSearchLevel.Strict, language: Context.Interaction.GetTwoLetterLanguageCode());

        var filteredImages = images
            .Where(x => x.Url.StartsWith("http") && x.SourceUrl.StartsWith("http"))
            .Chunk(multiImages ? 4 : 1)
            .ToArray();

        _logger.LogInformation(new EventId(0, "img"), "Image results: {count}", filteredImages.Length);

        if (filteredImages.Length == 0)
        {
            await Context.Interaction.FollowupWarning("No results.");
            return;
        }

        var paginator = new LazyPaginatorBuilder()
            .WithPageFactory(GeneratePage)
            .WithFergunEmotes()
            .WithActionOnCancellation(ActionOnStop.DisableInput)
            .WithActionOnTimeout(ActionOnStop.DisableInput)
            .WithMaxPageIndex(filteredImages.Length - 1)
            .WithFooter(PaginatorFooter.None)
            .AddUser(Context.User)
            .Build();

        _ = _interactive.SendPaginatorAsync(paginator, Context.Interaction, TimeSpan.FromMinutes(10), InteractionResponseType.DeferredChannelMessageWithSource);

        MultiEmbedPageBuilder GeneratePage(int index)
        {
            var builders = filteredImages[index].Select(result => new EmbedBuilder()
                .WithTitle(result.Title)
                .WithDescription("Google Images results")
                .WithUrl(multiImages ? "https://google.com" : result.SourceUrl)
                .WithImageUrl(result.Url)
                .WithFooter($"Page {index + 1}/{filteredImages.Length}", Constants.GoogleLogoUrl)
                .WithColor(Color.Orange));

            return new MultiEmbedPageBuilder().WithBuilders(builders);
        }
    }

    [SlashCommand("img2", "Searches for images from DuckDuckGo and displays them in a paginator.")]
    public async Task Img2([Autocomplete(typeof(DuckDuckGoAutocompleteHandler))] [Summary(description: "The query to search.")] string query)
    {
        await DeferAsync();

        bool isNsfw = Context.Channel.IsNsfw();
        _logger.LogInformation(new EventId(0, "img2"), "Query: \"{query}\", is NSFW: {isNsfw}", query, isNsfw);

        var images = await _duckDuckGoScraper.GetImagesAsync(query, isNsfw ? SafeSearchLevel.Off : SafeSearchLevel.Strict);

        var filteredImages = images
            .Where(x => x.Url.StartsWith("http") && x.SourceUrl.StartsWith("http"))
            .ToArray();

        _logger.LogInformation(new EventId(0, "img2"), "Image results: {count}", filteredImages.Length);

        if (filteredImages.Length == 0)
        {
            await Context.Interaction.FollowupWarning("No results.");
            return;
        }

        var paginator = new LazyPaginatorBuilder()
            .WithPageFactory(GeneratePageAsync)
            .WithFergunEmotes()
            .WithActionOnCancellation(ActionOnStop.DisableInput)
            .WithActionOnTimeout(ActionOnStop.DisableInput)
            .WithMaxPageIndex(filteredImages.Length - 1)
            .WithFooter(PaginatorFooter.None)
            .AddUser(Context.User)
            .Build();

        _ = _interactive.SendPaginatorAsync(paginator, Context.Interaction, TimeSpan.FromMinutes(10), InteractionResponseType.DeferredChannelMessageWithSource);

        Task<PageBuilder> GeneratePageAsync(int index)
        {
            var pageBuilder = new PageBuilder()
                .WithTitle(filteredImages[index].Title)
                .WithDescription("DuckDuckGo image search")
                .WithUrl(filteredImages[index].SourceUrl)
                .WithImageUrl(filteredImages[index].Url)
                .WithFooter($"Page {index + 1}/{filteredImages.Length}", Constants.DuckDuckGoLogoUrl)
                .WithColor(Color.Orange);

            return Task.FromResult(pageBuilder);
        }
    }

    [SlashCommand("img3", "Searches for images from Brave and displays them in a paginator.")]
    public async Task Img3([Autocomplete(typeof(BraveAutocompleteHandler))] [Summary(description: "The query to search.")] string query)
    {
        await DeferAsync();

        bool isNsfw = Context.Channel.IsNsfw();
        _logger.LogInformation(new EventId(0, "img3"), "Query: \"{query}\", is NSFW: {isNsfw}", query, isNsfw);

        var images = await _braveScraper.GetImagesAsync(query, isNsfw ? SafeSearchLevel.Off : SafeSearchLevel.Strict);

        var filteredImages = images
            .Where(x => x.Url.StartsWith("http") && x.SourceUrl.StartsWith("http"))
            .ToArray();

        _logger.LogInformation(new EventId(0, "img3"), "Image results: {count}", filteredImages.Length);

        if (filteredImages.Length == 0)
        {
            await Context.Interaction.FollowupWarning("No results.");
            return;
        }

        var paginator = new LazyPaginatorBuilder()
            .WithPageFactory(GeneratePageAsync)
            .WithFergunEmotes()
            .WithActionOnCancellation(ActionOnStop.DisableInput)
            .WithActionOnTimeout(ActionOnStop.DisableInput)
            .WithMaxPageIndex(filteredImages.Length - 1)
            .WithFooter(PaginatorFooter.None)
            .AddUser(Context.User)
            .Build();

        _ = _interactive.SendPaginatorAsync(paginator, Context.Interaction, TimeSpan.FromMinutes(10), InteractionResponseType.DeferredChannelMessageWithSource);

        Task<PageBuilder> GeneratePageAsync(int index)
        {
            var pageBuilder = new PageBuilder()
                .WithTitle(filteredImages[index].Title)
                .WithDescription("Brave image search")
                .WithUrl(filteredImages[index].SourceUrl)
                .WithImageUrl(filteredImages[index].Url)
                .WithFooter($"Page {index + 1}/{filteredImages.Length}", Constants.BraveLogoUrl)
                .WithColor(Color.Orange);

            return Task.FromResult(pageBuilder);
        }
    }

    [SlashCommand("say", "Says something.")]
    public async Task Say([Summary(description: "The text to send.")] string text)
    {
        await RespondAsync(text.Truncate(DiscordConfig.MaxMessageSize), allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("stats", "Sends the stats of the bot.")]
    public async Task Stats()
    {
        await DeferAsync();

        long temp;
        var owner = (await Context.Client.GetApplicationInfoAsync()).Owner;
        var cpuUsage = (int)await CommandUtils.GetCpuUsageForProcessAsync();
        string? cpu = null;
        long? totalRamUsage = null;
        long processRamUsage = 0;
        long? totalRam = null;
        string? os = RuntimeInformation.OSDescription;

        if (OperatingSystem.IsLinux())
        {
            // CPU Name
            if (File.Exists("/proc/cpuinfo"))
            {
                cpu = File.ReadAllLines("/proc/cpuinfo")
                    .FirstOrDefault(x => x.StartsWith("model name", StringComparison.OrdinalIgnoreCase))?
                    .Split(':')
                    .ElementAtOrDefault(1)?
                    .Trim();
            }

            if (string.IsNullOrWhiteSpace(cpu))
            {
                cpu = CommandUtils.RunCommand("lscpu")?
                    .Split('\n')
                    .FirstOrDefault(x => x.StartsWith("model name", StringComparison.OrdinalIgnoreCase))?
                    .Split(':')
                    .ElementAtOrDefault(1)?
                    .Trim();

                if (string.IsNullOrWhiteSpace(cpu))
                {
                    cpu = "?";
                }
            }

            // OS Name
            if (File.Exists("/etc/lsb-release"))
            {
                var distroInfo = File.ReadAllLines("/etc/lsb-release");
                os = distroInfo.ElementAtOrDefault(3)?.Split('=').ElementAtOrDefault(1)?.Trim('\"');
            }

            // Total RAM & total RAM usage
            var output = CommandUtils.RunCommand("free -m")?.Split(Environment.NewLine);
            var memory = output?.ElementAtOrDefault(1)?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (long.TryParse(memory?.ElementAtOrDefault(1), out temp)) totalRam = temp;
            if (long.TryParse(memory?.ElementAtOrDefault(2), out temp)) totalRamUsage = temp;

            // Process RAM usage
            processRamUsage = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
        }
        else if (OperatingSystem.IsWindows())
        {
            // CPU Name
            cpu = CommandUtils.RunCommand("wmic cpu get name")
                ?.Trim()
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .ElementAtOrDefault(1);

            // Total RAM & total RAM usage
            var output = CommandUtils.RunCommand("wmic OS get FreePhysicalMemory,TotalVisibleMemorySize /Value")
                ?.Trim()
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

            if (output?.Length > 1)
            {
                long freeRam = 0;
                var split = output[0].Split('=', StringSplitOptions.RemoveEmptyEntries);
                if (split.Length > 1 && long.TryParse(split[1], out temp))
                {
                    freeRam = temp / 1024;
                }

                split = output[1].Split('=', StringSplitOptions.RemoveEmptyEntries);
                if (split.Length > 1 && long.TryParse(split[1], out temp))
                {
                    totalRam = temp / 1024;
                }

                if (totalRam != null && freeRam != 0)
                {
                    totalRamUsage = totalRam - freeRam;
                }
            }

            // Process RAM usage
            processRamUsage = Process.GetCurrentProcess().PrivateMemorySize64 / 1024 / 1024;
        }

        int totalUsers = 0;
        foreach (var guild in Context.Client.Guilds)
        {
            totalUsers += guild.MemberCount;
        }

        int totalUsersInShard = 0;
        int shardId = Context.Channel.IsPrivate() ? 0 : Context.Client.GetShardIdFor(Context.Guild);
        foreach (var guild in Context.Client.GetShard(shardId).Guilds)
        {
            totalUsersInShard += guild.MemberCount;
        }

        string version = $"v{Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}";

        var elapsed = DateTimeOffset.UtcNow - Process.GetCurrentProcess().StartTime;

        var builder = new EmbedBuilder()
            .WithTitle("Fergun Stats")
            .AddField("Operating System", os, true)
            .AddField("\u200b", "\u200b", true)
            .AddField("CPU", cpu, true)
            .AddField("CPU Usage", cpuUsage + "%", true)
            .AddField("\u200b", "\u200b", true)
            .AddField("RAM Usage",
                $"{processRamUsage}MB ({(totalRam == null ? 0 : Math.Round((double)processRamUsage / totalRam.Value * 100, 2))}%) " +
                $"/ {(totalRamUsage == null || totalRam == null ? "?MB" : $"{totalRamUsage}MB ({Math.Round((double)totalRamUsage.Value / totalRam.Value * 100, 2)}%)")} " +
                $"/ {totalRam?.ToString() ?? "?"}MB", true)
            .AddField("Library", $"Discord.Net v{DiscordConfig.Version}", true)
            .AddField("\u200b", "\u200b", true)
            .AddField("BotVersion", version, true)
            .AddField("Total Servers", $"{Context.Client.Guilds.Count} (Shard: {Context.Client.GetShard(shardId).Guilds.Count})", true)
            .AddField("\u200b", "\u200b", true)
            .AddField("Total Users", $"{totalUsers} (Shard: {totalUsersInShard})", true)
            .AddField("Shard ID", shardId, true)
            .AddField("\u200b", "\u200b", true)
            .AddField("Shards", Context.Client.Shards.Count, true)
            .AddField("Uptime", elapsed.Humanize(), true)
            .AddField("\u200b", "\u200b", true)
            .AddField("BotOwner", owner, true);

        builder.WithColor(Color.Orange);

        await FollowupAsync(embed: builder.Build());
    }

    [SlashCommand("translate", "Translates a text.")]
    public async Task Translate([Summary(description: "The text to translate")] string text,
        [Autocomplete(typeof(TranslateAutocompleteHandler))] [Summary(description: "Target language (name, code or alias)")] string target,
        [Autocomplete(typeof(TranslateAutocompleteHandler))] [Summary(description: "Source language (name, code or alias)")] string? source = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await Context.Interaction.RespondWarningAsync("The message must contain text.", true);
            return;
        }

        if (!Language.TryGetLanguage(target, out _))
        {
            await Context.Interaction.RespondWarningAsync($"Invalid target language \"{target}\".", true);
            return;
        }

        if (source != null && !Language.TryGetLanguage(source, out _))
        {
            await Context.Interaction.RespondWarningAsync($"Invalid source language \"{source}\".", true);
            return;
        }

        await DeferAsync();
        ITranslationResult result;

        try
        {
            result = await _translator.TranslateAsync(text, target, source);
        }
        catch (Exception e)
        {
            _logger.LogWarning(new(0, "Translate"), e, "Error translating text {text} ({source} -> {target})", text, source ?? "auto", target);
            await Context.Interaction.FollowupWarning(e.Message);
            return;
        }

        string thumbnailUrl = result.Service switch
        {
            "BingTranslator" => Constants.BingTranslatorLogoUrl,
            "MicrosoftTranslator" => Constants.MicrosoftAzureLogoUrl,
            "YandexTranslator" => Constants.YandexTranslateLogoUrl,
            _ => Constants.GoogleTranslateLogoUrl
        };

        string embedText = $"**Source language** {(source == null ? "**(Detected)**" : "")}\n" +
                           $"{result.SourceLanguage.Name}\n\n" +
                           "**Target language**\n" +
                           $"{result.TargetLanguage.Name}" +
                           "\n\n**Result**\n";

        string translation = result.Translation.Replace('`', '´').Truncate(EmbedBuilder.MaxDescriptionLength - embedText.Length - 6);

        var builder = new EmbedBuilder()
            .WithTitle("Translation result")
            .WithDescription($"{embedText}```{translation}```")
            .WithThumbnailUrl(thumbnailUrl)
            .WithColor(Color.Orange);

        await FollowupAsync(embed: builder.Build());
    }

    [MessageCommand("Translate")]
    public async Task Translate(IUserMessage message)
        => await Translate(message.GetText(), Context.Interaction.GetTwoLetterLanguageCode());

    [SlashCommand("youtube", "Sends a paginator containing YouTube videos.")]
    public async Task YouTube([Autocomplete(typeof(YouTubeAutocompleteHandler))] [Summary(description: "The query.")] string query)
    {
        await DeferAsync();

        var videos = await _searchClient.GetVideosAsync(query).Take(10);

        switch (videos.Count)
        {
            case 0:
                await Context.Interaction.FollowupWarning("No results.");
                break;

            case 1:
                await Context.Interaction.FollowupAsync(videos[0].Url);
                break;

            default:
                var paginator = new StaticPaginatorBuilder()
                    .AddUser(Context.User)
                    .WithPages(videos.Select((x, i) => new PageBuilder { Text = $"{x.Url}\nPage {i + 1} of {videos.Count}" }).ToArray())
                    .WithActionOnCancellation(ActionOnStop.DisableInput)
                    .WithActionOnTimeout(ActionOnStop.DisableInput)
                    .WithFooter(PaginatorFooter.None)
                    .WithFergunEmotes()
                    .Build();

                _ = _interactive.SendPaginatorAsync(paginator, Context.Interaction, TimeSpan.FromMinutes(10), InteractionResponseType.DeferredChannelMessageWithSource);
                break;
        }
    }
}