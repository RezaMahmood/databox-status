using System;

namespace databox_status
{
    [Serializable]
    public class DataBoxStatus
    {
        public string OrderName { get; set; }
        public string Status { get; set; }
        public string ResourceGroup { get; set; }
    }
}