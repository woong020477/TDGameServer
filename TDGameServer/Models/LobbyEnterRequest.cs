using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TDGameServer.Models
{
    public class LobbyEnterRequest
    {
        public required string Command { get; set; }
        public required int UserId { get; set; }
        public required string Username { get; set; }
    }
}
