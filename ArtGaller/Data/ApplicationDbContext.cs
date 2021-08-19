using ArtGaller.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ArtGaller.Data
{
    public class ApplicationDbContext : IdentityDbContext
    {
        public DbSet<UploadInfo> UploadInfos { get; set; }

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {

        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<UploadInfo>()
                .HasKey(x => new { x.UserId, x.UploadId });
        }
    }
}
