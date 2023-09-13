using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace BroomBot.Model
{
    public class WorkItemCreateResponse
    {
        [JsonProperty("_links")]
        public WorkItemLink Link { get; set; }
    }

    public class WorkItemLink
    {
        public HREF html { get; set; }
    }

    public class HREF
    {
        public string href { get; set; }
    }
}
