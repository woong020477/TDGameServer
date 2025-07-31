using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TDGameServer.Models
{
    public class ChangePasswordRequest
    {
        public required string Command { get; set; } = "change-password";
        public required string Username { get; set; }
        public required string Email { get; set; }
        public required string Password { get; set; }          // 현재 비밀번호
        public required string NewPassword { get; set; }       // 변경할 비밀번호
    }
}
