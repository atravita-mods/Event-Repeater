using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EventRepeater
{
    public class ThingsToForget
    {
        public List<int> RepeatEvents { get; set; } = new();
        public List<string> RepeatMail { get; set; } = new();

        public List<int> RepeatResponse { get; set; } = new();
    }
}