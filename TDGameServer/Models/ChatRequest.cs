using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TDGameServer.Models
{
    public class ChatRequest
    {
        public required string Command { get; set; }
        public required int UserId { get; set; }
        public required string Username { get; set; }
        public required string ChatType { get; set; }       // "All", "Room", "Whisper"
        public string? TargetUsername { get; set; }         // Whisper 대상이 없을 경우 null
        public required string Message { get; set; }
    }
}
