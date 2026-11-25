using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DAL.Migrations;

[DbContext(typeof(StaticXDbContext))]
partial class StaticXDbContextModelSnapshot : ModelSnapshot
{
    // This delegates only to frozen migration metadata, never to the current runtime model.
    protected override void BuildModel(ModelBuilder modelBuilder) => AddExerciseDuration.BuildExerciseDurationModel(modelBuilder);
}
