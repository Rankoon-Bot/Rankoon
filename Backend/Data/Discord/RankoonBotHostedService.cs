using Discord;
using Discord.WebSocket;

namespace Rankoon.Data.Discord;

/// <summary>Owns the Discord gateway lifecycle so module event handlers live with the web host.</summary>
public sealed class RankoonBotHostedService(DiscordShardedClient client, Microsoft.Extensions.Options.IOptions<Rankoon.Data.Auth.DiscordSettings> settings, Rankoon.Data.Auth.IBotOperatorAccessService botOperatorAccess, ILogger<RankoonBotHostedService> logger) : IHostedLifecycleService
{
    private readonly TaskCompletionSource<bool> startup = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<bool> Startup => startup.Task;

    private Task OnLogAsync(LogMessage message)
    {
        if (message.Exception != null)
        {
            logger.LogError(message.Exception, "Discord {Source}: {Message}", message.Source, message.Message ?? "No message provided");
        }
        else
        {
            logger.LogInformation("Discord {Source}: {Message}", message.Source, message.Message ?? "No message provided");
        }

        return Task.CompletedTask;
    }

    private Task OnShardConnectedAsync(DiscordSocketClient shard)
    {
        logger.LogInformation("Discord shard {ShardId} connected", shard.ShardId);
        return Task.CompletedTask;
    }

    private Task OnShardDisconnectedAsync(Exception exception, DiscordSocketClient shard)
    {
        if (exception.ToString().Contains("close 4014", StringComparison.Ordinal))
        {
            logger.LogError(exception, "Discord shard {ShardId} was rejected because a requested Gateway Intent is not enabled in the Discord Developer Portal", shard.ShardId);
            return Task.CompletedTask;
        }

        logger.LogWarning(exception, "Discord shard {ShardId} disconnected; Discord.Net will reconnect it automatically", shard.ShardId);
        return Task.CompletedTask;
    }

    private Task OnShardReadyAsync(DiscordSocketClient shard)
    {
        logger.LogInformation("Discord shard {ShardId} is ready", shard.ShardId);
        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => StartDiscordAsync(cancellationToken);
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task StartDiscordAsync(CancellationToken cancellationToken)
    {
        client.Log += OnLogAsync;
        client.ShardConnected += OnShardConnectedAsync;
        client.ShardDisconnected += OnShardDisconnectedAsync;
        client.ShardReady += OnShardReadyAsync;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(settings.Value.BotToken))
            {
                logger.LogError("Discord bot was not started because Discord:BotToken is empty");
                startup.TrySetResult(false);
                return;
            }

            await client.LoginAsync(TokenType.Bot, settings.Value.BotToken);
            await client.StartAsync();
            await botOperatorAccess.WarmAsync(cancellationToken);
            startup.TrySetResult(true);
            logger.LogInformation("Discord bot started");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            startup.TrySetResult(false);
            UnsubscribeEvents();
        }
        catch (Exception exception)
        {
            startup.TrySetResult(false);
            UnsubscribeEvents();
            logger.LogError(exception, "Discord bot could not be started; the HTTP backend remains available");
        }
    }

    private void UnsubscribeEvents()
    {
        client.Log -= OnLogAsync;
        client.ShardConnected -= OnShardConnectedAsync;
        client.ShardDisconnected -= OnShardDisconnectedAsync;
        client.ShardReady -= OnShardReadyAsync;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await client.StopAsync();
            await client.LogoutAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Discord bot shutdown failed");
        }
        finally
        {
            UnsubscribeEvents();
        }
    }

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
