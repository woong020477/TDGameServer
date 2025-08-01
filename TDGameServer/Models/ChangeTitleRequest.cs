using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TDGameServer.Models
{
    public class ChangeTitleRequest
    {
        public int UserId { get; set; }
        public int TitleId { get; set; }
    }

}
