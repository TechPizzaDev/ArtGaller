using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ArtGaller.Models
{
    public class UploadInfo : UserObject
    {
        [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid UploadId { get; set; }

        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? FormFileName { get; set; }
        public string? ContentType { get; set; }
        public string FileName { get; set; }
        public DateTimeOffset CreationTime { get; set; }

        public string DownloadName => DisplayName ?? FormFileName ?? FileName;
    }
}
