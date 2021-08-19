using System.Collections.Generic;

namespace ArtGaller.Models
{
    public class CollectionViewModel
    {
        public int Offset { get; set; }
        public int Count { get; set; }
        public List<UploadInfo> Items { get; set; }
    }
}
