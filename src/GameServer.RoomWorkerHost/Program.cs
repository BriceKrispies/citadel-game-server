using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Transport;

// Child worker host: runs ONE authoritative room's loop in this process, driven by the parent
// over a line-delimited JSON RPC on stdio (parent -> our stdin, our stdout -> parent). Closing
// our stdin (EOF) is the cooperative stop signal. NOTHING else may write to stdout, or it would
// corrupt the protocol stream; diagnostics go to stderr.
//
// args: [0] roomId  [1] gameId
var roomId = args.Length > 0 ? args[0] : "room";
var gameId = args.Length > 1 ? args[1] : string.Empty;

// Mirrors the host's game selection. A real deployment would resolve this from the control-plane
// catalog; here it matches GameServer.Host's GameFor so the child runs the same game.
IGameSimulation game = gameId switch
{
    "grid-walk" => new GridWalkGame(),
    _ => new MoveRightGame(),
};

var room = new GameRoom(new RoomId(roomId), game, new LogicalSimulationClock(), new SeededRandomSource());
new RoomHost(room).Run(Console.In, Console.Out);
