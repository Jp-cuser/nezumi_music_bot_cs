using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Lavalink4NET;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Rest.Entities.Tracks;
using Lavalink4NET.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace NezumiMusicBot;

public class BotManager
{
    private readonly Config _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly List<BotWorker> _workers = new();
    private BotWorker? _leader;

    public BotManager(Config config, ILoggerFactory loggerFactory)
    {
        _config = config;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("BotManager");
    }

    public async Task InitializeAsync()
    {
        // Initialize Leader
        _leader = new BotWorker(_config.MainToken, 0, _config, _loggerFactory);
        _workers.Add(_leader);

        // Initialize Workers
        for (int i = 0; i < _config.WorkerTokens.Count; i++)
        {
            var worker = new BotWorker(_config.WorkerTokens[i], i + 1, _config, _loggerFactory);
            _workers.Add(worker);
        }

        // Start all workers
        foreach (var worker in _workers)
        {
            await worker.StartAsync();
        }

        if (_leader != null)
        {
            _leader.Client.MessageReceived += HandleCommandAsync;
        }
    }

    private async Task HandleCommandAsync(SocketMessage rawMessage)
    {
        if (rawMessage is not SocketUserMessage message || message.Author.IsBot) return;
        if (!message.Content.StartsWith("n!")) return;
        if (message.Channel is not IGuildChannel guildChannel) return;

        // Whitelist check
        if (!_config.AllowedGuilds.Contains(guildChannel.GuildId)) return;

        var args = message.Content[2..].Trim().Split(" ", StringSplitOptions.RemoveEmptyEntries);
        if (args.Length == 0) return;

        var command = args[0].ToLower();
        var cmdArgs = args.Skip(1).ToArray();

        var guild = (IGuild)guildChannel.Guild;
        var user = (IGuildUser)message.Author;
        var voiceChannel = user.VoiceChannel;
        var socketGuild = (message.Channel as SocketGuildChannel)?.Guild;
        if (socketGuild == null) return;

        // Special command: status
        if (command == "status")
        {
            var status = string.Join("\n", _workers.Select(w => $"Bot #{w.Index}: {(w.IsBusy ? "🔴 使用中" : "🟢 待機中")}"));
            await message.ReplyAsync($"📊 **全ワーカー稼働状況**\n{status}");
            return;
        }

        if (voiceChannel == null)
        {
            await message.ReplyAsync("❌ 先にボイスチャンネルに入ってください。");
            return;
        }

        // Find worker in the same VC or an empty one
        BotWorker? targetBot = null;
        
        // Try to find bot already in the channel
        foreach (var w in _workers)
        {
            var player = await w.AudioService.Players.GetPlayerAsync(guild.Id);
            if (player != null && player.VoiceChannelId == voiceChannel.Id)
            {
                targetBot = w;
                break;
            }
        }

        if (targetBot == null)
        {
            if (new[] { "p", "play", "j", "join" }.Contains(command))
            {
                targetBot = _workers.Find(w => !w.IsBusy);
                if (targetBot == null)
                {
                    await message.ReplyAsync("❌ 現在すべてのワーカーが出撃中です。");
                    return;
                }
            }
            else
            {
                await message.ReplyAsync("⚠️ あなたのVCで稼働中のBotはいません。`n!play` で呼び出してください。");
                return;
            }
        }

        await ExecuteCommandAsync(targetBot, command, cmdArgs, message, voiceChannel);
    }

