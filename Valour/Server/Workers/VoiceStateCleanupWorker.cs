using StackExchange.Redis;
using Valour.Server.Redis;
using Valour.Server.Services;
using Valour.Shared.Models;

namespace Valour.Server.Workers;

public class VoiceStateCleanupWorker : BackgroundService
{
    private readonly ILogger<VoiceStateCleanupWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnectionMultiplexer _redis;

    public VoiceStateCleanupWorker(
        ILogger<VoiceStateCleanupWorker> logger,
        IServiceProvider serviceProvider,
        IConnectionMultiplexer redis)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _redis = redis;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                await CleanupStaleVoiceStateAsync();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in voice state cleanup worker");
            }
        }
    }

    private async Task CleanupStaleVoiceStateAsync()
    {
        var db = _redis.GetDatabase(RedisDbTypes.Cluster);
        var server = _redis.GetServers().FirstOrDefault();
        if (server is null)
            return;

        var channelKeys = server.Keys(RedisDbTypes.Cluster, "voice:channel:*").ToList();

        using var scope = _serviceProvider.CreateScope();
        var hostedPlanetService = scope.ServiceProvider.GetRequiredService<HostedPlanetService>();
        var coreHub = scope.ServiceProvider.GetRequiredService<CoreHubService>();
        var valourDb = scope.ServiceProvider.GetRequiredService<ValourDb>();

        foreach (var channelKey in channelKeys)
        {
            var channelIdStr = channelKey.ToString().Replace("voice:channel:", "");
            if (!long.TryParse(channelIdStr, out var channelId))
                continue;

            var members = await db.SetMembersAsync(channelKey);
            var staleUserIds = new List<long>();

            foreach (var member in members)
            {
                if (!long.TryParse((string?)member, out var userId))
                {
                    await db.SetRemoveAsync(channelKey, member);
                    continue;
                }

                var userKey = $"voice:user:{userId}";
                var userChannel = await db.StringGetAsync(userKey);

                // User key expired or points to a different channel
                if (!userChannel.HasValue ||
                    (long.TryParse((string?)userChannel, out var currentChannelId) && currentChannelId != channelId))
                {
                    staleUserIds.Add(userId);
                    await db.SetRemoveAsync(channelKey, member);
                }
            }

            if (staleUserIds.Count == 0)
                continue;

            // Look up the channel to find its planet
            var dbChannel = await valourDb.Channels
                .AsNoTracking()
                .Where(x => x.Id == channelId)
                .Select(x => new { x.Id, x.PlanetId })
                .FirstOrDefaultAsync();

            if (dbChannel?.PlanetId is null)
                continue;

            var hostedResult = await hostedPlanetService.TryGetAsync(dbChannel.PlanetId.Value);
            var hosted = hostedResult.HostedPlanet;
            if (hosted is null)
                continue;

            foreach (var userId in staleUserIds)
            {
                hosted.RemoveVoiceParticipant(channelId, userId);
            }

            // Get remaining participants and broadcast
            var remainingMembers = await db.SetMembersAsync(channelKey);
            var remainingUserIds = remainingMembers
                .Select(m => long.TryParse((string?)m, out var id) ? id : 0)
                .Where(id => id > 0)
                .ToList();

            // Clean up empty sets
            if (remainingUserIds.Count == 0)
            {
                await db.KeyDeleteAsync(channelKey);
                hosted.SetVoiceParticipants(channelId, new List<long>());
            }

            coreHub.NotifyVoiceChannelParticipants(dbChannel.PlanetId.Value, new VoiceChannelParticipantsUpdate
            {
                PlanetId = dbChannel.PlanetId.Value,
                ChannelId = channelId,
                UserIds = remainingUserIds
            });

            _logger.LogInformation(
                "Cleaned {Count} stale voice participants from channel {ChannelId}",
                staleUserIds.Count, channelId);
        }
    }
}
