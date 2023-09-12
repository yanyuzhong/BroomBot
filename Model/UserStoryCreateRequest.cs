using System;
using System.Collections.Generic;
using System.Text;

namespace BroomBot.Model
{
    public class UserStoryCreateRequest
    {
        public string? from { get; set; }
        public string op { get; set; }
        public string path { get; set; }
        public string value { get; set; }
    }
}
