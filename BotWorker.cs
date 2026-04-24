using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Lavalink4NET;
using Lavalink4NET.DiscordNet;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Events.Players;
using Lavalink4NET.Protocol.Models;
using Lavalink4NET.Clients;
using Lavalink4NET.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NezumiMusicBot;

public class BotWorker
{
    private readonly string _token;
    private readonly int _index;
    private readonly Config _config;
    private readonly ILogger _logger;
    
    public DiscordSocketClient Client { get; private set; }
    public IAudioService AudioService { get; private set; }
    public bool IsBusy { get; set; }
    public int Index => _index;

    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _inactivityTimers = new();
    private readonly ServiceProvider _serviceProvider;

    public BotWorker(string token, int index, Config config, ILoggerFactory loggerFactory)
    {
        _token = token;
        _index = index;
        _config = config;
        _logger = loggerFactory.CreateLogger($"BotWorker#{index}");

        var services = new ServiceCollection();
        
        services.AddLogging(x => x.AddConsole().SetMinimumLevel(LogLevel.Information));
        
        Client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | 
                             GatewayIntents.GuildVoiceStates | 
                             GatewayIntents.GuildMessages | 
                             GatewayIntents.MessageContent,
            LogLevel = LogSeverity.Info
        });

        services.AddSingleton(Client);
        services.AddSingleton<IDiscordClientWrapper, DiscordClientWrapper>();
        services.AddLavalink();
        
        services.Configure<LavalinkNodeOptions>(options =>
        {
            options.BaseAddress = new Uri($"http://{_config.LavalinkUrl}/");
            options.Passphrase = _config.LavalinkPass;
            options.ReadyTimeout = TimeSpan.FromSeconds(10);
        });

        _serviceProvider = services.BuildServiceProvider();
        AudioService = _serviceProvider.GetRequiredService<IAudioService>();
    }

    public async Task StartAsync()
    {
        Client.Log += LogAsync;
        Client.UserVoiceStateUpdated += HandleVoiceStateUpdateAsync;
        
        await Client.LoginAsync(TokenType.Bot, _token);
        await Client.StartAsync();

        // Start Lavalink
        await AudioService.StartAsync();

        AudioService.TrackEnded += HandleTrackEndedAsync;

        Client.Ready += async () =>
        {
            _logger.LogInformation("✅ Bot #{Index} ({Tag}) 起動完了", _index, Client.CurrentUser.ToString());
            
            await Task.Delay(3000);
            foreach (var guild in Client.Guilds)
            {
                var player = await AudioService.Players.GetPlayerAsync(guild.Id);
                var me = guild.GetUser(Client.CurrentUser.Id);
                if (me?.VoiceChannel != null && player == null)
                {
                    await me.ModifyAsync(x => x.Channel = null);
                    _logger.LogInformation("🧹 Bot #{Index} がVCのゴースト接続を強制解除しました。", _index);
                }
            }
        };
    }

    private Task HandleTrackEndedAsync(object sender, TrackEndedEventArgs args)
    {
        if (args.Player is QueuedLavalinkPlayer player)
        {
            if (player.Queue.Count == 0 && player.State == PlayerState.NotPlaying)
            {
                IsBusy = false;
                StartInactivityTimer(player);
            }
        }
        return Task.CompletedTask;
    }

    private void StartInactivityTimer(QueuedLavalinkPlayer player)
    {
        // Cancel existing timer if any
        if (_inactivityTimers.TryRemove(player.GuildId, out var oldCts))
        {
            oldCts.Cancel();
        }

        var cts = new CancellationTokenSource();
        _inactivityTimers[player.GuildId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(3), cts.Token);
                
                // If we reach here, 3 minutes passed without cancellation
                if (player.State == PlayerState.NotPlaying && player.Queue.Count == 0)
                {
                    await player.DisconnectAsync();
                    IsBusy = false;
                    _logger.LogInformation("💤 3分間何も再生されなかったため、Bot #{Index} は退出しました。(Guild: {GuildId})", _index, player.GuildId);
                }
            }
            catch (TaskCanceledException)
            {
                // Timer was cancelled (something started playing or bot left manually)
            }
            finally
            {
                _inactivityTimers.TryRemove(player.GuildId, out _);
            }
        });
    }

    private async Task HandleVoiceStateUpdateAsync(SocketUser user, SocketVoiceState oldState, SocketVoiceState newState)
    {
        // Ignore if user is bot or state change is irrelevant
        if (user.IsBot) return;

        var guildId = oldState.VoiceChannel?.Guild.Id ?? newState.VoiceChannel?.Guild.Id;
        if (guildId == null) return;

        var player = await AudioService.Players.GetPlayerAsync<QueuedLavalinkPlayer>(guildId.Value);
        if (player == null) return;

        var botVoiceChannelId = player.VoiceChannelId;

        // Check if someone left the bot's channel
        if (oldState.VoiceChannel?.Id == botVoiceChannelId && newState.VoiceChannel?.Id != botVoiceChannelId)
        {
            var channel = oldState.VoiceChannel;
            var humanCount = channel.Users.Count(u => !u.IsBot);

            if (humanCount == 0)
            {
                await player.DisconnectAsync();
                IsBusy = false;
                _logger.LogInformation("💤 VCに誰もいなくなったため、Bot #{Index} は退出しました。(Guild: {GuildId})", _index, guildId);
            }
        }
    }

    private Task LogAsync(LogMessage msg)
    {
        _logger.LogInformation(msg.ToString());
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        await AudioService.StopAsync();
        await Client.StopAsync();
        await Client.LogoutAsync();
    }
}
