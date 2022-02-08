using System;
using System.Collections.Generic;
using SixLabors.ImageSharp;

namespace ArtGaller.Models
{
    public class CollectionViewModel
    {
        public int Offset { get; set; }
        public int Count { get; set; }
        public List<UploadInfo> Items { get; set; }
        public Dictionary<Guid, IImageInfo> ThumbnailInfos { get; set; }
    }
}
