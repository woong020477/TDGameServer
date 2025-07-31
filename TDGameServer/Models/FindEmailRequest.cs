using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TDGameServer.Models
{
    public class FindEmailRequest
    {
        public required string Command { get; set; }    // "find-email"
        public required string Username { get; set; }
        public required string Password { get; set; }
    }
}
