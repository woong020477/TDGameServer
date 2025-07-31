using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TDGameServer.Models
{
    public class LogoutRequest
    {
        public required string Command { get; set; }
        public int UserId { get; set; }
    }
}