    private async Task ExecuteCommandAsync(BotWorker bot, string command, string[] args, SocketUserMessage message, IVoiceChannel voiceChannel)
    {
        var playerOptions = new QueuedLavalinkPlayerOptions
        {
            // Initial volume handled in join/play
        };

        var player = await bot.AudioService.Players.GetPlayerAsync<QueuedLavalinkPlayer>(
            voiceChannel.GuildId);

        try
        {
            switch (command)
            {
                case "join": case "j":
                    if (player != null && player.State != PlayerState.Destroyed)
                    {
                        await message.ReplyAsync("すでに入室しています。");
                        return;
                    }
                    player = await bot.AudioService.Players.JoinAsync<QueuedLavalinkPlayer, QueuedLavalinkPlayerOptions>(
                        voiceChannel.GuildId,
                        voiceChannel.Id,
                        (p, op) => ValueTask.FromResult(new QueuedLavalinkPlayer(p)),
                        Microsoft.Extensions.Options.Options.Create(playerOptions));
                    
                    await player.SetVolumeAsync(0.1f);
                    bot.IsBusy = true;
                    await message.ReplyAsync($"✅ **Bot #{bot.Index}** がVCに参加しました。（初期音量: 10%）");
                    break;

                case "play": case "p":
                    if (args.Length == 0)
                    {
                        await message.ReplyAsync("URLまたは曲名を指定してください。");
                        return;
                    }

                    var query = string.Join(" ", args);
                    if (query.Contains("spotify")) query = query.Split('?')[0];

                    var waitingMsg = await message.ReplyAsync("🔍 解析中...");

                    if (player == null || player.State == PlayerState.Destroyed)
                    {
                        player = await bot.AudioService.Players.JoinAsync<QueuedLavalinkPlayer, QueuedLavalinkPlayerOptions>(
                            voiceChannel.GuildId, 
                            voiceChannel.Id,
                            (p, op) => ValueTask.FromResult(new QueuedLavalinkPlayer(p)),
                            Microsoft.Extensions.Options.Options.Create(playerOptions));
                        await player.SetVolumeAsync(0.1f);
                    }

                    var loadResult = await bot.AudioService.Tracks.LoadTracksAsync(query, TrackSearchMode.YouTube);

                    if (loadResult.Tracks.Length == 0)
                    {
                        await waitingMsg.ModifyAsync(m => m.Content = "❌ 楽曲が見つかりませんでした。");
                        return;
                    }

                    if (loadResult.Playlist != null)
                    {
                        foreach (var track in loadResult.Tracks)
                        {
                            await player.Queue.AddAsync(new TrackQueueItem(track));
                        }
                        await waitingMsg.ModifyAsync(m => m.Content = $"✅ **Bot #{bot.Index}** プレイリストから **{loadResult.Tracks.Length}曲** をキューに追加しました！");
                    }
                    else
                    {
                        var track = loadResult.Tracks[0];
                        await player.Queue.AddAsync(new TrackQueueItem(track));
                        await waitingMsg.ModifyAsync(m => m.Content = $"🔍 **Bot #{bot.Index}** `{track.Title}` を予約。");
                    }

                    if (player.State == PlayerState.NotPlaying)
                    {
                        var track = player.Queue.FirstOrDefault();
                        if (track != null)
                        {
                            await player.Queue.RemoveAsync(track);
                            await player.PlayAsync(track.Reference);
                        }
                    }
                    
                    bot.IsBusy = true;
                    break;

                case "skip": case "s":
                    if (player != null) await player.SkipAsync();
                    await message.ReplyAsync($"⏭️ **Bot #{bot.Index}** スキップしました。");
                    break;

                case "stop": case "st":
                    if (player != null)
                    {
                        await player.Queue.ClearAsync();
                        await player.DisconnectAsync();
                    }
                    bot.IsBusy = false;
                    await message.ReplyAsync("⏹️ 停止し、状態をリセットしました。");
                    break;

                case "clear": case "cl":
                    if (player != null)
                    {
                        await player.Queue.ClearAsync();
                        await message.ReplyAsync("🧹 キューをクリア。");
                    }
                    break;

                case "leave": case "dc": case "d":
                    if (player != null) await player.DisconnectAsync();
                    bot.IsBusy = false;
                    await message.ReplyAsync("👋 退出しました。");
                    break;

                case "roff": case "r0": case "loopoff":
                    if (player != null)
                    {
                        player.RepeatMode = TrackRepeatMode.None;
                        await message.ReplyAsync("🔁 リピート: OFF");
                    }
                    break;
                case "rc": case "r1":
                    if (player != null)
                    {
                        player.RepeatMode = TrackRepeatMode.Track;
                        await message.ReplyAsync("🔁 リピート: 単曲 (ONE)");
                    }
                    break;
                case "rq": case "ra":
                    if (player != null)
                    {
                        player.RepeatMode = TrackRepeatMode.Queue;
                        await message.ReplyAsync("🔁 リピート: 全曲 (ALL)");
                    }
                    break;

                case "volume": case "vol": case "v":
                    if (player == null)
                    {
                        await message.ReplyAsync("❌ 現在再生中のBotがいません。");
                        return;
                    }
                    if (args.Length == 0)
                    {
                        await message.ReplyAsync($"🔊 現在の音量は **{(int)(player.Volume * 100)}%** です。");
                        return;
                    }
                    if (int.TryParse(args[0], out var vol) && vol >= 0 && vol <= 200)
                    {
                        await player.SetVolumeAsync(vol / 100f);
                        await message.ReplyAsync($"🔊 **Bot #{bot.Index}** の音量を **{vol}%** に変更しました！");
                    }
                    else
                    {
                        await message.ReplyAsync("❌ 音量は `0` から `200` の間の数字で指定してください。");
                    }
                    break;
                
                case "queue": case "q":
                    if (player == null || player.Queue.Count == 0)
                    {
                        await message.ReplyAsync("キューは空です。");
                        return;
                    }
                    var qMsg = string.Join("\n", player.Queue.Take(10).Select((t, i) => $"{i + 1}. {t.Track?.Title}"));
                    await message.ReplyAsync($"📋 **Bot #{bot.Index} キュー:**\n{qMsg}");
                    break;

                case "shuffle": case "sh":
                    if (player != null)
                    {
                        player.Queue.Shuffle();
                        await message.ReplyAsync($"🔀 **Bot #${bot.Index}** シャッフル。");
                    }
                    break;

                case "pause": case "pa":
                    if (player != null) await player.PauseAsync();
                    await message.ReplyAsync("⏸️ 一時停止しました。");
                    break;

                case "resume": case "r":
                    if (player != null) await player.ResumeAsync();
                    await message.ReplyAsync("▶️ 再開しました。");
                    break;

                case "repeat": case "loop": case "rp":
                    if (player == null) return;
                    var sub = args.Length > 0 ? args[0].ToLower() : "";
                    if (string.IsNullOrEmpty(sub))
                    {
                        await message.ReplyAsync($"🔁 現在のモード: **{player.RepeatMode}**");
                    }
                    else if (new[] { "off", "roff", "r0", "loopoff" }.Contains(sub))
                    {
                        player.RepeatMode = TrackRepeatMode.None;
                        await message.ReplyAsync("🔁 リピート: OFF");
                    }
                    else if (new[] { "one", "rc", "r1" }.Contains(sub))
                    {
                        player.RepeatMode = TrackRepeatMode.Track;
                        await message.ReplyAsync("🔁 リピート: 単曲 (ONE)");
                    }
                    else if (new[] { "all", "rq", "ra" }.Contains(sub))
                    {
                        player.RepeatMode = TrackRepeatMode.Queue;
                        await message.ReplyAsync("🔁 リピート: 全曲 (ALL)");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing command {Command}", command);
            await message.ReplyAsync("❌ エラーが発生しました。");
        }
    }
}
