using System.Collections.Concurrent;
using GameServer.Protocol;

namespace GameServer.Matchmaking;

/// <summary>
/// A deterministic <see cref="IRoomAllocator"/> for tests: hands out sequential room ids per scope, no
/// real cluster. Records every allocation so a test can assert how many rooms were created.
/// </summary>
public sealed class FakeRoomAllocator : IRoomAllocator
{
    private readonly bool _atCapacity;
    private int _next;

    public FakeRoomAllocator(bool atCapacity = false) => _atCapacity = atCapacity;

    public List<(MatchScope Scope, RoomId Room)> Allocations { get; } = new();

    public RoomId? Allocate(MatchScope scope)
    {
        if (_atCapacity)
        {
            return null;
        }

        var room = new RoomId($"room-{Interlocked.Increment(ref _next)}");
        Allocations.Add((scope, room));
        return room;
    }
}

/// <summary>
/// A deterministic <see cref="IMatchJoinTokenIssuer"/> for tests: a token string that encodes the
/// scope/room/player so a test can prove the token was minted for the right (isolated) target without
/// pulling in real HS256 signing.
/// </summary>
public sealed class FakeJoinTokenIssuer : IMatchJoinTokenIssuer
{
    public ConcurrentBag<(MatchScope Scope, RoomId Room, PlayerId Player)> Issued { get; } = new();

    public string IssueJoinToken(MatchScope scope, RoomId room, PlayerId player)
    {
        Issued.Add((scope, room, player));
        return $"token:{scope.TenantId.Value}/{scope.GameId.Value}/v{scope.GameVersion}/{room.Value}/{player.Value}";
    }
}
