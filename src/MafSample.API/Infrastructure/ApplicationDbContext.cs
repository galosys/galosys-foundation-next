using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;

namespace MafSample.API.Infrastructure
{
    [DbContext("Default")]
    public class ApplicationDbContext
        : DbContext<ApplicationDbContext>
    {
        public ApplicationDbContext([NotNull] DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }
    }
}
