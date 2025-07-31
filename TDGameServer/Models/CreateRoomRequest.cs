using System;

namespace TDGameServer.Models
{
    public class CreateRoomRequest
    {
        public required string Command { get; set; }      // "create-room"
        public required int HostId { get; set; }
        public required string Host { get; set; }
        public required string RoomName { get; set; }
        public required string Difficulty { get; set; }
    }
}