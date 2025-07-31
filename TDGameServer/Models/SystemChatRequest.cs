using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TDGameServer.Models
{
    public class SystemChatRequest
    {
        public required string Command { get; set; }
        public required string Sender { get; set; }
        public required string Message { get; set; }
    }
}
