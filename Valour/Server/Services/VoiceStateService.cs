using StackExchange.Redis;
using Valour.Server.Redis;
using Valour.Shared.Models;

namespace Valour.Server.Services;

public class VoiceStateService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly HostedPlanetService _hostedPlanetService;
    private readonly CoreHubService _coreHub;
    private readonly ILogger<VoiceStateService> _logger;

    private static readonly TimeSpan UserKeyTtl = TimeSpan.FromSeconds(120);

    public VoiceStateService(
        IConnectionMultiplexer redis,
        HostedPlanetService hostedPlanetService,
        CoreHubService coreHub,
        ILogger<VoiceStateService> logger)
    {
        _redis = redis;
        _hostedPlanetService = hostedPlanetService;
        _coreHub = coreHub;
        _logger = logger;
    }

    /// <summary>
    /// Called when a user joins a voice channel. Returns the previous channel ID if the user was already in one.
    /// </summary>
    public async Task<long?> UserJoinVoiceChannelAsync(long userId, long channelId, long planetId)
    {
        var db = _redis.GetDatabase(RedisDbTypes.Cluster);
        var userKey = $"voice:user:{userId}";
        long? previousChannelId = null;

        // Check if user is already in a channel
        var existing = await db.StringGetAsync(userKey);
        if (existing.HasValue && long.TryParse((string?)existing, out var oldChannelId) && oldChannelId != channelId)
        {
            previousChannelId = oldChannelId;

            // Remove from old channel set
            await db.SetRemoveAsync($"voice:channel:{oldChannelId}", userId);

            // Update HostedPlanet for old channel (may be on same planet)
            var oldHosted = await _hostedPlanetService.GetRequiredAsync(planetId);
            if (oldHosted is not null)
            {
                oldHosted.RemoveVoiceParticipant(oldChannelId, userId);
                BroadcastChannelParticipants(oldHosted, oldChannelId, planetId);
            }
        }

        // Set user key with TTL
        await db.StringSetAsync(userKey, channelId, UserKeyTtl);

        // Add to channel set
        await db.SetAddAsync($"voice:channel:{channelId}", userId);

        // Update HostedPlanet
        var hosted = await _hostedPlanetService.GetRequiredAsync(planetId);
        if (hosted is not null)
        {
            hosted.AddVoiceParticipant(channelId, userId);
            BroadcastChannelParticipants(hosted, channelId, planetId);
        }

        return previousChannelId;
    }

    /// <summary>
    /// Called when a user leaves a voice channel.
    /// </summary>
    public async Task UserLeaveVoiceChannelAsync(long userId, long channelId, long planetId)
    {
        var db = _redis.GetDatabase(RedisDbTypes.Cluster);
        var userKey = $"voice:user:{userId}";

        // Only delete user key if it still points to this channel
        var existing = await db.StringGetAsync(userKey);
        if (existing.HasValue && long.TryParse((string?)existing, out var currentChannelId) && currentChannelId == channelId)
        {
            await db.KeyDeleteAsync(userKey);
        }

        // Remove from channel set
        await db.SetRemoveAsync($"voice:channel:{channelId}", userId);

        // Update HostedPlanet
        var hosted = await _hostedPlanetService.GetRequiredAsync(planetId);
        if (hosted is not null)
        {
            hosted.RemoveVoiceParticipant(channelId, userId);
            BroadcastChannelParticipants(hosted, channelId, planetId);
        }
    }

    /// <summary>
    /// Refreshes the TTL on a user's voice key (heartbeat).
    /// </summary>
    public async Task RefreshVoiceHeartbeatAsync(long userId)
    {
        var db = _redis.GetDatabase(RedisDbTypes.Cluster);
        var userKey = $"voice:user:{userId}";
        await db.KeyExpireAsync(userKey, UserKeyTtl);
    }

    /// <summary>
    /// Gets the list of user IDs in a voice channel from Redis.
    /// </summary>
    public async Task<List<long>> GetChannelParticipantsAsync(long channelId)
    {
        var db = _redis.GetDatabase(RedisDbTypes.Cluster);
        var members = await db.SetMembersAsync($"voice:channel:{channelId}");
        var result = new List<long>(members.Length);
        foreach (var member in members)
        {
            if (long.TryParse((string?)member, out var userId))
                result.Add(userId);
        }
        return result;
    }

    private void BroadcastChannelParticipants(HostedPlanet hosted, long channelId, long planetId)
    {
        var channel = hosted.GetChannel(channelId);
        if (channel is null)
            return;

        var participants = hosted.GetAllVoiceParticipants();
        var userIds = participants.TryGetValue(channelId, out var list) ? list : new List<long>();

        _coreHub.NotifyVoiceChannelParticipants(planetId, new VoiceChannelParticipantsUpdate
        {
            PlanetId = planetId,
            ChannelId = channelId,
            UserIds = userIds
        });
    }
}
