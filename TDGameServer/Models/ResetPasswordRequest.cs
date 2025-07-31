using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TDGameServer.Models
{
    public class ResetPasswordRequest
    {
        public required string Command { get; set; }    // "reset-password"
        public required string Username { get; set; }
        public required string Email { get; set; }
    }
}
